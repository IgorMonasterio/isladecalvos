using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("Isla de Calvos", "Igor Monasterio", "1.0.0")]
    [Description("Baldness system for the Isla de Calvos Rust server: being bald is glory, hair is a curse.")]
    public class IslaDeCalvos : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "isladecalvos.admin";
        private const int MinBaldness = 0;
        private const int MaxBaldness = 100;
        private const int TopCount = 10;
        private const float SurvivalTickSeconds = 60f;

        private Configuration config;
        private StoredData storedData;
        private bool dataDirty;

        // Last counted kill per killer -> victim, used by the anti-farm cooldown. In memory only.
        private readonly Dictionary<ulong, Dictionary<ulong, DateTime>> killCooldowns = new Dictionary<ulong, Dictionary<ulong, DateTime>>();

        // Who downed a player, so a bleed-out death is still credited to them. Cleared on recovery or death.
        private readonly Dictionary<ulong, WoundRecord> woundRecords = new Dictionary<ulong, WoundRecord>();

        private class WoundRecord
        {
            public BasePlayer Attacker;
            public bool Headshot;
        }

        #endregion

        #region Configuration

        private class Configuration
        {
            [JsonProperty("Baldness gained per player kill")]
            public int KillReward = 3;

            [JsonProperty("Baldness gained per headshot kill (instead of the normal kill reward)")]
            public int HeadshotKillReward = 7;

            [JsonProperty("Baldness gained per survival interval")]
            public int SurvivalReward = 1;

            [JsonProperty("Survival interval (minutes alive and connected)")]
            public int SurvivalIntervalMinutes = 30;

            [JsonProperty("Baldness lost on death")]
            public int DeathPenalty = 5;

            [JsonProperty("Extra baldness lost when the death is a headshot")]
            public int HeadshotDeathExtraPenalty = 3;

            [JsonProperty("Kill cooldown per victim (minutes)")]
            public int KillCooldownMinutes = 30;

            [JsonProperty("Count kills of NPC players (scientists, etc.)")]
            public bool CountNpcKills = false;

            [JsonProperty("Announce when a player reaches 100% baldness")]
            public bool AnnounceSupremeBaldness = true;

            [JsonProperty("Announce when a player drops to a lower title")]
            public bool AnnounceTitleDrop = true;

            [JsonProperty("Reset baldness on map wipe (stats are kept)")]
            public bool ResetBaldnessOnWipe = false;

            [JsonProperty("Titles (minimum baldness -> title)")]
            public List<TitleTier> Titles = new List<TitleTier>
            {
                new TitleTier { MinBaldness = 0, Name = "Aspirante a Calvo" },
                new TitleTier { MinBaldness = 15, Name = "Calvo Novato" },
                new TitleTier { MinBaldness = 30, Name = "Calvo Profesional" },
                new TitleTier { MinBaldness = 50, Name = "Calvo Veterano" },
                new TitleTier { MinBaldness = 70, Name = "Maestro de la Calvicie" },
                new TitleTier { MinBaldness = 90, Name = "Gran Calvo" },
                new TitleTier { MinBaldness = 100, Name = "Dios Calvo" }
            };
        }

        private class TitleTier
        {
            [JsonProperty("Minimum baldness")]
            public int MinBaldness;

            [JsonProperty("Title")]
            public string Name;
        }

        protected override void LoadDefaultConfig() => config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null)
                {
                    throw new JsonException("Config is empty");
                }
            }
            catch (Exception ex)
            {
                PrintWarning($"Config file is invalid ({ex.Message}); using default values.");
                LoadDefaultConfig();
            }

            ValidateConfig();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(config, true);

        private void ValidateConfig()
        {
            if (config.Titles == null)
            {
                config.Titles = new List<TitleTier>();
            }

            config.Titles.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Name));
            if (config.Titles.Count == 0)
            {
                PrintWarning("No titles configured; restoring the default titles.");
                config.Titles = new Configuration().Titles;
            }

            config.Titles = config.Titles.OrderBy(t => t.MinBaldness).ToList();
            config.SurvivalIntervalMinutes = Math.Max(1, config.SurvivalIntervalMinutes);
            config.KillCooldownMinutes = Math.Max(0, config.KillCooldownMinutes);
        }

        #endregion

        #region Data

        private class StoredData
        {
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
        }

        private class PlayerData
        {
            public string Name = string.Empty;
            public int Baldness;
            public int Kills;
            public int Deaths;
            public int HeadshotKills;

            // Seconds alive and connected since the last survival reward (or since the last death).
            public float SurvivalSeconds;
        }

        private void LoadData()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch (Exception ex)
            {
                PrintError($"Data file is invalid ({ex.Message}); starting with empty data.");
                storedData = null;
            }

            if (storedData == null)
            {
                storedData = new StoredData();
            }

            if (storedData.Players == null)
            {
                storedData.Players = new Dictionary<ulong, PlayerData>();
            }
        }

        private void SaveData()
        {
            if (!dataDirty || storedData == null)
            {
                return;
            }

            Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);
            dataDirty = false;
        }

        private PlayerData GetOrCreateData(BasePlayer player)
        {
            ulong id = (ulong)player.userID;
            if (!storedData.Players.TryGetValue(id, out PlayerData data))
            {
                data = new PlayerData();
                storedData.Players[id] = data;
                dataDirty = true;
            }

            if (!string.IsNullOrEmpty(player.displayName) && data.Name != player.displayName)
            {
                data.Name = player.displayName;
                dataDirty = true;
            }

            return data;
        }

        #endregion

        #region Localization

        protected override void LoadDefaultMessages()
        {
            var messages = new Dictionary<string, string>
            {
                ["MyBaldness"] = "Tu calvicie: <color=#f0c040>{0}%</color> — {1}",
                ["TopHeader"] = "🧑‍🦲 Los {0} más calvos de la isla:",
                ["TopLine"] = "{0}. {1} — {2}% ({3})",
                ["TopEmpty"] = "Aún no hay nadie en el ranking. La isla está llena de pelo.",
                ["SupremeBaldness"] = "🧑‍🦲 {0} HA ALCANZADO LA CALVICIE SUPREMA",
                ["TitleDrop"] = "⚠️ A {0} le está saliendo pelo (ahora es {1})",
                ["NoPermission"] = "No tienes permiso para usar este comando.",
                ["AdminUsage"] = "Uso: /calvoadmin set <jugador> <valor> | /calvoadmin reset <jugador>",
                ["AdminInvalidValue"] = "El valor tiene que ser un número entre {0} y {1}.",
                ["PlayerNotFound"] = "No se ha encontrado ningún jugador con '{0}'.",
                ["PlayerAmbiguous"] = "Hay {0} jugadores que coinciden con '{1}'. Sé más concreto o usa el SteamID.",
                ["AdminSet"] = "Calvicie de {0} fijada en {1}%.",
                ["AdminReset"] = "Calvicie de {0} reseteada a {1}%."
            };

            // Spanish is registered as the default ("en") set too: Oxide assigns each player the language
            // of their game client (usually "en") and falls back to "en", so this keeps Spanish as the default.
            lang.RegisterMessages(messages, this);
            lang.RegisterMessages(messages, this, "es");
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, userId);
            return args.Length > 0 ? string.Format(message, args) : message;
        }

        private void Reply(BasePlayer player, string key, params object[] args) =>
            SendReply(player, Lang(key, player.UserIDString, args));

        private void Broadcast(string key, params object[] args) => PrintToChat(Lang(key, null, args));

        #endregion

        #region Lifecycle Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player))
                {
                    GetOrCreateData(player);
                }
            }

            timer.Every(SurvivalTickSeconds, SurvivalTick);
        }

        private void OnServerSave() => SaveData();

        private void Unload() => SaveData();

        private void OnNewSave(string filename)
        {
            if (!config.ResetBaldnessOnWipe || storedData == null)
            {
                return;
            }

            foreach (PlayerData data in storedData.Players.Values)
            {
                data.Baldness = MinBaldness;
                data.SurvivalSeconds = 0f;
            }

            killCooldowns.Clear();
            dataDirty = true;
            SaveData();
            Puts("Map wipe detected: baldness reset for all players (stats kept).");
        }

        #endregion

        #region Game Hooks

        private void OnPlayerConnected(BasePlayer player)
        {
            if (IsRealPlayer(player))
            {
                GetOrCreateData(player);
            }
        }

        private void OnPlayerWound(BasePlayer player, HitInfo info)
        {
            if (player == null)
            {
                return;
            }

            BasePlayer attacker = info?.InitiatorPlayer;
            if (attacker == null || attacker == player)
            {
                woundRecords.Remove((ulong)player.userID);
                return;
            }

            woundRecords[(ulong)player.userID] = new WoundRecord { Attacker = attacker, Headshot = info.isHeadshot };
        }

        private void OnPlayerRecovered(BasePlayer player)
        {
            if (player != null)
            {
                woundRecords.Remove((ulong)player.userID);
            }
        }

        private void OnPlayerDeath(BasePlayer victim, HitInfo info)
        {
            if (victim == null)
            {
                return;
            }

            ulong victimId = (ulong)victim.userID;
            BasePlayer killer = info?.InitiatorPlayer;
            bool headshot = info != null && info.isHeadshot;

            // A downed player who bleeds out (no attacker) or gives up (self-inflicted) is credited to whoever downed them.
            if (woundRecords.TryGetValue(victimId, out WoundRecord wound))
            {
                woundRecords.Remove(victimId);
                if ((killer == null || killer == victim) && wound.Attacker != null)
                {
                    killer = wound.Attacker;
                    headshot = wound.Headshot;
                }
            }

            bool victimIsReal = IsRealPlayer(victim);

            if (victimIsReal)
            {
                PlayerData victimData = GetOrCreateData(victim);
                victimData.Deaths++;
                victimData.SurvivalSeconds = 0f;
                dataDirty = true;

                int penalty = config.DeathPenalty + (headshot ? config.HeadshotDeathExtraPenalty : 0);
                ChangeBaldness(victimData, -penalty, true);
            }

            // Suicide (or no killer at all) only counts as a death.
            if (killer == null || killer == victim || !IsRealPlayer(killer))
            {
                return;
            }

            if (!victimIsReal && !config.CountNpcKills)
            {
                return;
            }

            PlayerData killerData = GetOrCreateData(killer);
            killerData.Kills++;
            if (headshot)
            {
                killerData.HeadshotKills++;
            }

            dataDirty = true;

            if (victimIsReal && !IsKillRewardable((ulong)killer.userID, victim))
            {
                return;
            }

            ChangeBaldness(killerData, headshot ? config.HeadshotKillReward : config.KillReward, true);
        }

        #endregion

        #region Commands

        [ChatCommand("calvo")]
        private void CmdCalvo(BasePlayer player, string command, string[] args)
        {
            if (!IsRealPlayer(player))
            {
                return;
            }

            PlayerData data = GetOrCreateData(player);
            Reply(player, "MyBaldness", data.Baldness, GetTitle(data.Baldness));
        }

        [ChatCommand("calvos")]
        private void CmdCalvos(BasePlayer player, string command, string[] args)
        {
            List<PlayerData> top = storedData.Players.Values
                .OrderByDescending(d => d.Baldness)
                .ThenByDescending(d => d.Kills)
                .Take(TopCount)
                .ToList();

            if (top.Count == 0)
            {
                Reply(player, "TopEmpty");
                return;
            }

            var lines = new List<string> { Lang("TopHeader", player.UserIDString, TopCount) };
            for (int i = 0; i < top.Count; i++)
            {
                lines.Add(Lang("TopLine", player.UserIDString, i + 1, top[i].Name, top[i].Baldness, GetTitle(top[i].Baldness)));
            }

            SendReply(player, string.Join("\n", lines));
        }

        [ChatCommand("calvoadmin")]
        private void CmdCalvoAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                Reply(player, "NoPermission");
                return;
            }

            if (args.Length < 2)
            {
                Reply(player, "AdminUsage");
                return;
            }

            string action = args[0].ToLowerInvariant();
            int value;
            if (action == "set")
            {
                if (args.Length < 3 || !int.TryParse(args[2], out value) || value < MinBaldness || value > MaxBaldness)
                {
                    Reply(player, "AdminInvalidValue", MinBaldness, MaxBaldness);
                    return;
                }
            }
            else if (action == "reset")
            {
                value = MinBaldness;
            }
            else
            {
                Reply(player, "AdminUsage");
                return;
            }

            List<KeyValuePair<ulong, PlayerData>> matches = FindStoredPlayers(args[1]);
            if (matches.Count == 0)
            {
                Reply(player, "PlayerNotFound", args[1]);
                return;
            }

            if (matches.Count > 1)
            {
                Reply(player, "PlayerAmbiguous", matches.Count, args[1]);
                return;
            }

            PlayerData target = matches[0].Value;

            // Admin changes are silent: no global announcements.
            ChangeBaldness(target, value - target.Baldness, false);
            Reply(player, action == "set" ? "AdminSet" : "AdminReset", target.Name, target.Baldness);
            Puts($"{player.displayName} ({player.UserIDString}) {action} baldness of {target.Name} ({matches[0].Key}) to {target.Baldness}%.");
        }

        #endregion

        #region Helpers

        private static bool IsRealPlayer(BasePlayer player) => player != null && player.userID.IsSteamId();

        private bool IsKillRewardable(ulong killerId, BasePlayer victim)
        {
            // Killing sleepers or disconnected players is not glorious.
            if (victim.IsSleeping() || !victim.IsConnected)
            {
                return false;
            }

            if (!killCooldowns.TryGetValue(killerId, out Dictionary<ulong, DateTime> victims))
            {
                victims = new Dictionary<ulong, DateTime>();
                killCooldowns[killerId] = victims;
            }

            ulong victimId = (ulong)victim.userID;
            DateTime now = DateTime.UtcNow;
            if (victims.TryGetValue(victimId, out DateTime lastKill) && (now - lastKill).TotalMinutes < config.KillCooldownMinutes)
            {
                return false;
            }

            victims[victimId] = now;
            return true;
        }

        private void SurvivalTick()
        {
            float interval = config.SurvivalIntervalMinutes * 60f;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (!IsRealPlayer(player) || !player.IsConnected || player.IsDead() || player.IsSleeping())
                {
                    continue;
                }

                PlayerData data = GetOrCreateData(player);
                data.SurvivalSeconds += SurvivalTickSeconds;
                dataDirty = true;

                if (data.SurvivalSeconds >= interval)
                {
                    data.SurvivalSeconds -= interval;
                    ChangeBaldness(data, config.SurvivalReward, true);
                }
            }
        }

        private void ChangeBaldness(PlayerData data, int delta, bool announce)
        {
            int oldValue = data.Baldness;
            int newValue = Math.Max(MinBaldness, Math.Min(MaxBaldness, oldValue + delta));
            if (newValue == oldValue)
            {
                return;
            }

            data.Baldness = newValue;
            dataDirty = true;

            if (!announce)
            {
                return;
            }

            if (config.AnnounceSupremeBaldness && newValue == MaxBaldness && oldValue < MaxBaldness)
            {
                Broadcast("SupremeBaldness", data.Name);
            }

            if (config.AnnounceTitleDrop && GetTierIndex(newValue) < GetTierIndex(oldValue))
            {
                Broadcast("TitleDrop", data.Name, GetTitle(newValue));
            }
        }

        private int GetTierIndex(int baldness)
        {
            int index = 0;
            for (int i = 0; i < config.Titles.Count; i++)
            {
                if (baldness >= config.Titles[i].MinBaldness)
                {
                    index = i;
                }
            }

            return index;
        }

        private string GetTitle(int baldness) => config.Titles[GetTierIndex(baldness)].Name;

        private List<KeyValuePair<ulong, PlayerData>> FindStoredPlayers(string query)
        {
            if (ulong.TryParse(query, out ulong id) && storedData.Players.TryGetValue(id, out PlayerData byId))
            {
                return new List<KeyValuePair<ulong, PlayerData>> { new KeyValuePair<ulong, PlayerData>(id, byId) };
            }

            List<KeyValuePair<ulong, PlayerData>> exact = storedData.Players
                .Where(p => string.Equals(p.Value.Name, query, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count > 0)
            {
                return exact;
            }

            return storedData.Players
                .Where(p => p.Value.Name != null && p.Value.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
        }

        #endregion
    }
}
