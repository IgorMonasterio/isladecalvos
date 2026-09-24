using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Isla de Calvos", "Igor Monasterio", "1.1.0")]
    [Description("Baldness system for the Isla de Calvos Rust server: being bald is glory, hair is a curse.")]
    public class IslaDeCalvos : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "isladecalvos.admin";
        private const long MinBaldness = 0;
        private const int TopCount = 10;
        private const float SurvivalTickSeconds = 60f;

        private Configuration config;
        private StoredData storedData;
        private bool dataDirty;

        // Runtime lookups built from the config (case-insensitive ShortPrefabName keys).
        private Dictionary<string, int> npcTiers;
        private Dictionary<int, long> tierRewards;
        private HashSet<string> disabledNpcs;
        private HashSet<string> sharedRewardTargets;

        // Last counted kill per killer -> victim, used by the anti-farm cooldown. In memory only.
        private readonly Dictionary<ulong, Dictionary<ulong, DateTime>> killCooldowns = new Dictionary<ulong, Dictionary<ulong, DateTime>>();

        // Who downed a player, so a bleed-out death is still credited to them. Cleared on recovery or death.
        private readonly Dictionary<ulong, WoundRecord> woundRecords = new Dictionary<ulong, WoundRecord>();

        // Unlisted NPC prefabs already reported in the console (each one is logged only once per load).
        private readonly HashSet<string> reportedUnknownNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Admins receiving debug messages for every baldness change. In memory only.
        private readonly HashSet<ulong> debugAdmins = new HashSet<ulong>();

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
            public long KillReward = 1000;

            [JsonProperty("Baldness gained per headshot kill (instead of the normal kill reward)")]
            public long HeadshotKillReward = 1000;

            [JsonProperty("Baldness gained per survival interval")]
            public long SurvivalReward = 100;

            [JsonProperty("Survival interval (minutes alive and connected)")]
            public int SurvivalIntervalMinutes = 30;

            [JsonProperty("Baldness lost on death")]
            public long DeathPenalty = 1000;

            [JsonProperty("Extra baldness lost when the death is a headshot")]
            public long HeadshotDeathExtraPenalty = 0;

            [JsonProperty("Deaths caused by NPCs lower baldness")]
            public bool NpcDeathsLowerBaldness = true;

            [JsonProperty("Kill cooldown per victim (minutes)")]
            public int KillCooldownMinutes = 30;

            [JsonProperty("Announce when a player reaches the highest title")]
            public bool AnnounceSupremeBaldness = true;

            [JsonProperty("Announce when a player rises to a higher title")]
            public bool AnnounceTitleUp = true;

            [JsonProperty("Announce when a player drops to a lower title")]
            public bool AnnounceTitleDrop = true;

            [JsonProperty("Reset baldness on map wipe (stats are kept)")]
            public bool ResetBaldnessOnWipe = false;

            // Collections use Replace, not merge: Oxide reads configs with default Newtonsoft settings, which would
            // append the file's entries to these defaults (lists grow on every reload, removed keys come back).
            [JsonProperty("Titles (minimum baldness -> title)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<TitleTier> Titles = new List<TitleTier>
            {
                new TitleTier { MinBaldness = 1, Name = "Greñas Sucias" },
                new TitleTier { MinBaldness = 10, Name = "Pelambrera Lamentable" },
                new TitleTier { MinBaldness = 100, Name = "Entradas Incipientes" },
                new TitleTier { MinBaldness = 1000, Name = "Coronilla a la Intemperie" },
                new TitleTier { MinBaldness = 10000, Name = "Caballero de la Tonsura" },
                new TitleTier { MinBaldness = 100000, Name = "Lord Bola de Billar" },
                new TitleTier { MinBaldness = 1000000, Name = "Su Calvísima Majestad" }
            };

            [JsonProperty("NpcTiers", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> NpcTiers = DefaultNpcTiers();

            [JsonProperty("TierRewards", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, long> TierRewards = DefaultTierRewards();

            [JsonProperty("DisabledNpcs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> DisabledNpcs = new List<string>
            {
                "npc_bandit_guard",
                "sentry.scientist.static",
                "sentry.scientist.barge",
                "sentry.scientist.barge.static",
                "sentry.bandit.static"
            };

            [JsonProperty("SharedRewardTargets", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> SharedRewardTargets = new List<string> { "patrolhelicopter", "bradleyapc", "ch47scientists.entity" };

            [JsonProperty("Shared reward: teammate radius from the target (meters)")]
            public float SharedRewardTeamRadius = 300f;

            [JsonProperty("Chat icon: SteamID64 whose avatar is shown next to plugin messages (0 = default Rust icon)")]
            public ulong ChatIconSteamId = 76561198635630459UL;

            [JsonProperty("Global events")]
            public GlobalEventsConfig GlobalEvents = new GlobalEventsConfig();
        }

        private class GlobalEventsConfig
        {
            [JsonProperty("Enabled")]
            public bool Enabled = true;

            [JsonProperty("Minutes between random events")]
            public int IntervalMinutes = 60;

            [JsonProperty("Bald hour (all baldness gains multiplied)")]
            public BaldHourConfig BaldHour = new BaldHourConfig();

            [JsonProperty("Shampoo rain (death penalty multiplied)")]
            public ShampooRainConfig ShampooRain = new ShampooRainConfig();

            [JsonProperty("Hunt the hairiest (bounty on the online player with least baldness)")]
            public HairiestHuntConfig HairiestHunt = new HairiestHuntConfig();

            [JsonProperty("Alopecia outbreak (shared big-target rewards multiplied)")]
            public AlopeciaOutbreakConfig AlopeciaOutbreak = new AlopeciaOutbreakConfig();
        }

        private class BaldHourConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Duration (minutes)")] public int DurationMinutes = 30;
            [JsonProperty("Gain multiplier")] public int Multiplier = 2;
        }

        private class ShampooRainConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Duration (minutes)")] public int DurationMinutes = 20;
            [JsonProperty("Death penalty multiplier")] public int Multiplier = 2;
        }

        private class HairiestHuntConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Duration (minutes)")] public int DurationMinutes = 20;
            [JsonProperty("Bonus for the killer")] public long KillerBonus = 2000;
            [JsonProperty("Bonus for the target if they survive")] public long SurvivorBonus = 1000;
            [JsonProperty("Minimum online players")] public int MinPlayers = 2;
        }

        private class AlopeciaOutbreakConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Duration (minutes)")] public int DurationMinutes = 60;
            [JsonProperty("Shared reward multiplier")] public int Multiplier = 3;
        }

        private class TitleTier
        {
            [JsonProperty("Minimum baldness")]
            public long MinBaldness;

            [JsonProperty("Title")]
            public string Name;
        }

        private static Dictionary<string, int> DefaultNpcTiers()
        {
            var tiers = new Dictionary<string, int>();
            void Add(int tier, params string[] prefabs)
            {
                foreach (string prefab in prefabs)
                {
                    tiers[prefab] = tier;
                }
            }

            Add(1, "chicken");
            Add(2, "zombie");
            Add(3, "snake.entity", "boar", "stag");
            Add(4, "beeswarm", "scientistnpc_ptboat", "scientistnpc_rhib");
            Add(5, "beemasterswarm", "wolf2", "frankensteinpet");
            Add(6, "npc_tunneldweller", "npc_tunneldwellerspawned");
            Add(7, "scientistnpc_junkpile_pistol", "npc_underwaterdweller", "simpleshark");
            Add(8, "scientistnpc_full_pistol", "scientistnpc_full_shotgun", "scientistnpc_full_mp5",
                "scientistnpc_full_lr300", "scientistnpc_full_any");
            Add(9, "scientistnpc_roam", "scientistnpc_roamtethered", "scientistnpc_patrol",
                "scientistnpc_patrol_arctic", "scientistnpc_arena");
            Add(10, "scientistnpc_outbreak", "scientistnpc_excavator", "scientistnpc_ch47_gunner",
                "scientistnpc_bradley", "panther", "tiger", "scarecrow", "scarecrow_dungeon", "scarecrow_dungeonnoroam");
            Add(11, "bear", "scientist2", "scientist2.shotgun", "npc_bandit_guard");
            Add(12, "scientistnpc_oilrig", "scientistnpc_cargo", "scientistnpc_cargo_turret_any",
                "scientistnpc_cargo_turret_lr300");
            Add(13, "polarbear", "crocodile", "gingerbread_dungeon");
            Add(14, "scientistnpc_heavy", "scientistnpc_peacekeeper", "scientistnpc_roam_nvg_variant",
                "gingerbread_meleedungeon");
            Add(15, "scientistnpc_bradley_heavy");
            Add(16, "sentry.scientist.static", "sentry.scientist.barge", "sentry.scientist.barge.static",
                "sentry.bandit.static");
            Add(17, "scientist2.heavy");
            Add(18, "bradleyapc");
            Add(19, "ch47scientists.entity");
            Add(20, "patrolhelicopter");
            return tiers;
        }

        // Geometric curve from 1 (tier 1) to 1000 (tier 20), about x1.44 per tier, rounded to strictly increasing integers.
        private static Dictionary<string, long> DefaultTierRewards()
        {
            long[] rewards =
            {
                1, 2, 3, 4, 5, 6, 9, 13, 18, 26,
                38, 55, 78, 113, 162, 234, 336, 483, 695, 1000
            };

            var result = new Dictionary<string, long>();
            for (int i = 0; i < rewards.Length; i++)
            {
                result[(i + 1).ToString(CultureInfo.InvariantCulture)] = rewards[i];
            }

            return result;
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
            config.SharedRewardTeamRadius = Math.Max(0f, config.SharedRewardTeamRadius);

            if (config.GlobalEvents == null) config.GlobalEvents = new GlobalEventsConfig();
            GlobalEventsConfig events = config.GlobalEvents;
            if (events.BaldHour == null) events.BaldHour = new BaldHourConfig();
            if (events.ShampooRain == null) events.ShampooRain = new ShampooRainConfig();
            if (events.HairiestHunt == null) events.HairiestHunt = new HairiestHuntConfig();
            if (events.AlopeciaOutbreak == null) events.AlopeciaOutbreak = new AlopeciaOutbreakConfig();
            events.IntervalMinutes = Math.Max(1, events.IntervalMinutes);
            events.BaldHour.DurationMinutes = Math.Max(1, events.BaldHour.DurationMinutes);
            events.ShampooRain.DurationMinutes = Math.Max(1, events.ShampooRain.DurationMinutes);
            events.HairiestHunt.DurationMinutes = Math.Max(1, events.HairiestHunt.DurationMinutes);
            events.AlopeciaOutbreak.DurationMinutes = Math.Max(1, events.AlopeciaOutbreak.DurationMinutes);
            events.HairiestHunt.MinPlayers = Math.Max(2, events.HairiestHunt.MinPlayers);

            if (config.NpcTiers == null)
            {
                config.NpcTiers = new Dictionary<string, int>();
            }

            if (config.TierRewards == null)
            {
                config.TierRewards = new Dictionary<string, long>();
            }

            if (config.DisabledNpcs == null)
            {
                config.DisabledNpcs = new List<string>();
            }

            if (config.SharedRewardTargets == null)
            {
                config.SharedRewardTargets = new List<string>();
            }

            npcTiers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> entry in config.NpcTiers)
            {
                if (!string.IsNullOrEmpty(entry.Key))
                {
                    npcTiers[entry.Key] = entry.Value;
                }
            }

            tierRewards = new Dictionary<int, long>();
            foreach (KeyValuePair<string, long> entry in config.TierRewards)
            {
                if (int.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int tier))
                {
                    tierRewards[tier] = entry.Value;
                }
                else
                {
                    PrintWarning($"TierRewards: '{entry.Key}' is not a tier number; ignored.");
                }
            }

            foreach (int tier in npcTiers.Values.Distinct().Where(t => !tierRewards.ContainsKey(t)))
            {
                PrintWarning($"TierRewards has no value for tier {tier}; NPCs of that tier give nothing.");
            }

            disabledNpcs = new HashSet<string>(config.DisabledNpcs.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
            sharedRewardTargets = new HashSet<string>(config.SharedRewardTargets.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
        }

        private class PlayerData
        {
            public string Name = string.Empty;
            public long Baldness;
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
                ["MyBaldness"] = "Tu calvicie: <color=#f0c040>{0}</color> — {1}",
                ["TopHeader"] = "<color=#f0c040>Los {0} más calvos de la isla:</color>",
                ["TopLine"] = "{0}. {1} — {2} ({3})",
                ["TopEmpty"] = "Aún no hay nadie en el ranking. La isla está llena de pelo.",
                ["SupremeBaldness"] = "<color=#f0c040>{0} HA ALCANZADO LA CALVICIE SUPREMA</color>",
                ["TitleUp"] = "<color=#f0c040>{0}</color> asciende a <color=#f0c040>{1}</color>",
                ["TitleDrop"] = "<color=#e05050>A {0} le está saliendo pelo</color> (ahora es {1})",
                ["NoPermission"] = "No tienes permiso para usar este comando.",
                ["AdminUsage"] = "Uso: /calvoadmin set <jugador> <valor> | /calvoadmin reset <jugador> | /calvoadmin debug on|off | /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminInvalidValue"] = "El valor tiene que ser un número entero igual o mayor que {0}.",
                ["PlayerNotFound"] = "No se ha encontrado ningún jugador con '{0}'.",
                ["PlayerAmbiguous"] = "Hay {0} jugadores que coinciden con '{1}'. Sé más concreto o usa el SteamID.",
                ["AdminSet"] = "Calvicie de {0} fijada en {1}.",
                ["AdminReset"] = "Calvicie de {0} reseteada a {1}.",
                ["DebugOn"] = "Debug activado: verás en el chat cada cambio de calvicie y su motivo.",
                ["DebugOff"] = "Debug desactivado.",
                ["DebugChange"] = "[debug] {0}: {1} → {2} ({3}{4}) · {5}",
                ["DebugNoReward"] = "[debug] {0}: sin calvicie · {1}",
                ["ReasonPlayerKill"] = "kill a {0}",
                ["ReasonPlayerHeadshotKill"] = "kill de headshot a {0}",
                ["ReasonDeath"] = "muerte",
                ["ReasonHeadshotDeath"] = "muerte por headshot",
                ["ReasonSurvival"] = "supervivencia",
                ["ReasonNpcKill"] = "NPC {0} (T{1})",
                ["ReasonEventParticipant"] = "evento {0} (T{1}), le hizo daño",
                ["ReasonEventTeammate"] = "evento {0} (T{1}), compañero de equipo cerca",
                ["ReasonAdmin"] = "admin",
                ["NoRewardSleeper"] = "víctima dormida o desconectada ({0})",
                ["NoRewardCooldown"] = "cooldown con {0}",
                ["NoRewardNpcDisabled"] = "NPC {0} desactivado en la config",
                ["NoRewardNpcUnlisted"] = "NPC {0} no está en NpcTiers",
                ["NoRewardTierMissing"] = "NPC {0} (T{1}) sin valor en TierRewards",
                ["NoRewardNpcDeath"] = "muerte por NPC (desactivado en la config)",
                ["EventNameBaldHour"] = "Hora de la calvicie",
                ["EventNameShampooRain"] = "Lluvia de champú",
                ["EventNameHairiestHunt"] = "Cazar al más peludo",
                ["EventNameAlopeciaOutbreak"] = "Brote de alopecia",
                ["EventTag"] = " [{0} x{1}]",
                ["EventBaldHourStart"] = "<color=#f0c040>HORA DE LA CALVICIE</color>: durante {0} min todo da x{1} de calvicie. Aprovechad, que el pelo no descansa.",
                ["EventBaldHourEnd"] = "Se acabó la Hora de la calvicie. El pelo vuelve a acechar.",
                ["EventShampooRainStart"] = "<color=#e05050>LLUVIA DE CHAMPÚ</color>: durante {0} min morir resta x{1}. Con este tiempo el pelo crece que da gusto.",
                ["EventShampooRainEnd"] = "Ha escampado. Podéis volver a morir con relativa dignidad.",
                ["EventHuntStart"] = "<color=#f0c040>CAZAR AL MÁS PELUDO</color>: {0} es el más peludo de la isla ({1}). Quien lo mate gana +{2}. Si aguanta {3} min con su melena, gana él +{4}.",
                ["EventHuntKilled"] = "<color=#f0c040>{0}</color> ha cazado al más peludo, {1}, y gana +{2}. La isla respira aliviada.",
                ["EventHuntSurvived"] = "{0} ha sobrevivido a la cacería con todo su pelo y gana +{1}. Qué asco.",
                ["EventHuntDied"] = "{0} ha muerto sin que nadie se lleve el mérito. Se acabó la cacería.",
                ["EventHuntEscaped"] = "{0} ha huido de la isla con su melena. Se acabó la cacería.",
                ["EventAlopeciaStart"] = "<color=#f0c040>BROTE DE ALOPECIA</color>: durante {0} min el heli, la Bradley y el Chinook dan x{1}.",
                ["EventAlopeciaEnd"] = "El brote de alopecia remite. Por ahora.",
                ["EventStoppedByAdmin"] = "Un admin ha cancelado el evento {0}.",
                ["ReasonHuntKill"] = "cazar al más peludo ({0})",
                ["ReasonHuntSurvived"] = "sobrevivir a la cacería",
                ["AdminEventUsage"] = "Uso: /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminEventBusy"] = "Ya hay un evento en marcha: {0}. Páralo antes con /calvoadmin evento parar.",
                ["AdminEventCannotStart"] = "No se puede lanzar {0} ahora (¿pocos jugadores conectados o desactivado en la config?).",
                ["AdminEventNone"] = "No hay ningún evento en marcha."
            };

            // Spanish is registered as the default ("en") set too: Oxide assigns each player the language
            // of their game client (usually "en") and falls back to "en", so this keeps Spanish as the default.
            lang.RegisterMessages(messages, this);
            lang.RegisterMessages(messages, this, "es");
        }

        private string Lang(string key, string userId = null, params object[] args)
        {
            string message = lang.GetMessage(key, this, userId);
            if (args.Length == 0)
            {
                return message;
            }

            try
            {
                return string.Format(message, args);
            }
            catch (FormatException)
            {
                // A lang file edited with a wrong placeholder must not break the hook that is sending the message.
                PrintWarning($"Lang message '{key}' has invalid placeholders; showing it unformatted.");
                return message;
            }
        }

        private void Reply(BasePlayer player, string key, params object[] args) =>
            SendChat(player, Lang(key, player.UserIDString, args));

        // Oxide.Rust's chat helpers pass this SteamID to "chat.add", and the client draws that account's avatar.
        private void Broadcast(string key, params object[] args) => Server.Broadcast(Lang(key, null, args), config.ChatIconSteamId);

        private void SendChat(BasePlayer player, string message) => Player.Message(player, message, config.ChatIconSteamId);

        // Spanish-style thousands separator (1.000.000), built by hand so it does not depend on the server's cultures.
        private static readonly NumberFormatInfo BaldnessFormat = new NumberFormatInfo { NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 } };

        private static string FormatBaldness(long value) => value.ToString("#,0", BaldnessFormat);

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

            if (config.GlobalEvents.Enabled)
            {
                timer.Every(config.GlobalEvents.IntervalMinutes * 60f, () => StartRandomEvent());
            }
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

        #region Player Hooks

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

        // Real players only. NPC deaths (including NPC players) are rewarded in OnEntityDeath, which Rust
        // also fires for every BasePlayer after OnPlayerDeath (BasePlayer.Die calls base.Die).
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

            if (!IsRealPlayer(victim))
            {
                return;
            }

            PlayerData victimData = GetOrCreateData(victim);
            victimData.Deaths++;
            victimData.SurvivalSeconds = 0f;
            dataDirty = true;

            if (!config.NpcDeathsLowerBaldness && IsKilledByNpc(info, killer))
            {
                DebugNoReward(victimData, Lang("NoRewardNpcDeath"));
            }
            else
            {
                long penalty = config.DeathPenalty + (headshot ? config.HeadshotDeathExtraPenalty : 0);
                string reason = Lang(headshot ? "ReasonHeadshotDeath" : "ReasonDeath");
                if (activeEvent == GlobalEvent.ShampooRain)
                {
                    penalty *= config.GlobalEvents.ShampooRain.Multiplier;
                    reason += EventTag(GlobalEvent.ShampooRain, config.GlobalEvents.ShampooRain.Multiplier);
                }

                ChangeBaldness(victimData, -penalty, true, reason);
            }

            if (activeEvent == GlobalEvent.HairiestHunt && victimId == huntTargetId)
            {
                HandleHuntTargetDeath(victimData, killer != null && killer != victim && IsRealPlayer(killer) ? killer : null);
            }

            // Suicide (or no killer at all) only counts as a death.
            if (killer == null || killer == victim || !IsRealPlayer(killer))
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

            if (!IsKillRewardable(killerData, (ulong)killer.userID, victim, victimData.Name))
            {
                return;
            }

            GainBaldness(killerData, headshot ? config.HeadshotKillReward : config.KillReward,
                Lang(headshot ? "ReasonPlayerHeadshotKill" : "ReasonPlayerKill", null, victimData.Name));
        }

        #endregion

        #region NPC Hooks

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null)
            {
                return;
            }

            // Real players are handled in OnPlayerDeath.
            BasePlayer entityPlayer = entity as BasePlayer;
            if (entityPlayer != null && IsRealPlayer(entityPlayer))
            {
                return;
            }

            string prefab = entity.ShortPrefabName;
            if (sharedRewardTargets.Contains(prefab))
            {
                CompleteEventTarget(entity);
                return;
            }

            BasePlayer killer = info?.InitiatorPlayer;
            if (!IsRealPlayer(killer))
            {
                return;
            }

            if (!npcTiers.ContainsKey(prefab) && !IsPossibleNpc(entity))
            {
                return;
            }

            PlayerData killerData = GetOrCreateData(killer);
            if (!TryGetNpcReward(prefab, out int tier, out long reward, out string whyNot))
            {
                DebugNoReward(killerData, whyNot);
                return;
            }

            GainBaldness(killerData, reward, Lang("ReasonNpcKill", null, prefab, tier));
        }

        #endregion

        #region Event Rewards

        // Generic "event reward": a target whose death pays the full reward to every player who damaged it and to
        // their online teammates near the target, once per player. Future team events reuse TrackEventParticipant,
        // CompleteEventTarget and PayEventReward with their own targets.

        private readonly Dictionary<BaseEntity, EventState> activeEvents = new Dictionary<BaseEntity, EventState>();
        private readonly HashSet<BaseEntity> completedEvents = new HashSet<BaseEntity>();

        private class EventState
        {
            public readonly HashSet<ulong> Participants = new HashSet<ulong>();
            public readonly HashSet<ulong> Teams = new HashSet<ulong>();
        }

        private class RewardEvent
        {
            public string Label;
            public int Tier;
            public long Amount;
            public Vector3 Position;
            public EventState State;
        }

        // Generic damage hook (Oxide.Rust calls it from BaseCombatEntity.Hurt for non-player entities: Bradley, CH47…).
        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info) => TrackEventParticipant(entity, info);

        // The patrol helicopter overrides Hurt, so its damage has its own hook.
        private void OnPatrolHelicopterTakeDamage(PatrolHelicopter heli, HitInfo info) => TrackEventParticipant(heli, info);

        // CH47 damage also arrives here (before base.OnAttacked); tracking twice is harmless.
        private void OnHelicopterAttack(CH47HelicopterAIController heli, HitInfo info) => TrackEventParticipant(heli, info);

        // The patrol helicopter does not die when its health runs out: Hurt fires this hook, then sends it crashing.
        private void OnPatrolHelicopterKill(PatrolHelicopter heli, HitInfo info) => CompleteEventTarget(heli);

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (activeEvents.Count == 0 && completedEvents.Count == 0)
            {
                return;
            }

            BaseEntity baseEntity = entity as BaseEntity;
            if (baseEntity != null)
            {
                activeEvents.Remove(baseEntity);
                completedEvents.Remove(baseEntity);
            }
        }

        private void TrackEventParticipant(BaseEntity target, HitInfo info)
        {
            if (target == null || info == null || sharedRewardTargets.Count == 0)
            {
                return;
            }

            if (!sharedRewardTargets.Contains(target.ShortPrefabName) || completedEvents.Contains(target))
            {
                return;
            }

            BasePlayer attacker = info.InitiatorPlayer;
            if (!IsRealPlayer(attacker))
            {
                return;
            }

            if (!activeEvents.TryGetValue(target, out EventState state))
            {
                state = new EventState();
                activeEvents[target] = state;
            }

            if (state.Participants.Add((ulong)attacker.userID))
            {
                GetOrCreateData(attacker);
            }

            if (attacker.currentTeam != 0UL)
            {
                state.Teams.Add(attacker.currentTeam);
            }
        }

        private void CompleteEventTarget(BaseEntity target)
        {
            if (target == null || completedEvents.Contains(target))
            {
                return;
            }

            completedEvents.Add(target);
            if (!activeEvents.TryGetValue(target, out EventState state))
            {
                return;
            }

            activeEvents.Remove(target);
            string prefab = target.ShortPrefabName;
            if (!TryGetNpcReward(prefab, out int tier, out long reward, out string whyNot))
            {
                foreach (ulong id in state.Participants)
                {
                    if (storedData.Players.TryGetValue(id, out PlayerData data))
                    {
                        DebugNoReward(data, whyNot);
                    }
                }

                return;
            }

            if (activeEvent == GlobalEvent.AlopeciaOutbreak)
            {
                reward *= config.GlobalEvents.AlopeciaOutbreak.Multiplier;
                prefab += EventTag(GlobalEvent.AlopeciaOutbreak, config.GlobalEvents.AlopeciaOutbreak.Multiplier);
            }

            PayEventReward(new RewardEvent
            {
                Label = prefab,
                Tier = tier,
                Amount = reward,
                Position = target.transform.position,
                State = state
            });
        }

        private void PayEventReward(RewardEvent rewardEvent)
        {
            var paid = new HashSet<ulong>();

            foreach (ulong id in rewardEvent.State.Participants)
            {
                if (paid.Add(id) && storedData.Players.TryGetValue(id, out PlayerData data))
                {
                    GainBaldness(data, rewardEvent.Amount,
                        Lang("ReasonEventParticipant", null, rewardEvent.Label, rewardEvent.Tier));
                }
            }

            foreach (BasePlayer mate in GetNearbyOnlineTeammates(rewardEvent.State.Teams, rewardEvent.Position))
            {
                if (paid.Add((ulong)mate.userID))
                {
                    GainBaldness(GetOrCreateData(mate), rewardEvent.Amount,
                        Lang("ReasonEventTeammate", null, rewardEvent.Label, rewardEvent.Tier));
                }
            }
        }

        // Team IDs are recorded at damage time, so teammates are found even if the damager is offline or dead.
        private List<BasePlayer> GetNearbyOnlineTeammates(IEnumerable<ulong> teamIds, Vector3 position)
        {
            var result = new List<BasePlayer>();
            float radius = config.SharedRewardTeamRadius;

            foreach (ulong teamId in teamIds)
            {
                RelationshipManager.PlayerTeam team = RelationshipManager.ServerInstance?.FindTeam(teamId);
                if (team?.members == null)
                {
                    continue;
                }

                foreach (ulong memberId in team.members)
                {
                    BasePlayer member = BasePlayer.FindByID(memberId);
                    if (IsRealPlayer(member) && member.IsConnected &&
                        Vector3.Distance(member.transform.position, position) <= radius)
                    {
                        result.Add(member);
                    }
                }
            }

            return result;
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
            Reply(player, "MyBaldness", FormatBaldness(data.Baldness), GetTitle(data.Baldness));
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
                lines.Add(Lang("TopLine", player.UserIDString, i + 1, top[i].Name, FormatBaldness(top[i].Baldness), GetTitle(top[i].Baldness)));
            }

            SendChat(player, string.Join("\n", lines));
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
            if (action == "debug")
            {
                ToggleDebug(player, args[1]);
                return;
            }

            if (action == "evento")
            {
                AdminEvent(player, args[1]);
                return;
            }

            long value;
            if (action == "set")
            {
                if (args.Length < 3 || !long.TryParse(args[2].Replace(".", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out value) || value < MinBaldness)
                {
                    Reply(player, "AdminInvalidValue", FormatBaldness(MinBaldness));
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
            ChangeBaldness(target, value - target.Baldness, false, Lang("ReasonAdmin"));
            Reply(player, action == "set" ? "AdminSet" : "AdminReset", target.Name, FormatBaldness(target.Baldness));
            Puts($"{player.displayName} ({player.UserIDString}) {action} baldness of {target.Name} ({matches[0].Key}) to {target.Baldness}.");
        }

        private void ToggleDebug(BasePlayer player, string mode)
        {
            ulong id = (ulong)player.userID;
            switch (mode.ToLowerInvariant())
            {
                case "on":
                    debugAdmins.Add(id);
                    Reply(player, "DebugOn");
                    break;
                case "off":
                    debugAdmins.Remove(id);
                    Reply(player, "DebugOff");
                    break;
                default:
                    Reply(player, "AdminUsage");
                    break;
            }
        }

        #endregion

        #region Global Events

        private enum GlobalEvent
        {
            None,
            BaldHour,
            ShampooRain,
            HairiestHunt,
            AlopeciaOutbreak
        }

        private readonly System.Random random = new System.Random();
        private GlobalEvent activeEvent = GlobalEvent.None;
        private Timer eventEndTimer;
        private ulong huntTargetId;

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (activeEvent == GlobalEvent.HairiestHunt && player != null && (ulong)player.userID == huntTargetId)
            {
                Broadcast("EventHuntEscaped", player.displayName);
                EndEvent(false);
            }
        }

        // Called every IntervalMinutes. Skips if an event is still running or nobody is online.
        private bool StartRandomEvent()
        {
            if (activeEvent != GlobalEvent.None)
            {
                return false;
            }

            var candidates = new List<GlobalEvent> { GlobalEvent.BaldHour, GlobalEvent.ShampooRain, GlobalEvent.HairiestHunt, GlobalEvent.AlopeciaOutbreak };
            while (candidates.Count > 0)
            {
                GlobalEvent pick = candidates[random.Next(candidates.Count)];
                if (StartEvent(pick))
                {
                    return true;
                }

                candidates.Remove(pick);
            }

            return false;
        }

        private bool StartEvent(GlobalEvent globalEvent)
        {
            if (activeEvent != GlobalEvent.None || GetActivePlayers().Count == 0)
            {
                return false;
            }

            GlobalEventsConfig events = config.GlobalEvents;
            int minutes;
            switch (globalEvent)
            {
                case GlobalEvent.BaldHour:
                    if (!events.BaldHour.Enabled) return false;
                    minutes = events.BaldHour.DurationMinutes;
                    Broadcast("EventBaldHourStart", minutes, events.BaldHour.Multiplier);
                    break;
                case GlobalEvent.ShampooRain:
                    if (!events.ShampooRain.Enabled) return false;
                    minutes = events.ShampooRain.DurationMinutes;
                    Broadcast("EventShampooRainStart", minutes, events.ShampooRain.Multiplier);
                    break;
                case GlobalEvent.HairiestHunt:
                    HairiestHuntConfig hunt = events.HairiestHunt;
                    List<BasePlayer> players = GetActivePlayers();
                    if (!hunt.Enabled || players.Count < hunt.MinPlayers) return false;
                    // Ties go to a random player among the hairiest.
                    long lowest = players.Min(p => GetOrCreateData(p).Baldness);
                    List<BasePlayer> hairiest = players.Where(p => GetOrCreateData(p).Baldness == lowest).ToList();
                    BasePlayer target = hairiest[random.Next(hairiest.Count)];
                    huntTargetId = (ulong)target.userID;
                    minutes = hunt.DurationMinutes;
                    Broadcast("EventHuntStart", target.displayName, FormatBaldness(lowest), FormatBaldness(hunt.KillerBonus), minutes, FormatBaldness(hunt.SurvivorBonus));
                    break;
                case GlobalEvent.AlopeciaOutbreak:
                    if (!events.AlopeciaOutbreak.Enabled) return false;
                    minutes = events.AlopeciaOutbreak.DurationMinutes;
                    Broadcast("EventAlopeciaStart", minutes, events.AlopeciaOutbreak.Multiplier);
                    break;
                default:
                    return false;
            }

            activeEvent = globalEvent;
            eventEndTimer = timer.Once(minutes * 60f, () => EndEvent(true));
            Puts($"Global event started: {globalEvent} ({minutes} min).");
            return true;
        }

        // timedOut: the event ran its full duration (announces the end; the hunt target gets the survivor bonus).
        private void EndEvent(bool timedOut)
        {
            GlobalEvent ended = activeEvent;
            if (ended == GlobalEvent.None)
            {
                return;
            }

            activeEvent = GlobalEvent.None;
            eventEndTimer?.Destroy();
            eventEndTimer = null;

            if (timedOut)
            {
                switch (ended)
                {
                    case GlobalEvent.BaldHour:
                        Broadcast("EventBaldHourEnd");
                        break;
                    case GlobalEvent.ShampooRain:
                        Broadcast("EventShampooRainEnd");
                        break;
                    case GlobalEvent.HairiestHunt:
                        if (storedData.Players.TryGetValue(huntTargetId, out PlayerData target))
                        {
                            long bonus = config.GlobalEvents.HairiestHunt.SurvivorBonus;
                            Broadcast("EventHuntSurvived", target.Name, FormatBaldness(bonus));
                            ChangeBaldness(target, bonus, true, Lang("ReasonHuntSurvived"));
                        }

                        break;
                    case GlobalEvent.AlopeciaOutbreak:
                        Broadcast("EventAlopeciaEnd");
                        break;
                }
            }

            huntTargetId = 0;
            Puts($"Global event ended: {ended}.");
        }

        private void HandleHuntTargetDeath(PlayerData targetData, BasePlayer killer)
        {
            if (killer == null)
            {
                Broadcast("EventHuntDied", targetData.Name);
            }
            else
            {
                long bonus = config.GlobalEvents.HairiestHunt.KillerBonus;
                PlayerData killerData = GetOrCreateData(killer);
                Broadcast("EventHuntKilled", killerData.Name, targetData.Name, FormatBaldness(bonus));
                ChangeBaldness(killerData, bonus, true, Lang("ReasonHuntKill", null, targetData.Name));
            }

            EndEvent(false);
        }

        private void AdminEvent(BasePlayer player, string name)
        {
            GlobalEvent requested;
            switch (name.ToLowerInvariant())
            {
                case "hora": requested = GlobalEvent.BaldHour; break;
                case "champu":
                case "champú": requested = GlobalEvent.ShampooRain; break;
                case "peludo": requested = GlobalEvent.HairiestHunt; break;
                case "alopecia": requested = GlobalEvent.AlopeciaOutbreak; break;
                case "parar":
                    if (activeEvent == GlobalEvent.None)
                    {
                        Reply(player, "AdminEventNone");
                        return;
                    }

                    Broadcast("EventStoppedByAdmin", EventName(activeEvent));
                    EndEvent(false);
                    return;
                default:
                    Reply(player, "AdminEventUsage");
                    return;
            }

            if (activeEvent != GlobalEvent.None)
            {
                Reply(player, "AdminEventBusy", EventName(activeEvent));
                return;
            }

            if (!StartEvent(requested))
            {
                Reply(player, "AdminEventCannotStart", EventName(requested));
            }
        }

        private string EventName(GlobalEvent globalEvent) => Lang("EventName" + globalEvent);

        private string EventTag(GlobalEvent globalEvent, int multiplier) => Lang("EventTag", null, EventName(globalEvent), multiplier);

        // Real players who are connected, alive and awake.
        private static List<BasePlayer> GetActivePlayers() =>
            BasePlayer.activePlayerList.Where(p => IsRealPlayer(p) && p.IsConnected && !p.IsDead() && !p.IsSleeping()).ToList();

        // Every baldness gain from gameplay goes through here so the Bald hour can multiply it.
        private void GainBaldness(PlayerData data, long amount, string reason)
        {
            if (activeEvent == GlobalEvent.BaldHour)
            {
                amount *= config.GlobalEvents.BaldHour.Multiplier;
                reason += EventTag(GlobalEvent.BaldHour, config.GlobalEvents.BaldHour.Multiplier);
            }

            ChangeBaldness(data, amount, true, reason);
        }

        #endregion

        #region Helpers

        private static bool IsRealPlayer(BasePlayer player) => player != null && player.userID.IsSteamId();

        // Used only to decide whether an unlisted prefab deserves a console notice; rewards come from NpcTiers.
        private static bool IsPossibleNpc(BaseCombatEntity entity)
        {
            if (entity is BasePlayer || entity is BaseNpc)
            {
                return true;
            }

            BaseEntity baseEntity = entity;
            return entity.OwnerID == 0UL && !(baseEntity is LootContainer) && !(baseEntity is ResourceEntity);
        }

        private bool IsKilledByNpc(HitInfo info, BasePlayer killer)
        {
            if (killer != null)
            {
                return !IsRealPlayer(killer);
            }

            BaseEntity initiator = info?.Initiator;
            if (initiator == null)
            {
                return false;
            }

            return initiator is BaseNpc || npcTiers.ContainsKey(initiator.ShortPrefabName);
        }

        private bool TryGetNpcReward(string prefab, out int tier, out long reward, out string whyNot)
        {
            tier = 0;
            reward = 0;
            whyNot = null;

            if (!npcTiers.TryGetValue(prefab, out tier))
            {
                if (reportedUnknownNpcs.Add(prefab))
                {
                    Puts($"Unlisted NPC killed: '{prefab}'. Add it to NpcTiers in the config to give baldness for it.");
                }

                whyNot = Lang("NoRewardNpcUnlisted", null, prefab);
                return false;
            }

            if (disabledNpcs.Contains(prefab))
            {
                whyNot = Lang("NoRewardNpcDisabled", null, prefab);
                return false;
            }

            if (!tierRewards.TryGetValue(tier, out reward))
            {
                whyNot = Lang("NoRewardTierMissing", null, prefab, tier);
                return false;
            }

            return true;
        }

        private bool IsKillRewardable(PlayerData killerData, ulong killerId, BasePlayer victim, string victimName)
        {
            // Killing sleepers or disconnected players is not glorious.
            if (victim.IsSleeping() || !victim.IsConnected)
            {
                DebugNoReward(killerData, Lang("NoRewardSleeper", null, victimName));
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
                DebugNoReward(killerData, Lang("NoRewardCooldown", null, victimName));
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
                    GainBaldness(data, config.SurvivalReward, Lang("ReasonSurvival"));
                }
            }
        }

        private void ChangeBaldness(PlayerData data, long delta, bool announce, string reason)
        {
            long oldValue = data.Baldness;
            long newValue = Math.Max(MinBaldness, oldValue + delta);

            SendDebug("DebugChange", data.Name, FormatBaldness(oldValue), FormatBaldness(newValue),
                delta >= 0 ? "+" : string.Empty, FormatBaldness(delta), reason);

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

            int oldTier = GetTierIndex(oldValue);
            int newTier = GetTierIndex(newValue);
            if (newTier > oldTier)
            {
                // Reaching the highest title gets its own announcement instead of the generic one.
                if (newTier == config.Titles.Count - 1 && config.AnnounceSupremeBaldness)
                {
                    Broadcast("SupremeBaldness", data.Name);
                }
                else if (config.AnnounceTitleUp)
                {
                    Broadcast("TitleUp", data.Name, GetTitle(newValue));
                }
            }
            else if (newTier < oldTier && config.AnnounceTitleDrop)
            {
                Broadcast("TitleDrop", data.Name, GetTitle(newValue));
            }
        }

        private void DebugNoReward(PlayerData data, string reason) => SendDebug("DebugNoReward", data.Name, reason);

        private void SendDebug(string key, params object[] args)
        {
            if (debugAdmins.Count == 0)
            {
                return;
            }

            foreach (ulong adminId in debugAdmins)
            {
                BasePlayer admin = BasePlayer.FindByID(adminId);
                if (admin != null && admin.IsConnected)
                {
                    SendChat(admin, Lang(key, admin.UserIDString, args));
                }
            }
        }

        private int GetTierIndex(long baldness)
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

        private string GetTitle(long baldness) => config.Titles[GetTierIndex(baldness)].Name;

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
