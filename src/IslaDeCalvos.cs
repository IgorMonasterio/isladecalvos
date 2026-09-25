using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Isla de Calvos", "Igor Monasterio", "1.5.1")]
    [Description("Baldness system for the Isla de Calvos Rust server: being bald is glory, hair is a curse.")]
    public class IslaDeCalvos : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "isladecalvos.admin";
        private const long MinBaldness = 0;
        private const int RankingPageSize = 10;
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

        // Server Rewards (RP): optional. If it is not loaded, no RP is paid and baldness works as usual.
        [PluginReference] private Plugin ServerRewards = null;

        // RP per interval by title, sorted by minimum baldness (built from the config).
        private List<KeyValuePair<long, int>> rpRates;

        // Position at the previous survival tick, to tell AFK players apart. In memory only.
        private readonly Dictionary<ulong, Vector3> lastPositions = new Dictionary<ulong, Vector3>();

        private bool warnedNoServerRewards;

        // Barber shop: Calvario NPC ids (HumanNPC) and the NPC each player last talked to. In memory only.
        private HashSet<ulong> calvarioNpcIds;
        private readonly Dictionary<ulong, BasePlayer> calvarioNpcInUse = new Dictionary<ulong, BasePlayer>();

        private class WoundRecord
        {
            public BasePlayer Attacker;
            public bool Headshot;
        }

        #endregion

        #region Configuration

        // Bump when a release must overwrite values already saved in existing config files.
        private const int CurrentConfigVersion = 131;

        private class Configuration
        {
            // Missing in configs older than 1.3.0, so it reads as 0 and triggers the migration below.
            [JsonProperty("Config version (do not edit)")]
            public int ConfigVersion;

            [JsonProperty("Baldness gained per player kill")]
            public long KillReward = 1000;

            [JsonProperty("Baldness gained per headshot kill (instead of the normal kill reward)")]
            public long HeadshotKillReward = 1000;

            [JsonProperty("Baldness gained per survival interval")]
            public long SurvivalReward = 100;

            [JsonProperty("Survival interval (minutes alive and connected)")]
            public int SurvivalIntervalMinutes = 30;

            // Percentage of the victim's current baldness (rounded up), whatever killed them.
            [JsonProperty("Baldness lost on death (% of current baldness)")]
            public int DeathPenaltyPercent = 10;

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

            [JsonProperty("On-screen UI")]
            public UiConfig Ui = new UiConfig();

            [JsonProperty("Cursed items (El Calvario)")]
            public CursedItemsConfig CursedItems = new CursedItemsConfig();

            [JsonProperty("Server Rewards (RP by title)")]
            public ServerRewardsConfig ServerRewards = new ServerRewardsConfig();

            [JsonProperty("Barber shop (HumanNPC)")]
            public BarberShopConfig BarberShop = new BarberShopConfig();
        }

        // El Calvario opens from a HumanNPC at the barber shop; /calvos only shows the ranking.
        private class BarberShopConfig
        {
            [JsonProperty("Calvario NPC ids (HumanNPC userid)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> CalvarioNpcIds = new List<ulong>();

            [JsonProperty("Max distance to the Calvario NPC to use items (meters)")] public float MaxDistance = 5f;
        }

        // Pays Server Rewards RP to bald players who stay alive, connected and not AFK.
        private class ServerRewardsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;

            [JsonProperty("Interval (minutes alive and connected)")] public int IntervalMinutes = 30;

            [JsonProperty("Only pay players who moved during the interval (not AFK)")] public bool RequireMovement = true;

            [JsonProperty("Minimum movement between checks to count as active (meters)")] public float MinMoveMeters = 1f;

            [JsonProperty("Tell the player in chat when RP is paid")] public bool NotifyPlayer = true;

            [JsonProperty("RP per interval by title (minimum baldness -> RP)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> RpByMinBaldness = new Dictionary<string, int>
            {
                ["1000"] = 1,
                ["10000"] = 3,
                ["100000"] = 10,
                ["1000000"] = 30
            };
        }

        private class ItemDropConfig
        {
            [JsonProperty("Item shortname")] public string Shortname;
            [JsonProperty("Drop chance (0-1)")] public float DropChance;
        }

        private class BleachConfig : ItemDropConfig
        {
            [JsonProperty("Win chance (0-1)")] public float WinChance = 0.7f;
            [JsonProperty("Baldness on win")] public long WinAmount = 500;
            [JsonProperty("Baldness lost on fail")] public long LoseAmount = 500;
        }

        private class BatteryConfig : ItemDropConfig
        {
            [JsonProperty("Gain multiplier")] public int Multiplier = 2;
            [JsonProperty("Duration (minutes)")] public int Minutes = 10;
        }

        private class TrophyConfig : ItemDropConfig
        {
            [JsonProperty("Baldness when used")] public long Reward;
            [JsonProperty("Drops from NPC tier (min)")] public int MinTier;
            [JsonProperty("Drops from NPC tier (max)")] public int MaxTier;
        }

        private class IdTagsConfig
        {
            [JsonProperty("Item shortnames (one per color)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shortnames = new List<string>
            {
                "blueidtag", "grayidtag", "greenidtag", "lavenderidtag", "mintidtag", "orangeidtag",
                "pinkidtag", "purpleidtag", "redidtag", "whiteidtag", "yellowidtag"
            };

            [JsonProperty("Drop chance per NPC kill (0-1)")] public float DropChance = 0.05f;
            [JsonProperty("Drops from NPC tier (min)")] public int MinTier = 1;
            [JsonProperty("Drops from NPC tier (max)")] public int MaxTier = 17;
            [JsonProperty("Baldness per delivered tag")] public long Reward = 100;
            [JsonProperty("Bonus for completing all colors")] public long CollectionBonus = 10000;
        }

        // Items that exist in Rust's code but never spawn on normal servers; only this plugin hands them out.
        private class CursedItemsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;

            [JsonProperty("Bleach (gamble)")]
            public BleachConfig Bleach = new BleachConfig { Shortname = "bleach", DropChance = 0.05f };

            [JsonProperty("Duct tape (your next death costs nothing)")]
            public ItemDropConfig DuctTape = new ItemDropConfig { Shortname = "ducttape", DropChance = 0.05f };

            [JsonProperty("Small battery (personal gain multiplier)")]
            public BatteryConfig Battery = new BatteryConfig { Shortname = "battery.small", DropChance = 0.03f };

            [JsonProperty("Dog tag")]
            public TrophyConfig DogTag = new TrophyConfig { Shortname = "dogtagneutral", Reward = 200, DropChance = 0.5f, MinTier = 8, MaxTier = 12 };

            [JsonProperty("Blue dog tags")]
            public TrophyConfig BlueDogTags = new TrophyConfig { Shortname = "bluedogtags", Reward = 500, DropChance = 0.3f, MinTier = 13, MaxTier = 17 };

            [JsonProperty("Red dog tags (heli/Bradley/CH47, every paid player; tiers ignored)")]
            public TrophyConfig RedDogTags = new TrophyConfig { Shortname = "reddogtags", Reward = 1500, DropChance = 1f, MinTier = 18, MaxTier = 20 };

            [JsonProperty("Gems")]
            public TrophyConfig Gems = new TrophyConfig { Shortname = "kickgems", Reward = 5000, DropChance = 0.01f, MinTier = 12, MaxTier = 20 };

            [JsonProperty("ID tags (Carne de Calvo collection)")]
            public IdTagsConfig IdTags = new IdTagsConfig();
        }

        private class UiConfig
        {
            [JsonProperty("Show baldness counter")]
            public bool ShowCounter = true;

            // Anchors are 0-1 screen fractions (0 0 = bottom left); offsets are pixels from those anchors.
            [JsonProperty("Counter anchor min")] public string CounterAnchorMin = "1 1";
            [JsonProperty("Counter anchor max")] public string CounterAnchorMax = "1 1";
            // Top-right corner of the screen (the bottom area is used by RaidableBases' status panel).
            [JsonProperty("Counter offset min")] public string CounterOffsetMin = "-212 -58";
            [JsonProperty("Counter offset max")] public string CounterOffsetMax = "-16 -22";

            [JsonProperty("Seconds the +X / -X popup stays")]
            public float DeltaSeconds = 2.5f;

            [JsonProperty("Show event banner in the middle of the screen")]
            public bool ShowEventBanner = true;

            [JsonProperty("Seconds the event banner stays")]
            public float BannerSeconds = 8f;
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

        protected override void LoadDefaultConfig() => config = new Configuration { ConfigVersion = CurrentConfigVersion };

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
            config.DeathPenaltyPercent = Math.Max(0, Math.Min(100, config.DeathPenaltyPercent));
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

            if (config.Ui == null) config.Ui = new UiConfig();

            if (config.ConfigVersion < 131)
            {
                // 1.3.0 moved the counter next to the status bars (pickup notices covered it); 1.3.1 moves it to the
                // top-right corner because RaidableBases' status panel uses that bottom area.
                UiConfig uiDefaults = new UiConfig();
                config.Ui.CounterAnchorMin = uiDefaults.CounterAnchorMin;
                config.Ui.CounterAnchorMax = uiDefaults.CounterAnchorMax;
                config.Ui.CounterOffsetMin = uiDefaults.CounterOffsetMin;
                config.Ui.CounterOffsetMax = uiDefaults.CounterOffsetMax;
                PrintWarning("Config updated to 1.3.1: baldness counter moved to the top-right corner.");
            }

            config.ConfigVersion = CurrentConfigVersion;
            if (config.CursedItems == null) config.CursedItems = new CursedItemsConfig();
            CursedItemsConfig cursed = config.CursedItems;
            CursedItemsConfig cursedDefaults = new CursedItemsConfig();
            if (cursed.Bleach == null) cursed.Bleach = cursedDefaults.Bleach;
            if (cursed.DuctTape == null) cursed.DuctTape = cursedDefaults.DuctTape;
            if (cursed.Battery == null) cursed.Battery = cursedDefaults.Battery;
            if (cursed.DogTag == null) cursed.DogTag = cursedDefaults.DogTag;
            if (cursed.BlueDogTags == null) cursed.BlueDogTags = cursedDefaults.BlueDogTags;
            if (cursed.RedDogTags == null) cursed.RedDogTags = cursedDefaults.RedDogTags;
            if (cursed.Gems == null) cursed.Gems = cursedDefaults.Gems;
            if (cursed.IdTags == null) cursed.IdTags = cursedDefaults.IdTags;
            if (cursed.IdTags.Shortnames == null) cursed.IdTags.Shortnames = new List<string>();
            cursed.IdTags.Shortnames = cursed.IdTags.Shortnames.Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
            cursed.Battery.Minutes = Math.Max(1, cursed.Battery.Minutes);
            config.Ui.DeltaSeconds = Math.Max(0.5f, config.Ui.DeltaSeconds);
            config.Ui.BannerSeconds = Math.Max(1f, config.Ui.BannerSeconds);

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

            if (config.BarberShop == null) config.BarberShop = new BarberShopConfig();
            if (config.BarberShop.CalvarioNpcIds == null) config.BarberShop.CalvarioNpcIds = new List<ulong>();
            config.BarberShop.MaxDistance = Math.Max(1f, config.BarberShop.MaxDistance);
            calvarioNpcIds = new HashSet<ulong>(config.BarberShop.CalvarioNpcIds);
            if (calvarioNpcIds.Count == 0)
            {
                PrintWarning("No Calvario NPC configured (\"Calvario NPC ids\" is empty): nobody can use cursed items until the barber's HumanNPC userid is added.");
            }

            if (config.ServerRewards == null) config.ServerRewards = new ServerRewardsConfig();
            ServerRewardsConfig rp = config.ServerRewards;
            rp.IntervalMinutes = Math.Max(1, rp.IntervalMinutes);
            rp.MinMoveMeters = Math.Max(0f, rp.MinMoveMeters);
            if (rp.RpByMinBaldness == null) rp.RpByMinBaldness = new Dictionary<string, int>();
            rpRates = new List<KeyValuePair<long, int>>();
            foreach (KeyValuePair<string, int> entry in rp.RpByMinBaldness)
            {
                if (long.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long minBaldness))
                {
                    rpRates.Add(new KeyValuePair<long, int>(minBaldness, Math.Max(0, entry.Value)));
                }
                else
                {
                    PrintWarning($"Server Rewards: '{entry.Key}' is not a baldness number; ignored.");
                }
            }

            rpRates = rpRates.OrderBy(r => r.Key).ToList();

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
            // Filled in at load/creation (it is the dictionary key), not stored twice in the file.
            [JsonIgnore]
            public ulong Id;

            public string Name = string.Empty;
            public long Baldness;
            public int Kills;
            public int Deaths;
            public int HeadshotKills;

            // El Calvario: duct tape shield, delivered ID tag colors and completed collections.
            public bool HasDeathShield;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> CarneColors = new List<string>();

            public int CarnesCompleted;

            // Seconds alive and connected since the last survival reward (or since the last death).
            public float SurvivalSeconds;

            // Server Rewards: seconds alive and connected since the last RP payout, and whether the player moved.
            public float RpSeconds;
            public bool RpMoved;
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

            foreach (KeyValuePair<ulong, PlayerData> entry in storedData.Players)
            {
                entry.Value.Id = entry.Key;
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
                data = new PlayerData { Id = id };
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
                ["CalvarioTitle"] = "EL CALVARIO",
                ["BarberName"] = "EL BARBERO",
                ["BarberGreeting1"] = "Siéntate, peludo. ¿Qué te pelo hoy?",
                ["BarberGreeting2"] = "Pasa, pasa. Esa melena no se va a arrancar sola.",
                ["BarberGreeting3"] = "Otra vez tú. Cada día te veo más frente, así me gusta.",
                ["BarberOptItems"] = "Quiero usar un objeto maldito",
                ["BarberOptCarne"] = "Vengo a sellar el Carné de Calvo",
                ["BarberOptBye"] = "Nada, solo miraba",
                ["BarberOptBack"] = "Volver",
                ["BarberOptStamp"] = "Séllame lo que traigo",
                ["BarberItemsIntro"] = "A ver qué traes en esos bolsillos.",
                ["BarberItemsNone"] = "No llevas nada maldito encima. Vuelve cuando hayas matado algo.",
                ["BarberItemLine"] = "{0} (tienes {1}): {2}",
                ["BarberTrophyUsed"] = "{0}: +{1}. Lo cuelgo en la pared de los trofeos.",
                ["BarberCarneIntro"] = "Enséñame el carné. Llevas {0} de {1} colores sellados. Carnés completos: {2}.",
                ["BarberCarneStamped"] = "Sellados: {0}",
                ["BarberCarneMissing"] = "Te faltan: {0}",
                ["TagColorBlue"] = "azul",
                ["TagColorGray"] = "gris",
                ["TagColorGreen"] = "verde",
                ["TagColorLavender"] = "lavanda",
                ["TagColorMint"] = "menta",
                ["TagColorOrange"] = "naranja",
                ["TagColorPink"] = "rosa",
                ["TagColorPurple"] = "morado",
                ["TagColorRed"] = "rojo",
                ["TagColorWhite"] = "blanco",
                ["TagColorYellow"] = "amarillo",
                ["CalvarioSubtitleV2"] = "Clínica de alopecia voluntaria  ·  Se entra con pelo y se sale con dignidad",
                ["CalvarioYou"] = "Tu calvicie: <color=#f5d3a8>{0}</color>  —  {1}",
                ["CalvarioNextV2"] = "Hacia <color=#f5d3a8>{0}</color>: te faltan {1}. Sigue matando, que no se pela solo.",
                ["CalvarioTop"] = "Cima capilar alcanzada. Ya no queda nada que arrancar.",
                ["CalvarioTabRanking"] = "SALÓN DE LA FAMA CALVA",
                ["CalvarioClose"] = "X",
                ["CalvarioShieldOn"] = "Cinta puesta. Tu calva sobrevive a la próxima muerte.",
                ["CalvarioBatteryOn"] = "Maquinilla zumbando: quedan {0} min.",
                ["CalvarioProverb1"] = "Dios hizo pocas cabezas perfectas. Al resto les puso pelo.",
                ["CalvarioProverb2"] = "El pelo es temporal. La calva es para siempre.",
                ["CalvarioProverb3"] = "Más vale calvo conocido que peludo por conocer.",
                ["CalvarioProverb4"] = "Cabeza que brilla, cabeza que manda.",
                ["CalvarioProverb5"] = "No es una calva. Es un panel solar.",
                ["CalvarioProverb7"] = "La calva no se pierde: se conquista.",
                ["CalvarioProverb8"] = "El champú anticaída es propaganda peluda.",
                ["CalvarioCarneHintV2"] = "Una tarjeta de cada color: +{0} por sello y +{1} al completar el carné. Las repetidas, para cambiarlas en el patio.",
                ["CalvarioRankingEmpty"] = "Aún no hay nadie en el salón. La isla está llena de pelo.",
                ["CalvarioRankingLine"] = "{0}.  {1}",
                ["CalvarioRankingYouV2"] = "Tu puesto: <color=#f5d3a8>{0}º</color> de {1}. Te faltan <color=#f5d3a8>{2}</color> para adelantar a {3}. Venga, que ese tiene hasta cejas.",
                ["CalvarioRankingFirstV2"] = "Eres la cabeza más brillante de la isla. Los demás se peinan mirándose en ti.",
                ["CalvarioPrev"] = "< ANTERIOR",
                ["CalvarioNextPage"] = "SIGUIENTE >",
                ["CalvarioPage"] = "Página {0}/{1}",
                ["CalvarioItemBleach"] = "Lejía",
                ["CalvarioItemDuctTape"] = "Cinta americana",
                ["CalvarioItemBattery"] = "Pila pequeña",
                ["CalvarioItemDogTag"] = "Placa militar",
                ["CalvarioItemBlueDogTags"] = "Placas azules",
                ["CalvarioItemRedDogTags"] = "Placas rojas",
                ["CalvarioItemGems"] = "Gemas",
                ["CalvarioItemIdTag"] = "Tarjeta de identificación",
                ["CalvarioDescBleach"] = "Champú de la casa. {0} %: te abrasa el cuero cabelludo (+{1}). Si no, mechón rebelde (-{2}).",
                ["CalvarioDescDuctTape"] = "Parche para la calva: si mueres, el pelo ni se entera. Una vez.",
                ["CalvarioDescBattery"] = "Para la maquinilla: x{0} a todo lo que ganes durante {1} min. Bzzzz.",
                ["CalvarioDescDogTag"] = "Recuerdo de un científico con flequillo. +{0}.",
                ["CalvarioDescBlueDogTags"] = "Arrancadas a un heavy con melena. +{0}.",
                ["CalvarioDescRedDogTags"] = "Del piloto que perdió el tupé con el helicóptero. +{0}.",
                ["CalvarioDescGems"] = "Joya de la corona de Su Calvísima Majestad. Brilla como tu cabeza. +{0}.",
                ["ItemFoundV3"] = "Has encontrado: <color=#f0c040>{0}</color>. Llévalo al Calvario de la peluquería (/peluqueria), que en el bolsillo no hace nada.",
                ["CalvarioGoToBarber"] = "Los objetos malditos se usan en el Calvario de la peluquería. Ve con /peluqueria y háblale al barbero.",
                ["ItemNoneV2"] = "No llevas {0} encima. Ni eso.",
                ["ItemShieldAlready"] = "Ya llevas la calva tapada con cinta. Muere primero.",
                ["ItemBatteryAlreadyV2"] = "La maquinilla ya está en marcha (quedan {0} min). Más rápido no va a ir, fiera.",
                ["ItemBleachWin"] = "La lejía te ha abrasado el cuero cabelludo. Gloria: +{0}.",
                ["ItemBleachFail"] = "La lejía te ha dejado un mechón rebelde. Vergüenza: -{0}.",
                ["ItemShieldOnV2"] = "Te has tapado la calva con cinta americana. Tu próxima muerte no restará. Elegante no es, pero funciona.",
                ["ItemShieldUsed"] = "La cinta americana ha protegido tu calva: esta muerte no resta.",
                ["ItemBatteryOnV2"] = "Maquinilla en marcha: x{0} durante {1} min. Bzzzz.",
                ["ItemBatteryOffV2"] = "Se le ha acabado la pila a la maquinilla. Vuelves a pelarte a mano, como los pobres.",
                ["CarneNothingV2"] = "No llevas ninguna tarjeta de un color que te falte. Las repes, a tu primo.",
                ["CarneDeliveredV2"] = "Has entregado {0} tarjeta(s): +{1}. El funcionario ni te ha mirado.",
                ["CarneCompletedV2"] = "<color=#f0c040>{0}</color> ha completado el CARNÉ DE CALVO y gana +{1}. El Ministerio de Alopecia está orgulloso. Su madre, no tanto.",
                ["ReasonItemUse"] = "objeto: {0}",
                ["ReasonCarne"] = "carné de calvo",
                ["SupremeBaldnessV2"] = "<color=#f0c040>{0} HA ALCANZADO LA CALVICIE SUPREMA</color>. Tiene la cabeza tan pulida que las gaviotas se peinan mirándose en ella y los pilotos la usan para aterrizar de noche. Peludos del mundo: de rodillas, que a partir de hoy hasta vuestra madre se la frota para pedir un deseo.",
                ["TitleUpV2"] = "<color=#f0c040>{0}</color> asciende a <color=#f0c040>{1}</color>. Su peluquero ya ha pedido el paro.",
                ["TitleDrop"] = "<color=#e05050>A {0} le está saliendo pelo</color> (ahora es {1})",
                ["NoPermissionV2"] = "No tienes permiso para usar este comando. Buen intento, listo.",
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
                ["DebugRp"] = "[debug] {0}: +{1} RP ({2})",
                ["DebugNoRp"] = "[debug] {0}: sin RP · {1}",
                ["NoRpAfk"] = "no se ha movido (AFK)",
                ["NoRpPlugin"] = "Server Rewards no está cargado",
                ["NoRpRefused"] = "Server Rewards no aceptó el pago",
                ["RpEarnedV2"] = "<color=#f0c040>+{0} RP</color> por lucir calva de <color=#f5d3a8>{1}</color>. Es lo único que te va a pagar alguien en la vida por estar calvo: disfrútalo.",
                ["ReasonPlayerKill"] = "kill a {0}",
                ["ReasonPlayerHeadshotKill"] = "kill de headshot a {0}",
                ["ReasonDeath"] = "muerte (-{0} %)",
                ["ReasonHeadshotDeath"] = "muerte por headshot (-{0} %)",
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
                ["EventBaldHourStartV2"] = "<color=#f0c040>HORA DE LA CALVICIE</color>: durante {0} min todo da x{1} de calvicie. Salid a matar, que la frente no se despeja sola.",
                ["EventBaldHourEndV2"] = "Se acabó la Hora de la calvicie. Volvéis a pelaros a precio normal.",
                ["EventShampooRainStart"] = "<color=#e05050>LLUVIA DE CHAMPÚ</color>: durante {0} min morir resta x{1}. Con este tiempo el pelo crece que da gusto.",
                ["EventShampooRainEnd"] = "Ha escampado. Podéis volver a morir con relativa dignidad.",
                ["EventHuntStartV2"] = "<color=#f0c040>CAZAR AL MÁS PELUDO</color>: {0} es el más peludo de la isla ({1}). Quien lo mate gana +{2}. Si aguanta {3} min con su melena, gana él +{4}. A por él, que esa melena no se va a cortar sola.",
                ["EventHuntKilledV2"] = "<color=#f0c040>{0}</color> ha cazado al más peludo, {1}, y gana +{2}. Corte de pelo gratis y a bocajarro.",
                ["EventHuntSurvivedV2"] = "{0} ha sobrevivido a la cacería con todo su pelo y gana +{1}. Vergüenza os debería dar, calvos.",
                ["EventHuntDiedV2"] = "{0}, el más peludo de la isla, la ha palmado solito, sin que nadie le meta un tiro. Se ha muerto como vivió: con el pelo en la cara y sin que nadie le haga ni puto caso. Se acabó la cacería.",
                ["EventHuntEscapedV2"] = "{0} se ha pirado de la isla con su melena, como una rata con extensiones. Volverá cuando se le acabe el acondicionador. Se acabó la cacería.",
                ["EventAlopeciaStartV2"] = "<color=#f0c040>BROTE DE ALOPECIA</color>: durante {0} min el heli, la Bradley y el Chinook dan x{1}. Hoy hasta el cielo se pela.",
                ["EventAlopeciaEndV2"] = "Se acabó el brote de alopecia. El heli vuelve a pagar lo de siempre, como un funcionario.",
                ["EventStoppedByAdminV2"] = "Un admin ha cancelado el evento {0}. Las quejas, a su peluquero.",
                ["ReasonHuntKill"] = "cazar al más peludo ({0})",
                ["ReasonHuntSurvived"] = "sobrevivir a la cacería",
                ["AdminEventUsage"] = "Uso: /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminEventBusy"] = "Ya hay un evento en marcha: {0}. Páralo antes con /calvoadmin evento parar.",
                ["AdminEventCannotStart"] = "No se puede lanzar {0} ahora (¿pocos jugadores conectados o desactivado en la config?).",
                ["AdminEventNone"] = "No hay ningún evento en marcha.",
                ["BatteryTag"] = " [pila x{0}]",
                ["HudCounter"] = "<size=11><color=#b8b8b8>CALVICIE</color></size>  <color=#f0c040>{0}</color>\n<size=10><color=#d8d8d8>{1}</color></size>"
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

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player) && !player.IsSleeping())
                {
                    DrawCounter(player);
                }
            }

            if (config.GlobalEvents.Enabled)
            {
                timer.Every(config.GlobalEvents.IntervalMinutes * 60f, () => StartRandomEvent());
            }

            ValidateCursedItemNames();
        }

        private void OnServerSave() => SaveData();

        private void Unload()
        {
            SaveData();
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                DestroyUi(player);
            }
        }

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

            if (victimData.HasDeathShield)
            {
                victimData.HasDeathShield = false;
                Reply(victim, "ItemShieldUsed");
                DebugNoReward(victimData, Lang("ItemShieldUsed"));
            }
            else if (!config.NpcDeathsLowerBaldness && IsKilledByNpc(info, killer))
            {
                DebugNoReward(victimData, Lang("NoRewardNpcDeath"));
            }
            else
            {
                int percent = config.DeathPenaltyPercent;
                string eventTag = string.Empty;
                if (activeEvent == GlobalEvent.ShampooRain)
                {
                    percent *= config.GlobalEvents.ShampooRain.Multiplier;
                    eventTag = EventTag(GlobalEvent.ShampooRain, config.GlobalEvents.ShampooRain.Multiplier);
                }

                percent = Math.Min(100, percent);
                string reason = Lang(headshot ? "ReasonHeadshotDeath" : "ReasonDeath", null, percent) + eventTag;
                long penalty = (long)Math.Ceiling(victimData.Baldness * percent / 100.0);

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

            BaseEntity asBaseEntity = entity;
            if (asBaseEntity is LootContainer && prefab.IndexOf("barrel", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                RollBarrelDrops(killer);
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
            RollNpcDrops(killer, tier);
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
            PayEventRewardTo(rewardEvent, paid);

            // Red dog tags for every paid player who is online to receive them.
            foreach (ulong id in paid)
            {
                BasePlayer player = BasePlayer.FindByID(id);
                if (player != null && player.IsConnected)
                {
                    TryDrop(player, config.CursedItems.RedDogTags);
                }
            }
        }

        private void PayEventRewardTo(RewardEvent rewardEvent, HashSet<ulong> paid)
        {

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

        [ChatCommand("calvos")]
        private void CmdCalvos(BasePlayer player, string command, string[] args)
        {
            if (IsRealPlayer(player))
            {
                OpenRanking(player, 0);
            }
        }

        [ChatCommand("calvoadmin")]
        private void CmdCalvoAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                Reply(player, "NoPermissionV2");
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
            if (player != null)
            {
                lastPositions.Remove((ulong)player.userID);
                calvarioNpcInUse.Remove((ulong)player.userID);
            }

            if (activeEvent == GlobalEvent.HairiestHunt && player != null && (ulong)player.userID == huntTargetId)
            {
                BroadcastEvent("EventHuntEscapedV2", player.displayName);
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
                    BroadcastEvent("EventBaldHourStartV2", minutes, events.BaldHour.Multiplier);
                    break;
                case GlobalEvent.ShampooRain:
                    if (!events.ShampooRain.Enabled) return false;
                    minutes = events.ShampooRain.DurationMinutes;
                    BroadcastEvent("EventShampooRainStart", minutes, events.ShampooRain.Multiplier);
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
                    BroadcastEvent("EventHuntStartV2", target.displayName, FormatBaldness(lowest), FormatBaldness(hunt.KillerBonus), minutes, FormatBaldness(hunt.SurvivorBonus));
                    break;
                case GlobalEvent.AlopeciaOutbreak:
                    if (!events.AlopeciaOutbreak.Enabled) return false;
                    minutes = events.AlopeciaOutbreak.DurationMinutes;
                    BroadcastEvent("EventAlopeciaStartV2", minutes, events.AlopeciaOutbreak.Multiplier);
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
                        BroadcastEvent("EventBaldHourEndV2");
                        break;
                    case GlobalEvent.ShampooRain:
                        BroadcastEvent("EventShampooRainEnd");
                        break;
                    case GlobalEvent.HairiestHunt:
                        if (storedData.Players.TryGetValue(huntTargetId, out PlayerData target))
                        {
                            long bonus = config.GlobalEvents.HairiestHunt.SurvivorBonus;
                            BroadcastEvent("EventHuntSurvivedV2", target.Name, FormatBaldness(bonus));
                            ChangeBaldness(target, bonus, true, Lang("ReasonHuntSurvived"));
                        }

                        break;
                    case GlobalEvent.AlopeciaOutbreak:
                        BroadcastEvent("EventAlopeciaEndV2");
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
                BroadcastEvent("EventHuntDiedV2", targetData.Name);
            }
            else
            {
                long bonus = config.GlobalEvents.HairiestHunt.KillerBonus;
                PlayerData killerData = GetOrCreateData(killer);
                BroadcastEvent("EventHuntKilledV2", killerData.Name, targetData.Name, FormatBaldness(bonus));
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

                    BroadcastEvent("EventStoppedByAdminV2", EventName(activeEvent));
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

            if (IsBatteryActive(data.Id))
            {
                amount *= config.CursedItems.Battery.Multiplier;
                reason += Lang("BatteryTag", null, config.CursedItems.Battery.Multiplier);
            }

            ChangeBaldness(data, amount, true, reason);
        }

        #endregion

        #region On-screen UI

        private const string UiCounter = "IslaDeCalvos.Counter";
        private const string UiDelta = "IslaDeCalvos.Delta";
        private const string UiBanner = "IslaDeCalvos.Banner";

        private readonly Dictionary<ulong, Timer> deltaTimers = new Dictionary<ulong, Timer>();
        private Timer bannerTimer;

        // The client is ready for UI once the player wakes up (after connecting and after every respawn).
        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (IsRealPlayer(player))
            {
                DrawCounter(player);
            }
        }

        private void DrawCounter(BasePlayer player)
        {
            if (!config.Ui.ShowCounter || player == null || !player.IsConnected)
            {
                return;
            }

            PlayerData data = GetOrCreateData(player);
            UiConfig ui = config.Ui;
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.55" },
                RectTransform = { AnchorMin = ui.CounterAnchorMin, AnchorMax = ui.CounterAnchorMax, OffsetMin = ui.CounterOffsetMin, OffsetMax = ui.CounterOffsetMax }
            }, "Hud", UiCounter, UiCounter);
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = Lang("HudCounter", player.UserIDString, FormatBaldness(data.Baldness), GetTitle(data.Baldness)),
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiCounter);
            CuiHelper.AddUi(player, container);
        }

        // Redraws the counter of an online player and pops a +X / -X next to it for a moment.
        private void RefreshCounter(PlayerData data, long delta)
        {
            BasePlayer player = BasePlayer.FindByID(data.Id);
            if (!config.Ui.ShowCounter || player == null || !player.IsConnected || player.IsSleeping())
            {
                return;
            }

            DrawCounter(player);

            UiConfig ui = config.Ui;
            // Same width as the counter: below it when the counter is in the top half of the screen, above otherwise.
            bool below = CounterIsOnTop();
            string popupMin = below ? ShiftY(ui.CounterOffsetMin, -28) : ShiftY(ui.CounterOffsetMax, 2, ui.CounterOffsetMin);
            string popupMax = below ? ShiftY(ui.CounterOffsetMin, -2, ui.CounterOffsetMax) : ShiftY(ui.CounterOffsetMax, 28);

            var container = new CuiElementContainer();
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = (delta > 0 ? "+" : string.Empty) + FormatBaldness(delta),
                    FontSize = 18,
                    Align = TextAnchor.MiddleCenter,
                    Color = delta > 0 ? "0.94 0.75 0.25 1" : "0.88 0.31 0.31 1",
                    FadeIn = 0.2f
                },
                RectTransform = { AnchorMin = ui.CounterAnchorMin, AnchorMax = ui.CounterAnchorMax, OffsetMin = popupMin, OffsetMax = popupMax },
                FadeOut = 0.5f
            }, "Hud", UiDelta, UiDelta);
            CuiHelper.AddUi(player, container);

            if (deltaTimers.TryGetValue(data.Id, out Timer previous))
            {
                previous?.Destroy();
            }

            deltaTimers[data.Id] = timer.Once(ui.DeltaSeconds, () =>
            {
                deltaTimers.Remove(data.Id);
                if (player != null && player.IsConnected)
                {
                    CuiHelper.DestroyUi(player, UiDelta);
                }
            });
        }

        // Event messages also go to the chat; the banner is the big version in the middle of the screen.
        private void BroadcastEvent(string key, params object[] args)
        {
            Broadcast(key, args);
            if (!config.Ui.ShowEventBanner)
            {
                return;
            }

            string message = Lang(key, null, args);
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (!IsRealPlayer(player) || !player.IsConnected)
                {
                    continue;
                }

                var container = new CuiElementContainer();
                container.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.6" },
                    RectTransform = { AnchorMin = "0.2 0.72", AnchorMax = "0.8 0.82" },
                    FadeOut = 0.8f
                }, "Hud", UiBanner, UiBanner);
                container.Add(new CuiLabel
                {
                    Text = { Text = message, FontSize = 22, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", FadeIn = 0.3f },
                    RectTransform = { AnchorMin = "0.02 0", AnchorMax = "0.98 1" },
                    FadeOut = 0.8f
                }, UiBanner);
                CuiHelper.AddUi(player, container);
            }

            bannerTimer?.Destroy();
            bannerTimer = timer.Once(config.Ui.BannerSeconds, () =>
            {
                bannerTimer = null;
                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    CuiHelper.DestroyUi(player, UiBanner);
                }
            });
        }

        private static void DestroyUi(BasePlayer player)
        {
            if (player == null)
            {
                return;
            }

            CuiHelper.DestroyUi(player, UiCounter);
            CuiHelper.DestroyUi(player, UiDelta);
            CuiHelper.DestroyUi(player, UiBanner);
            CuiHelper.DestroyUi(player, UiMenu);
        }

        private bool CounterIsOnTop()
        {
            string[] anchor = config.Ui.CounterAnchorMin.Split(' ');
            return anchor.Length == 2 && float.TryParse(anchor[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) && y >= 0.5f;
        }

        // "x y" offset with y moved by dy; x taken from xFrom (defaults to the same offset).
        private static string ShiftY(string offset, float dy, string xFrom = null)
        {
            string[] a = offset.Split(' ');
            string[] b = (xFrom ?? offset).Split(' ');
            if (a.Length != 2 || b.Length != 2 ||
                !float.TryParse(a[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
            {
                return offset;
            }

            return b[0] + " " + (y + dy).ToString(CultureInfo.InvariantCulture);
        }

        #endregion

        #region El Calvario (cursed items + ranking menu)

        private const string UiMenu = "IslaDeCalvos.Menu";

        // Pages of the barber conversation.
        private enum BarberPage
        {
            Main,
            Items,
            Carne
        }

        // Item keys used by the menu buttons and the config.
        private static readonly string[] CursedItemKeys = { "bleach", "ducttape", "battery", "dogtag", "bluedogtags", "reddogtags", "gems" };

        private readonly Dictionary<ulong, DateTime> batteryUntil = new Dictionary<ulong, DateTime>();
        private readonly HashSet<string> warnedMissingItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ItemDropConfig CursedItemConfig(string key)
        {
            CursedItemsConfig c = config.CursedItems;
            switch (key)
            {
                case "bleach": return c.Bleach;
                case "ducttape": return c.DuctTape;
                case "battery": return c.Battery;
                case "dogtag": return c.DogTag;
                case "bluedogtags": return c.BlueDogTags;
                case "reddogtags": return c.RedDogTags;
                case "gems": return c.Gems;
                default: return null;
            }
        }

        private string CursedItemName(string key)
        {
            switch (key)
            {
                case "bleach": return Lang("CalvarioItemBleach");
                case "ducttape": return Lang("CalvarioItemDuctTape");
                case "battery": return Lang("CalvarioItemBattery");
                case "dogtag": return Lang("CalvarioItemDogTag");
                case "bluedogtags": return Lang("CalvarioItemBlueDogTags");
                case "reddogtags": return Lang("CalvarioItemRedDogTags");
                case "gems": return Lang("CalvarioItemGems");
                default: return key;
            }
        }

        private string CursedItemDescription(string key)
        {
            CursedItemsConfig c = config.CursedItems;
            switch (key)
            {
                case "bleach": return Lang("CalvarioDescBleach", null, Mathf.RoundToInt(c.Bleach.WinChance * 100f), FormatBaldness(c.Bleach.WinAmount), FormatBaldness(c.Bleach.LoseAmount));
                case "ducttape": return Lang("CalvarioDescDuctTape");
                case "battery": return Lang("CalvarioDescBattery", null, c.Battery.Multiplier, c.Battery.Minutes);
                case "dogtag": return Lang("CalvarioDescDogTag", null, FormatBaldness(c.DogTag.Reward));
                case "bluedogtags": return Lang("CalvarioDescBlueDogTags", null, FormatBaldness(c.BlueDogTags.Reward));
                case "reddogtags": return Lang("CalvarioDescRedDogTags", null, FormatBaldness(c.RedDogTags.Reward));
                case "gems": return Lang("CalvarioDescGems", null, FormatBaldness(c.Gems.Reward));
                default: return string.Empty;
            }
        }

        private ItemDefinition FindItemDefinition(string shortname)
        {
            if (string.IsNullOrEmpty(shortname))
            {
                return null;
            }

            ItemDefinition definition = ItemManager.FindItemDefinition(shortname);
            if (definition == null && warnedMissingItems.Add(shortname))
            {
                PrintWarning($"Item '{shortname}' does not exist in this Rust version; fix its shortname in the config.");
            }

            return definition;
        }

        private void ValidateCursedItemNames()
        {
            foreach (string key in CursedItemKeys)
            {
                FindItemDefinition(CursedItemConfig(key).Shortname);
            }

            foreach (string shortname in config.CursedItems.IdTags.Shortnames)
            {
                FindItemDefinition(shortname);
            }
        }

        private int CountItem(BasePlayer player, string shortname)
        {
            ItemDefinition definition = FindItemDefinition(shortname);
            return definition == null || player.inventory == null ? 0 : player.inventory.GetAmount(definition.itemid);
        }

        // Gives one item straight to the inventory, or drops it at the player's feet if it is full.
        private bool GiveItem(BasePlayer player, string shortname)
        {
            ItemDefinition definition = FindItemDefinition(shortname);
            if (definition == null || player == null || player.inventory == null)
            {
                return false;
            }

            global::Item item = ItemManager.CreateByItemID(definition.itemid, 1);
            if (item == null)
            {
                return false;
            }

            if (!player.inventory.GiveItem(item))
            {
                item.Drop(player.GetDropPosition(), player.GetInheritedDropVelocity());
            }

            return true;
        }

        private bool TakeItem(BasePlayer player, string shortname)
        {
            ItemDefinition definition = FindItemDefinition(shortname);
            if (definition == null || player.inventory.GetAmount(definition.itemid) < 1)
            {
                return false;
            }

            player.inventory.Take(null, definition.itemid, 1);
            return true;
        }

        private void TryDrop(BasePlayer player, ItemDropConfig item, string displayName = null)
        {
            if (!config.CursedItems.Enabled || item == null || item.DropChance <= 0f || random.NextDouble() >= item.DropChance)
            {
                return;
            }

            if (GiveItem(player, item.Shortname))
            {
                Reply(player, "ItemFoundV3", displayName ?? CursedItemName(KeyOf(item)));
            }
        }

        private string KeyOf(ItemDropConfig item) => CursedItemKeys.FirstOrDefault(k => CursedItemConfig(k) == item) ?? item.Shortname;

        private void RollBarrelDrops(BasePlayer player)
        {
            CursedItemsConfig c = config.CursedItems;
            TryDrop(player, c.Bleach);
            TryDrop(player, c.DuctTape);
            TryDrop(player, c.Battery);
        }

        private void RollNpcDrops(BasePlayer player, int tier)
        {
            CursedItemsConfig c = config.CursedItems;
            foreach (TrophyConfig trophy in new[] { c.DogTag, c.BlueDogTags, c.Gems })
            {
                if (tier >= trophy.MinTier && tier <= trophy.MaxTier)
                {
                    TryDrop(player, trophy);
                }
            }

            IdTagsConfig tags = c.IdTags;
            if (tags.Shortnames.Count > 0 && tier >= tags.MinTier && tier <= tags.MaxTier)
            {
                string color = tags.Shortnames[random.Next(tags.Shortnames.Count)];
                TryDrop(player, new ItemDropConfig { Shortname = color, DropChance = tags.DropChance }, Lang("CalvarioItemIdTag"));
            }
        }

        private bool IsBatteryActive(ulong playerId) =>
            batteryUntil.TryGetValue(playerId, out DateTime until) && until > DateTime.UtcNow;

        private int BatteryMinutesLeft(ulong playerId) =>
            IsBatteryActive(playerId) ? (int)Math.Ceiling((batteryUntil[playerId] - DateTime.UtcNow).TotalMinutes) : 0;

        // Returns what the barber says about it (null if nothing happened).
        private string UseCursedItem(BasePlayer player, string key)
        {
            ItemDropConfig item = CursedItemConfig(key);
            if (item == null || !config.CursedItems.Enabled)
            {
                return null;
            }

            PlayerData data = GetOrCreateData(player);
            string name = CursedItemName(key);
            if (CountItem(player, item.Shortname) < 1)
            {
                return Lang("ItemNoneV2", player.UserIDString, name);
            }

            // Refuse before consuming anything.
            if (key == "ducttape" && data.HasDeathShield)
            {
                return Lang("ItemShieldAlready", player.UserIDString);
            }

            if (key == "battery" && IsBatteryActive(data.Id))
            {
                return Lang("ItemBatteryAlreadyV2", player.UserIDString, BatteryMinutesLeft(data.Id));
            }

            if (!TakeItem(player, item.Shortname))
            {
                return null;
            }

            string reason = Lang("ReasonItemUse", null, name);
            switch (key)
            {
                case "bleach":
                    BleachConfig bleach = config.CursedItems.Bleach;
                    if (random.NextDouble() < bleach.WinChance)
                    {
                        GainBaldness(data, bleach.WinAmount, reason);
                        return Lang("ItemBleachWin", player.UserIDString, FormatBaldness(bleach.WinAmount));
                    }

                    ChangeBaldness(data, -bleach.LoseAmount, true, reason);
                    return Lang("ItemBleachFail", player.UserIDString, FormatBaldness(bleach.LoseAmount));
                case "ducttape":
                    data.HasDeathShield = true;
                    dataDirty = true;
                    return Lang("ItemShieldOnV2", player.UserIDString);
                case "battery":
                    BatteryConfig battery = config.CursedItems.Battery;
                    ulong id = data.Id;
                    batteryUntil[id] = DateTime.UtcNow.AddMinutes(battery.Minutes);
                    timer.Once(battery.Minutes * 60f, () =>
                    {
                        if (!IsBatteryActive(id))
                        {
                            batteryUntil.Remove(id);
                            BasePlayer owner = BasePlayer.FindByID(id);
                            if (owner != null && owner.IsConnected)
                            {
                                Reply(owner, "ItemBatteryOffV2");
                            }
                        }
                    });
                    return Lang("ItemBatteryOnV2", player.UserIDString, battery.Multiplier, battery.Minutes);
                default:
                    long reward = ((TrophyConfig)item).Reward;
                    GainBaldness(data, reward, reason);
                    return Lang("BarberTrophyUsed", player.UserIDString, name, FormatBaldness(reward));
            }
        }

        // Delivers one tag of every color the player carries and has not delivered yet. Returns the barber's line.
        private string DeliverIdTags(BasePlayer player)
        {
            IdTagsConfig tags = config.CursedItems.IdTags;
            PlayerData data = GetOrCreateData(player);
            int delivered = 0;
            foreach (string color in tags.Shortnames)
            {
                if (!data.CarneColors.Contains(color) && TakeItem(player, color))
                {
                    data.CarneColors.Add(color);
                    delivered++;
                }
            }

            if (delivered == 0)
            {
                return Lang("CarneNothingV2", player.UserIDString);
            }

            dataDirty = true;
            string line = Lang("CarneDeliveredV2", player.UserIDString, delivered, FormatBaldness(delivered * tags.Reward));
            GainBaldness(data, delivered * tags.Reward, Lang("ReasonCarne"));

            if (tags.Shortnames.All(c => data.CarneColors.Contains(c)))
            {
                data.CarneColors.Clear();
                data.CarnesCompleted++;
                BroadcastEvent("CarneCompletedV2", data.Name, FormatBaldness(tags.CollectionBonus));
                ChangeBaldness(data, tags.CollectionBonus, true, Lang("ReasonCarne"));
            }

            return line;
        }

        #region Menu UI

        [ConsoleCommand("calvos.tab")]
        private void CcmdTab(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!IsRealPlayer(player))
            {
                return;
            }

            string[] parts = MenuArgs(arg);
            int page = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 0;
            OpenRanking(player, page);
        }

        [ConsoleCommand("calvos.use")]
        private void CcmdUse(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] parts = MenuArgs(arg);
            if (!IsRealPlayer(player) || parts.Length == 0 || !RequireCalvarioNpc(player))
            {
                return;
            }

            OpenBarber(player, BarberPage.Items, UseCursedItem(player, parts[0]));
        }

        [ConsoleCommand("calvos.carne")]
        private void CcmdCarne(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!IsRealPlayer(player) || !RequireCalvarioNpc(player))
            {
                return;
            }

            OpenBarber(player, BarberPage.Carne, DeliverIdTags(player));
        }

        // HumanNPC hook: called when a player presses USE on one of its NPCs (5 m max).
        private void OnUseNPC(BasePlayer npc, BasePlayer player)
        {
            if (npc == null || !IsRealPlayer(player) || !calvarioNpcIds.Contains((ulong)npc.userID))
            {
                return;
            }

            calvarioNpcInUse[(ulong)player.userID] = npc;
            OpenBarber(player, BarberPage.Main, BarberGreeting(player.UserIDString));
        }

        // Items can only be used next to the Calvario NPC the player talked to; console commands can be typed anywhere.
        private bool RequireCalvarioNpc(BasePlayer player)
        {
            if (calvarioNpcInUse.TryGetValue((ulong)player.userID, out BasePlayer npc) && npc != null && !npc.IsDead()
                && Vector3.Distance(player.transform.position, npc.transform.position) <= config.BarberShop.MaxDistance)
            {
                return true;
            }

            calvarioNpcInUse.Remove((ulong)player.userID);
            CuiHelper.DestroyUi(player, UiMenu);
            Reply(player, "CalvarioGoToBarber");
            return false;
        }

        private static string[] MenuArgs(ConsoleSystem.Arg arg) =>
            arg.HasArgs() ? arg.FullString.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];

        // Barber-shop palette: leather browns, bald-head cream and barber-pole stripes.
        private const string ColorWindow = "0.12 0.07 0.06 0.97";
        private const string ColorCard = "0.2 0.12 0.1 1";
        private const string ColorCardDark = "0.26 0.17 0.14 1";
        private const string ColorScalp = "0.96 0.83 0.66 1";
        private const string ColorText = "0.88 0.82 0.75 1";
        private const string ColorMuted = "0.62 0.54 0.47 1";
        private const string ColorPoleRed = "0.72 0.14 0.14 1";
        private const string ColorPoleWhite = "0.93 0.9 0.85 1";
        private const string ColorPoleBlue = "0.16 0.3 0.62 1";
        private const string ColorGood = "0.35 0.5 0.25 1";
        private const string ColorDisabled = "0.28 0.2 0.18 1";
        // Proverb 6 was removed in 1.4.1; its lang key is gone, so the numbers skip it.
        private static readonly int[] ProverbNumbers = { 1, 2, 3, 4, 5, 7, 8 };

        // Salon de la fama calva (/calvos). The cursed items live with the barber (OpenBarber).
        private void OpenRanking(BasePlayer player, int page)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            var ui = new CuiElementContainer();

            // Full-screen dim layer that grabs the mouse; the window sits on top of it.
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UiMenu, UiMenu);

            string window = ui.Add(new CuiPanel
            {
                Image = { Color = ColorWindow },
                RectTransform = { AnchorMin = "0.17 0.1", AnchorMax = "0.83 0.9" }
            }, UiMenu);

            // Barber pole along the top edge.
            string[] pole = { ColorPoleRed, ColorPoleWhite, ColorPoleBlue, ColorPoleWhite };
            const int stripes = 32;
            for (int i = 0; i < stripes; i++)
            {
                AddPanel(ui, window, pole[i % pole.Length], Anchor(i / (float)stripes, 0.988f), Anchor((i + 1) / (float)stripes, 1f));
            }

            AddText(ui, window, Lang("CalvarioTitle", userId), 26, TextAnchor.MiddleLeft, "0.03 0.915", "0.45 0.975", ColorScalp);
            AddText(ui, window, Lang("CalvarioSubtitleV2", userId), 11, TextAnchor.MiddleLeft, "0.03 0.88", "0.6 0.915", ColorMuted);
            AddText(ui, window, Lang("CalvarioYou", userId, FormatBaldness(data.Baldness), GetTitle(data.Baldness)), 15, TextAnchor.MiddleRight, "0.45 0.93", "0.935 0.975");
            DrawTitleProgress(ui, window, data.Baldness, userId);
            AddButton(ui, window, Lang("CalvarioClose", userId), "0.956 0.935", "0.99 0.985", ColorPoleRed, null, UiMenu, 18);

            string section = AddPanel(ui, window, ColorScalp, "0.03 0.81", "0.35 0.86");
            AddText(ui, section, Lang("CalvarioTabRanking", userId), 13, TextAnchor.MiddleCenter, "0 0", "1 1", "0.12 0.07 0.06 1");

            DrawRankingTab(ui, window, data, page, userId);
            CuiHelper.AddUi(player, ui);
        }

        // Thin bar under the header: how far you are from the next title.
        private void DrawTitleProgress(CuiElementContainer ui, string window, long baldness, string userId)
        {
            int tier = GetTierIndex(baldness);
            if (tier >= config.Titles.Count - 1 && baldness >= config.Titles[config.Titles.Count - 1].MinBaldness)
            {
                AddText(ui, window, Lang("CalvarioTop", userId), 11, TextAnchor.MiddleRight, "0.45 0.88", "0.935 0.925", ColorScalp);
                return;
            }

            TitleTier current = config.Titles[tier];
            TitleTier next = baldness < current.MinBaldness ? current : config.Titles[tier + 1];
            long from = baldness < current.MinBaldness ? 0 : current.MinBaldness;
            float progress = next.MinBaldness > from ? Math.Max(0f, Math.Min(1f, (baldness - from) / (float)(next.MinBaldness - from))) : 1f;

            AddText(ui, window, Lang("CalvarioNextV2", userId, next.Name, FormatBaldness(next.MinBaldness - baldness)), 11, TextAnchor.MiddleRight, "0.45 0.898", "0.935 0.925", ColorMuted);
            AddPanel(ui, window, ColorCardDark, "0.62 0.884", "0.935 0.896");
            if (progress > 0f)
            {
                AddPanel(ui, window, ColorScalp, "0.62 0.884", Anchor(0.62f + 0.315f * progress, 0.896f));
            }
        }

        private const string ColorDialog = "0.06 0.05 0.05 0.94";
        private const string ColorDialogOption = "0.16 0.13 0.12 0.95";

        // Lang key for each ID tag color, shown in the Carne de Calvo page.
        private static readonly Dictionary<string, string> TagColorKeys = new Dictionary<string, string>
        {
            ["blueidtag"] = "TagColorBlue", ["grayidtag"] = "TagColorGray", ["greenidtag"] = "TagColorGreen",
            ["lavenderidtag"] = "TagColorLavender", ["mintidtag"] = "TagColorMint", ["orangeidtag"] = "TagColorOrange",
            ["pinkidtag"] = "TagColorPink", ["purpleidtag"] = "TagColorPurple", ["redidtag"] = "TagColorRed",
            ["whiteidtag"] = "TagColorWhite", ["yellowidtag"] = "TagColorYellow"
        };

        private const int BarberGreetingCount = 3;

        // The barber greets with one of his own lines or one of the island proverbs.
        private string BarberGreeting(string userId)
        {
            int pick = random.Next(BarberGreetingCount + ProverbNumbers.Length);
            return pick < BarberGreetingCount
                ? Lang("BarberGreeting" + (pick + 1), userId)
                : Lang("CalvarioProverb" + ProverbNumbers[pick - BarberGreetingCount], userId);
        }

        private string TagColorName(string shortname, string userId) =>
            TagColorKeys.TryGetValue(shortname, out string key) ? Lang(key, userId) : shortname;

        [ConsoleCommand("calvos.barber")]
        private void CcmdBarber(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] parts = MenuArgs(arg);
            if (!IsRealPlayer(player) || !RequireCalvarioNpc(player))
            {
                return;
            }

            switch (parts.Length > 0 ? parts[0] : "main")
            {
                case "items":
                    OpenBarber(player, BarberPage.Items, null);
                    break;
                case "carne":
                    OpenBarber(player, BarberPage.Carne, null);
                    break;
                case "bye":
                    CuiHelper.DestroyUi(player, UiMenu);
                    break;
                default:
                    OpenBarber(player, BarberPage.Main, BarberGreeting(player.UserIDString));
                    break;
            }
        }

        // Conversation box in the style of the vanilla vendors: the barber's line on top, the player's answers below.
        private void OpenBarber(BasePlayer player, BarberPage page, string line)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            var options = new List<KeyValuePair<string, string>>();

            switch (page)
            {
                case BarberPage.Items:
                    foreach (string key in CursedItemKeys)
                    {
                        int count = CountItem(player, CursedItemConfig(key).Shortname);
                        if (count > 0)
                        {
                            options.Add(new KeyValuePair<string, string>(
                                Lang("BarberItemLine", userId, CursedItemName(key), count, CursedItemDescription(key)), "calvos.use " + key));
                        }
                    }

                    if (line == null)
                    {
                        var status = new List<string> { Lang(options.Count > 0 ? "BarberItemsIntro" : "BarberItemsNone", userId) };
                        if (data.HasDeathShield) status.Add(Lang("CalvarioShieldOn", userId));
                        if (IsBatteryActive(data.Id)) status.Add(Lang("CalvarioBatteryOn", userId, BatteryMinutesLeft(data.Id)));
                        line = string.Join(" ", status.ToArray());
                    }

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                case BarberPage.Carne:
                    IdTagsConfig tags = config.CursedItems.IdTags;
                    string[] stamped = tags.Shortnames.Where(c => data.CarneColors.Contains(c)).Select(c => TagColorName(c, userId)).ToArray();
                    string[] missing = tags.Shortnames.Where(c => !data.CarneColors.Contains(c)).Select(c => TagColorName(c, userId)).ToArray();
                    var carne = new List<string>();
                    if (line != null) carne.Add(line);
                    carne.Add(Lang("BarberCarneIntro", userId, stamped.Length, tags.Shortnames.Count, data.CarnesCompleted));
                    if (stamped.Length > 0) carne.Add(Lang("BarberCarneStamped", userId, string.Join(", ", stamped)));
                    if (missing.Length > 0) carne.Add(Lang("BarberCarneMissing", userId, string.Join(", ", missing)));
                    carne.Add(Lang("CalvarioCarneHintV2", userId, FormatBaldness(tags.Reward), FormatBaldness(tags.CollectionBonus)));
                    line = string.Join("\n", carne.ToArray());

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptStamp", userId), "calvos.carne"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                default:
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptItems", userId), "calvos.barber items"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptCarne", userId), "calvos.barber carne"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBye", userId), "calvos.barber bye"));
                    break;
            }

            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true
            }, "Overlay", UiMenu, UiMenu);

            string box = ui.Add(new CuiPanel
            {
                Image = { Color = ColorDialog },
                RectTransform = { AnchorMin = "0.22 0.05", AnchorMax = "0.78 0.5" }
            }, UiMenu);

            AddText(ui, box, Lang("BarberName", userId), 18, TextAnchor.MiddleLeft, "0.03 0.88", "0.5 0.98", ColorScalp);
            AddText(ui, box, Lang("CalvarioYou", userId, FormatBaldness(data.Baldness), GetTitle(data.Baldness)), 12, TextAnchor.MiddleRight, "0.5 0.88", "0.93 0.98", ColorMuted);
            AddButton(ui, box, Lang("CalvarioClose", userId), "0.945 0.9", "0.99 0.98", ColorPoleRed, null, UiMenu, 14);
            AddPanel(ui, box, ColorScalp, "0.03 0.872", "0.97 0.876");
            AddText(ui, box, line ?? string.Empty, 14, TextAnchor.UpperLeft, "0.03 0.5", "0.97 0.855", ColorText);

            // Answers stacked from the bottom, like the vanilla dialogue options.
            const float rowHeight = 0.052f, gap = 0.006f;
            for (int i = 0; i < options.Count; i++)
            {
                int fromBottom = options.Count - 1 - i;
                float y0 = 0.03f + fromBottom * (rowHeight + gap);
                AddButton(ui, box, (i + 1) + ". " + options[i].Key, Anchor(0.03f, y0), Anchor(0.97f, y0 + rowHeight), ColorDialogOption,
                    options[i].Value, null, 12, ColorText, TextAnchor.MiddleLeft);
            }

            CuiHelper.AddUi(player, ui);
        }

        private void DrawRankingTab(CuiElementContainer ui, string window, PlayerData me, int page, string userId)
        {
            List<PlayerData> ranking = storedData.Players.Values
                .OrderByDescending(d => d.Baldness)
                .ThenByDescending(d => d.Kills)
                .ToList();

            if (ranking.Count == 0)
            {
                AddText(ui, window, Lang("CalvarioRankingEmpty", userId), 16, TextAnchor.MiddleCenter, "0.03 0.4", "0.97 0.6", ColorText);
                return;
            }

            string[] medals = { "0.96 0.8 0.3 1", "0.82 0.82 0.86 1", "0.8 0.55 0.35 1" };
            int pages = (ranking.Count + RankingPageSize - 1) / RankingPageSize;
            page = Math.Max(0, Math.Min(page, pages - 1));

            for (int i = 0; i < RankingPageSize; i++)
            {
                int index = page * RankingPageSize + i;
                if (index >= ranking.Count)
                {
                    break;
                }

                PlayerData entry = ranking[index];
                float y1 = 0.79f - i * 0.06f, y0 = y1 - 0.055f;
                string rowColor = entry == me ? "0.96 0.83 0.66 0.25" : (i % 2 == 0 ? ColorCard : ColorCardDark);
                string row = AddPanel(ui, window, rowColor, Anchor(0.03f, y0), Anchor(0.97f, y1));
                if (index < medals.Length)
                {
                    AddPanel(ui, row, medals[index], "0 0", "0.008 1");
                }

                string nameColor = index < medals.Length ? medals[index] : "1 1 1 1";
                AddText(ui, row, Lang("CalvarioRankingLine", userId, index + 1, entry.Name), 14, TextAnchor.MiddleLeft, "0.02 0", "0.5 1", nameColor);
                AddText(ui, row, FormatBaldness(entry.Baldness), 14, TextAnchor.MiddleRight, "0.5 0", "0.68 1", ColorScalp);
                AddText(ui, row, GetTitle(entry.Baldness), 13, TextAnchor.MiddleRight, "0.68 0", "0.98 1", ColorText);
            }

            int myIndex = ranking.IndexOf(me);
            string footer = myIndex <= 0
                ? Lang("CalvarioRankingFirstV2", userId)
                : Lang("CalvarioRankingYouV2", userId, myIndex + 1, ranking.Count, FormatBaldness(ranking[myIndex - 1].Baldness - me.Baldness + 1), ranking[myIndex - 1].Name);
            AddText(ui, window, footer, 14, TextAnchor.MiddleLeft, "0.03 0.1", "0.97 0.17", ColorText);

            AddText(ui, window, Lang("CalvarioPage", userId, page + 1, pages), 13, TextAnchor.MiddleCenter, "0.42 0.03", "0.58 0.09", ColorMuted);
            AddButton(ui, window, Lang("CalvarioPrev", userId), "0.25 0.03", "0.4 0.09", page > 0 ? ColorCardDark : ColorCard, page > 0 ? "calvos.tab ranking " + (page - 1) : null, null, 13, page > 0 ? ColorText : ColorMuted);
            AddButton(ui, window, Lang("CalvarioNextPage", userId), "0.6 0.03", "0.75 0.09", page < pages - 1 ? ColorCardDark : ColorCard, page < pages - 1 ? "calvos.tab ranking " + (page + 1) : null, null, 13, page < pages - 1 ? ColorText : ColorMuted);
        }

        private static string AddPanel(CuiElementContainer ui, string parent, string color, string min, string max) =>
            ui.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);

        private static string Anchor(float x, float y) =>
            x.ToString("0.####", CultureInfo.InvariantCulture) + " " + y.ToString("0.####", CultureInfo.InvariantCulture);

        private static void AddText(CuiElementContainer ui, string parent, string text, int size, TextAnchor align, string min, string max, string color = "1 1 1 1")
        {
            ui.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Align = align, Color = color },
                RectTransform = { AnchorMin = min, AnchorMax = max }
            }, parent);
        }

        // command null = disabled button (does nothing). close = element to destroy on click.
        private static void AddButton(CuiElementContainer ui, string parent, string text, string min, string max, string color, string command, string close = null, int fontSize = 13, string textColor = "1 1 1 1", TextAnchor align = TextAnchor.MiddleCenter)
        {
            ui.Add(new CuiButton
            {
                Button = { Color = color, Command = command ?? string.Empty, Close = close },
                RectTransform = { AnchorMin = min, AnchorMax = max },
                Text = { Text = (align == TextAnchor.MiddleLeft ? "  " : string.Empty) + text, FontSize = fontSize, Align = align, Color = textColor }
            }, parent);
        }

        private void AddIcon(CuiElementContainer ui, string parent, string shortname, string min, string max, string color)
        {
            ItemDefinition definition = FindItemDefinition(shortname);
            if (definition == null)
            {
                return;
            }

            ui.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { ItemId = definition.itemid, Color = color },
                    new CuiRectTransformComponent { AnchorMin = min, AnchorMax = max }
                }
            });
        }

        #endregion

        #endregion

        #region Helpers

        private static bool IsRealPlayer(BasePlayer player) => player != null && player.userID.IsSteamId();

        // Used only to decide whether an unlisted prefab deserves a console notice; rewards come from NpcTiers.
        private static bool IsPossibleNpc(BaseCombatEntity entity)
        {
            // Skinning a corpse (wolf.corpse, bear.corpse...) kills it; that is not an NPC kill.
            if (entity.ShortPrefabName.EndsWith(".corpse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

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

                RewardPointsTick(player, data);
            }
        }

        private void RewardPointsTick(BasePlayer player, PlayerData data)
        {
            ServerRewardsConfig rp = config.ServerRewards;
            if (!rp.Enabled)
            {
                return;
            }

            Vector3 position = player.transform.position;
            if (lastPositions.TryGetValue(data.Id, out Vector3 last) && Vector3.Distance(last, position) >= rp.MinMoveMeters)
            {
                data.RpMoved = true;
            }

            lastPositions[data.Id] = position;
            data.RpSeconds += SurvivalTickSeconds;
            if (data.RpSeconds < rp.IntervalMinutes * 60f)
            {
                return;
            }

            data.RpSeconds = 0f;
            bool moved = data.RpMoved;
            data.RpMoved = false;

            int amount = GetRpRate(data.Baldness);
            if (amount <= 0)
            {
                return;
            }

            if (rp.RequireMovement && !moved)
            {
                SendDebug("DebugNoRp", data.Name, Lang("NoRpAfk"));
                return;
            }

            if (ServerRewards == null || !ServerRewards.IsLoaded)
            {
                if (!warnedNoServerRewards)
                {
                    warnedNoServerRewards = true;
                    PrintWarning("Server Rewards is not loaded; no RP is being paid.");
                }

                SendDebug("DebugNoRp", data.Name, Lang("NoRpPlugin"));
                return;
            }

            // Server Rewards API: object AddPoints(object userID, int amount), returns true when paid.
            object paid = ServerRewards.Call("AddPoints", data.Id, amount);
            if (!(paid is bool ok) || !ok)
            {
                SendDebug("DebugNoRp", data.Name, Lang("NoRpRefused"));
                return;
            }

            SendDebug("DebugRp", data.Name, amount, GetTitle(data.Baldness));
            if (rp.NotifyPlayer)
            {
                Reply(player, "RpEarnedV2", amount, GetTitle(data.Baldness));
            }
        }

        private int GetRpRate(long baldness)
        {
            int amount = 0;
            foreach (KeyValuePair<long, int> rate in rpRates)
            {
                if (baldness >= rate.Key)
                {
                    amount = rate.Value;
                }
            }

            return amount;
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
            RefreshCounter(data, newValue - oldValue);

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
                    Broadcast("SupremeBaldnessV2", data.Name);
                }
                else if (config.AnnounceTitleUp)
                {
                    Broadcast("TitleUpV2", data.Name, GetTitle(newValue));
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
