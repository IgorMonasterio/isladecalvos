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
    [Info("Isla de Calvos", "Igor Monasterio", "1.10.0")]
    [Description("Baldness system for the Isla de Calvos Rust server: being bald is glory, hair is a curse.")]
    public class IslaDeCalvos : RustPlugin
    {
        #region Fields

        private const string PermAdmin = "isladecalvos.admin";
        private const long MinBaldness = 0;
        private const int RankingPageSize = 10;
        private const int HallPageSize = 3;
        private const float SurvivalTickSeconds = 60f;
        private const float CalvoDelDiaCheckSeconds = 60f;

        // The wipe winner is announced to each player this long after they wake up, so the chat is already on screen.
        private const float WipeAnnouncementDelaySeconds = 5f;

        // A map close (OnNewSave or "salon cerrar") this soon after the last one is the same wipe: the server restarts
        // with a new seed a minute after the wipe, so OnNewSave comes twice. "forzar" skips the check.
        private const double MapCloseRepeatHours = 12;

        // OnNewSave runs while the world loads, before anybody can be on RCON: the hall of fame hook waits this long
        // after OnServerInitialized.
        private const float WipeHookDelaySeconds = 60f;

        // Never title groups: the plugin only touches the groups listed in "Title groups".
        private static readonly HashSet<string> ProtectedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*", "default", "admin" };

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

        // Economics (coins): optional, only for tier prizes and the baldness exchange.
        [PluginReference] private Plugin Economics = null;

        // Tier prizes by title index (built from the config).
        private Dictionary<int, TierPrize> tierPrizes;
        private Dictionary<int, string> titleGroups = new Dictionary<int, string>();

        // Baldness exchange waiting for the player's CONFIRMAR. In memory only; the button carries no amounts.
        private readonly Dictionary<ulong, PendingExchange> pendingExchanges = new Dictionary<ulong, PendingExchange>();

        // RP per interval by title, sorted by minimum baldness (built from the config).
        private List<KeyValuePair<long, int>> rpRates;

        // Position at the previous survival tick, to tell AFK players apart. In memory only.
        private readonly Dictionary<ulong, Vector3> lastPositions = new Dictionary<ulong, Vector3>();

        private bool warnedNoServerRewards;
        private bool warnedRpCap;
        private bool warnedMissingRpPatch;

        // Barber shop: Calvario NPC ids (HumanNPC) and the NPC each player last talked to. In memory only.
        private HashSet<ulong> calvarioNpcIds;
        private readonly Dictionary<ulong, BasePlayer> calvarioNpcInUse = new Dictionary<ulong, BasePlayer>();

        // Calvo del Día: pick time in minutes after midnight (server time) and its Oxide group (null = no group). From the config.
        private int calvoDelDiaMinutes;
        private string calvoDelDiaGroup;

        private class WoundRecord
        {
            public BasePlayer Attacker;
            public bool Headshot;
        }

        #endregion

        #region Configuration

        // Bump when a release must overwrite values already saved in existing config files.
        private const int CurrentConfigVersion = 191;

        private class Configuration
        {
            // Missing in configs older than 1.3.0, so it reads as 0 and triggers the migration below.
            [JsonProperty("Config version (do not edit)")]
            public int ConfigVersion;

            [JsonProperty("Baldness gained per player kill")]
            public long KillReward = 10000;

            [JsonProperty("Baldness gained per headshot kill (instead of the normal kill reward)")]
            public long HeadshotKillReward = 10000;

            [JsonProperty("Baldness gained per survival interval")]
            public long SurvivalReward = 100;

            [JsonProperty("Survival interval (minutes alive and connected)")]
            public int SurvivalIntervalMinutes = 30;

            // Same check as the RP payout (moved at least "Minimum movement between checks" once in the interval).
            [JsonProperty("Survival reward only if the player moved during the interval (not AFK)")]
            public bool SurvivalRequireMovement = true;

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
                new TitleTier { MinBaldness = 1000, Name = "Pelambrera Lamentable" },
                new TitleTier { MinBaldness = 10000, Name = "Entradas Incipientes" },
                new TitleTier { MinBaldness = 100000, Name = "Coronilla a la Intemperie" },
                new TitleTier { MinBaldness = 1000000, Name = "Caballero de la Tonsura" },
                new TitleTier { MinBaldness = 10000000, Name = "Lord Bola de Billar" },
                new TitleTier { MinBaldness = 100000000, Name = "Su Calvísima Majestad" }
            };

            // Paid once per player and title, the first time they reach it. Defaults are the live server's values.
            [JsonProperty("Tier prizes (title minimum baldness -> prize)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, TierPrize> TierPrizes = new Dictionary<string, TierPrize>
            {
                ["1"] = new TierPrize
                {
                    Coins = 250,
                    Items = new List<PrizeItem> { new PrizeItem { Shortname = "knife.bone", Amount = 1 } },
                    Message = "Toma este trozo de hueso afilado. Empieza a raparte solito."
                },
                ["1000"] = new TierPrize { Rp = 10, Coins = 1000 },
                ["10000"] = new TierPrize { Rp = 50, Coins = 5000 },
                ["100000"] = new TierPrize
                {
                    Rp = 250,
                    Items = new List<PrizeItem> { new PrizeItem { Shortname = "explosive.timed", Amount = 4 } }
                },
                ["1000000"] = new TierPrize
                {
                    Rp = 1500,
                    Items = new List<PrizeItem> { new PrizeItem { Shortname = "minicopter", Amount = 1 } }
                },
                ["10000000"] = new TierPrize
                {
                    Rp = 5000,
                    Items = new List<PrizeItem>
                    {
                        new PrizeItem { Shortname = "metal.facemask", Amount = 1 },
                        new PrizeItem { Shortname = "metal.plate.torso", Amount = 1 }
                    }
                },
                ["100000000"] = new TierPrize { Rp = 25000 }
            };

            [JsonProperty("Tier prizes also for bought baldness")]
            public bool TierPrizesForBoughtBaldness = false;

            // Every player sits in the group of their current title and in no other group of this list; the perks of each
            // title (homes, teleport cooldowns, backpack size, chat title) are permissions granted to these groups.
            [JsonProperty("Sync title groups")]
            public bool SyncTitleGroups = true;

            [JsonProperty("Title groups (title minimum baldness -> Oxide group)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> TitleGroups = new Dictionary<string, string>
            {
                ["1"] = "calvo1", ["1000"] = "calvo2", ["10000"] = "calvo3", ["100000"] = "calvo4",
                ["1000000"] = "calvo5", ["10000000"] = "calvo6", ["100000000"] = "calvo7"
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

            [JsonProperty("Baldness exchange (El Calvario)")]
            public ExchangeConfig Exchange = new ExchangeConfig();

            [JsonProperty("Hall of fame (one entry per map wipe)")]
            public HallOfFameConfig HallOfFame = new HallOfFameConfig();

            [JsonProperty("Bounties (/cabeza)")]
            public BountyConfig Bounties = new BountyConfig();

            [JsonProperty("Calvo del Día (top alopecia gainer of the last 24 h)")]
            public CalvoDelDiaConfig CalvoDelDia = new CalvoDelDiaConfig();
        }

        // Saved on every map wipe (OnNewSave), before anything is reset, even with "Reset baldness on map wipe" off.
        private class HallOfFameConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Max entries kept (0 = no limit)")] public int MaxEntries = 0;
            [JsonProperty("Announce the winner in chat after the wipe")] public bool AnnounceWinner = true;
        }

        // Puntos de Chola on a player's head: charged at once, never refunded, paid whole to the PvP killer.
        private class BountyConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Minimum amount (Puntos de Chola)")] public long MinAmount = 10;
            [JsonProperty("Not paid if the victim was sleeping or disconnected")] public bool NotOnSleepers = true;
        }

        private class CalvoDelDiaConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Pick time (server time, HH:mm)")] public string PickTime = "19:00";

            // Only the current Calvo del Día is in it. Empty = no group.
            [JsonProperty("Oxide group")] public string Group = "calvodeldia";

            [JsonProperty("History entries kept")] public int HistorySize = 30;

            // Same shape as the tier prizes; all zero by default.
            [JsonProperty("Prize")] public TierPrize Prize = new TierPrize();
        }

        // El Calvario opens from a HumanNPC at the barber shop; /calvos only shows the ranking.
        private class BarberShopConfig
        {
            [JsonProperty("Calvario NPC ids (HumanNPC userid)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> CalvarioNpcIds = new List<ulong> { 4211000001UL };

            [JsonProperty("Max distance to the Calvario NPC to use items (meters)")] public float MaxDistance = 5f;
        }

        private class TierPrize
        {
            [JsonProperty("RP (Server Rewards)")] public int Rp = 0;
            [JsonProperty("Coins (Economics)")] public long Coins = 0;

            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PrizeItem> Items = new List<PrizeItem>();

            // Said to the player along with the prize, as written (no lang key: each prize has its own).
            [JsonProperty("Message")] public string Message = string.Empty;

            [JsonIgnore]
            public bool IsEmpty => Rp <= 0 && Coins <= 0 && (Items == null || Items.All(i => i == null || i.Amount <= 0))
                && string.IsNullOrEmpty(Message);
        }

        private class PrizeItem
        {
            [JsonProperty("Item shortname")] public string Shortname = string.Empty;
            [JsonProperty("Amount")] public int Amount = 1;
        }

        // Selling baldness is cheap and buying it is expensive on purpose: baldness pays RP every 30 min forever.
        private class ExchangeConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Sell: baldness for 1 RP")] public long SellBaldnessPerRp = 100;
            [JsonProperty("Sell: coins per 100 baldness")] public long SellCoinsPer100 = 25;
            [JsonProperty("Buy: RP per 1 baldness")] public long BuyRpPerBaldness = 1;
            [JsonProperty("Buy: coins per 1 baldness")] public long BuyCoinsPerBaldness = 25;
            [JsonProperty("Minimum baldness to sell")] public long MinSell = 100;

            [JsonProperty("Amounts offered (baldness)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<long> Amounts = new List<long> { 100, 1000, 10000, 100000 };
        }

        // Pays Server Rewards RP to bald players who stay alive, connected and not AFK.
        private class ServerRewardsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;

            [JsonProperty("Interval (minutes alive and connected)")] public int IntervalMinutes = 30;

            [JsonProperty("Only pay players who moved during the interval (not AFK)")] public bool RequireMovement = true;

            [JsonProperty("Minimum movement between checks to count as active (meters)")] public float MinMoveMeters = 1f;

            [JsonProperty("Tell the player in chat when RP is paid")] public bool NotifyPlayer = true;

            // Linear RP: floor(baldness / X), no cap. 0 = use the table below instead.
            [JsonProperty("RP per X baldness (0 = use the table)")] public long RpPerBaldness = 100;

            // Only used with "RP per X baldness" at 0. Empty by default, as on the live server.
            [JsonProperty("RP per interval by title (minimum baldness -> RP)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, int> RpByMinBaldness = new Dictionary<string, int>();
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

            // Every item this plugin hands out carries this skin, and the barber only counts and takes items with it,
            // so the same item from normal loot (or a scientist's dog tag) is worth nothing. Changing it orphans items already out.
            [JsonProperty("Skin ID that marks the items this plugin hands out")]
            public ulong MarkSkin = 9202609270UL;
        }

        private class UiConfig
        {
            [JsonProperty("Show baldness counter")]
            public bool ShowCounter = true;

            // Puntos de Chola and pelones in a thin strip right under the counter (only with the counter on).
            [JsonProperty("Show wallet under the counter")]
            public bool ShowWallet = true;

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

            [JsonProperty("Show a banner to everyone when a player rises to a higher title")]
            public bool ShowTitleUpBanner = true;

            [JsonProperty("Seconds the title-up banner stays")]
            public float TitleUpBannerSeconds = 6f;

            // Uses the title-up banner's seconds.
            [JsonProperty("Show title drop banner")]
            public bool ShowTitleDropBanner = true;
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
            public BladeStormConfig BladeStorm = new BladeStormConfig();
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

        private class BladeStormConfig
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
            if (events.BladeStorm == null) events.BladeStorm = new BladeStormConfig();
            events.IntervalMinutes = Math.Max(1, events.IntervalMinutes);
            events.BaldHour.DurationMinutes = Math.Max(1, events.BaldHour.DurationMinutes);
            events.ShampooRain.DurationMinutes = Math.Max(1, events.ShampooRain.DurationMinutes);
            events.HairiestHunt.DurationMinutes = Math.Max(1, events.HairiestHunt.DurationMinutes);
            events.BladeStorm.DurationMinutes = Math.Max(1, events.BladeStorm.DurationMinutes);
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

            if (config.ConfigVersion < 191)
            {
                config.Ui.ShowWallet = new UiConfig().ShowWallet;
                PrintWarning("Config updated to 1.9.1: wallet (Puntos de Chola and pelones) under the baldness counter.");
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

            config.Ui.TitleUpBannerSeconds = Math.Max(1f, config.Ui.TitleUpBannerSeconds);
            if (config.Exchange == null) config.Exchange = new ExchangeConfig();
            ExchangeConfig ex = config.Exchange;
            ex.SellBaldnessPerRp = Math.Max(1, ex.SellBaldnessPerRp);
            ex.SellCoinsPer100 = Math.Max(0, ex.SellCoinsPer100);
            ex.BuyRpPerBaldness = Math.Max(1, ex.BuyRpPerBaldness);
            ex.BuyCoinsPerBaldness = Math.Max(1, ex.BuyCoinsPerBaldness);
            ex.MinSell = Math.Max(1, ex.MinSell);
            ex.Amounts = (ex.Amounts ?? new List<long>()).Where(a => a > 0).Distinct().OrderBy(a => a).ToList();

            if (config.TierPrizes == null) config.TierPrizes = new Dictionary<string, TierPrize>();
            tierPrizes = new Dictionary<int, TierPrize>();
            foreach (KeyValuePair<string, TierPrize> entry in config.TierPrizes)
            {
                if (entry.Value == null) continue;
                int index = long.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long min)
                    ? config.Titles.FindIndex(t => t.MinBaldness == min) : -1;
                if (index < 0)
                {
                    PrintWarning($"Tier prizes: '{entry.Key}' is not the minimum baldness of any title; ignored.");
                    continue;
                }

                tierPrizes[index] = entry.Value;
            }

            if (config.TitleGroups == null) config.TitleGroups = new Dictionary<string, string>();
            titleGroups = new Dictionary<int, string>();
            foreach (KeyValuePair<string, string> entry in config.TitleGroups)
            {
                string group = entry.Value?.Trim();
                if (string.IsNullOrEmpty(group)) continue;
                if (ProtectedGroups.Contains(group))
                {
                    // Removing players from these would be a disaster; "*" even means "all groups" to RemoveUserGroup.
                    PrintWarning($"Title groups: '{group}' cannot be a title group; ignored.");
                    continue;
                }

                int index = long.TryParse(entry.Key, NumberStyles.Integer, CultureInfo.InvariantCulture, out long min)
                    ? config.Titles.FindIndex(t => t.MinBaldness == min) : -1;
                if (index < 0)
                {
                    PrintWarning($"Title groups: '{entry.Key}' is not the minimum baldness of any title; ignored.");
                    continue;
                }

                titleGroups[index] = group;
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
            rp.RpPerBaldness = Math.Max(0, rp.RpPerBaldness);
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

            if (config.HallOfFame == null) config.HallOfFame = new HallOfFameConfig();
            config.HallOfFame.MaxEntries = Math.Max(0, config.HallOfFame.MaxEntries);

            if (config.Bounties == null) config.Bounties = new BountyConfig();
            config.Bounties.MinAmount = Math.Max(1, config.Bounties.MinAmount);

            if (config.CalvoDelDia == null) config.CalvoDelDia = new CalvoDelDiaConfig();
            CalvoDelDiaConfig day = config.CalvoDelDia;
            day.HistorySize = Math.Max(1, day.HistorySize);
            if (day.Prize == null) day.Prize = new TierPrize();
            if (!TryParseClock(day.PickTime, out calvoDelDiaMinutes))
            {
                string fallback = new CalvoDelDiaConfig().PickTime;
                PrintWarning($"Calvo del Día: '{day.PickTime}' is not a time (HH:mm); using {fallback}.");
                day.PickTime = fallback;
                TryParseClock(fallback, out calvoDelDiaMinutes);
            }

            calvoDelDiaGroup = day.Group?.Trim();
            if (string.IsNullOrEmpty(calvoDelDiaGroup))
            {
                calvoDelDiaGroup = null;
            }
            else if (ProtectedGroups.Contains(calvoDelDiaGroup) || titleGroups.Values.Contains(calvoDelDiaGroup, StringComparer.OrdinalIgnoreCase))
            {
                // A title group would be emptied by SyncTitleGroup, and the protected ones must never be touched.
                PrintWarning($"Calvo del Día: '{calvoDelDiaGroup}' cannot be its group (protected or a title group); no group is used.");
                calvoDelDiaGroup = null;
            }
        }

        // "21:00" -> 1260 minutes after midnight. Hours 0-23, minutes 0-59.
        private static bool TryParseClock(string text, out int minutes)
        {
            minutes = 0;
            string[] parts = (text ?? string.Empty).Trim().Split(':');
            if (parts.Length != 2
                || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int mins)
                || hours < 0 || hours > 23 || mins < 0 || mins > 59)
            {
                return false;
            }

            minutes = hours * 60 + mins;
            return true;
        }

        #endregion

        #region Data

        private class StoredData
        {
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();

            // Hall of fame, oldest entry first. Numbers are never reused, so "/calvoadmin salon borrar <n>" is stable.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HallEntry> HallOfFame = new List<HallEntry>();

            public int HallNextNumber = 1;

            // Entry whose winner is announced to each player the first time they wake up after the wipe (0 = nothing
            // pending). It stays until the next map close; WipeAnnouncementSeen lists who already got it.
            public int PendingWipeAnnouncement;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<ulong> WipeAnnouncementSeen = new HashSet<ulong>();

            // Entry saved by OnNewSave whose OnIslaWipeHallOfFame hook still has to be called (0 = none), after the boot.
            public int PendingWipeHook;

            // Server time of the last map close (OnNewSave or "salon cerrar"), for the repeat check. Never = default.
            public DateTime LastMapClose;

            // Puntos de Chola on each player's head. Kept across wipes; only a PvP kill or an admin removes them.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<ulong, long> Bounties = new Dictionary<ulong, long>();

            public CalvoDelDiaData CalvoDelDia = new CalvoDelDiaData();
        }

        private class HallEntry
        {
            public int Number;

            // Server time when the entry was saved.
            public DateTime Date;

            // Saved with "/calvoadmin salon guardar" (a snapshot, the map goes on) instead of by a map close.
            public bool Manual;

            // Top 3 by alopecia (Value = alopecia).
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<HallPlayer> Podium = new List<HallPlayer>();

            // Most kills and most deaths of that map (Value = count); null if nobody had any.
            public HallPlayer TopKiller;
            public HallPlayer TopDeaths;
        }

        private class HallPlayer
        {
            public ulong Id;
            public string Name = string.Empty;
            public long Value;
        }

        private class CalvoDelDiaData
        {
            // Server date (yyyy-MM-dd) of the last scheduled pick, so it runs once a day and not again after a restart.
            public string LastPickDate = string.Empty;

            // Alopecia of every player at the last pick; the next pick measures gains against it. Never taken = default.
            public DateTime SnapshotTime;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<ulong, long> Snapshot = new Dictionary<ulong, long>();

            // Players connected at some point since the snapshot: only they can be picked.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public HashSet<ulong> Seen = new HashSet<ulong>();

            // Current Calvo del Día (0 = none); their record is the last one in History.
            public ulong CurrentId;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<CalvoDelDiaRecord> History = new List<CalvoDelDiaRecord>();
        }

        private class CalvoDelDiaRecord
        {
            public DateTime Date;
            public ulong Id;
            public string Name = string.Empty;
            public long Gained;
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

            // Kills and deaths on the current map only: they go up with Kills/Deaths and back to 0 on every wipe.
            public int WipeKills;
            public int WipeDeaths;

            // El Calvario: duct tape shield, delivered ID tag colors and completed collections.
            public bool HasDeathShield;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> CarneColors = new List<string>();

            public int CarnesCompleted;

            // Seconds alive and connected since the last survival reward (or since the last death), and whether the player moved.
            public float SurvivalSeconds;
            public bool SurvivalMoved;

            // Server Rewards: seconds alive and connected since the last RP payout, and whether the player moved.
            public float RpSeconds;
            public bool RpMoved;

            // Highest title index whose tier prize was already handled (-1 = not initialized yet).
            public int PrizedTier = -1;
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

            if (storedData.HallOfFame == null) storedData.HallOfFame = new List<HallEntry>();
            storedData.HallOfFame.RemoveAll(e => e == null);
            foreach (HallEntry entry in storedData.HallOfFame)
            {
                if (entry.Podium == null) entry.Podium = new List<HallPlayer>();
                entry.Podium.RemoveAll(p => p == null);
            }

            int lastNumber = storedData.HallOfFame.Count > 0 ? storedData.HallOfFame.Max(e => e.Number) : 0;
            storedData.HallNextNumber = Math.Max(storedData.HallNextNumber, lastNumber + 1);
            if (storedData.WipeAnnouncementSeen == null) storedData.WipeAnnouncementSeen = new HashSet<ulong>();

            if (storedData.Bounties == null) storedData.Bounties = new Dictionary<ulong, long>();
            if (storedData.CalvoDelDia == null) storedData.CalvoDelDia = new CalvoDelDiaData();
            CalvoDelDiaData day = storedData.CalvoDelDia;
            if (day.LastPickDate == null) day.LastPickDate = string.Empty;
            if (day.Snapshot == null) day.Snapshot = new Dictionary<ulong, long>();
            if (day.Seen == null) day.Seen = new HashSet<ulong>();
            if (day.History == null) day.History = new List<CalvoDelDiaRecord>();
            day.History.RemoveAll(r => r == null);
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
                ["BarberOptExchangeV2"] = "Vengo a vender (o comprar) alopecia",
                ["BarberExIntroV4"] = "Aquí la alopecia se compra y se vende. Vender sale barato y comprar sale caro: esto es un negocio, no una ONG.\nTienes {0} de alopecia · {1} Puntos de Chola · {2} pelones.",
                ["BarberExSellRpV4"] = "Vender alopecia por Puntos de Chola (cada {0} de alopecia, 1 Punto de Chola)",
                ["BarberExSellCoinsV4"] = "Vender alopecia por pelones (cada 100 de alopecia, {0} pelones)",
                ["BarberExBuyRpV5"] = "Comprar alopecia con Puntos de Chola ({0} PdC cada 1 de alopecia)",
                ["BarberExBuyCoinsV4"] = "Comprar alopecia con pelones ({0} pelones cada 1 de alopecia)",
                ["BarberExClosedV3"] = "{0}  [cerrado: falta {1}. Vuelve cuando el jefe lo arregle]",
                ["BarberExPickAmount"] = "¿Cuánto? Piénsatelo bien, que luego lloras.",
                ["BarberExSellLineV2"] = "Dar {0} de alopecia y llevarme {1}",
                ["BarberExBuyLineV2"] = "Pagar {1} y llevarme {0} de alopecia",
                ["BarberExTooMuchV3"] = "{0}  [no te llega, tieso]",
                ["BarberExConfirmSellV2"] = "¿Seguro que cambias {0} de alopecia por {1}? Te va a volver a salir pelo, y eso no se paga con nada.",
                ["BarberExConfirmBuyV2"] = "¿Seguro que pagas {1} por {0} de alopecia? Aquí no hay devoluciones ni hoja de reclamaciones.",
                ["BarberExConfirm"] = "CONFIRMAR",
                ["BarberExCancel"] = "ME LO PIENSO",
                ["BarberExDoneSellV2"] = "Hecho: -{0} de alopecia y +{1}. Ya te asoma pelusilla, traidor.",
                ["BarberExDoneBuyV2"] = "Hecho: +{0} de alopecia y -{1}. Brillas como una bola de billar recién encerada.",
                ["BarberExNotEnough"] = "No te llega. Ni para un afeitado de barrio.",
                ["BarberExFailed"] = "Algo ha petado y no se ha tocado nada. Prueba otra vez, que la maquinilla tiene sus días.",
                ["UnitRpV2"] = "{0} Puntos de Chola",
                ["UnitRpOneV2"] = "{0} Punto de Chola",
                ["UnitCoinsV2"] = "{0} pelones",
                ["UnitCoinsOneV2"] = "{0} pelón",
                ["TitleUpBanner"] = "¡{0} ya es {1}! Gafas de sol, que deslumbra.",
                ["TierPrizeV2"] = "<color=#e0a526>Premio por ascender a {0}:</color> {1}. Invita la casa, que tú no tienes ni para peine.",
                ["TierPrizeItems"] = "<color=#e0a526>Premio por ascender a {0}:</color> ya lo tienes en el inventario (o a tus pies, si no te cabe). Invita la casa.",
                ["TitleDropBanner"] = "{0} baja a {1}. Ya no se le ve el cartón.",
                ["DebugTierPrize"] = "[debug] {0}: premio de {1} · {2}",
                ["ReasonExchange"] = "cambio en el Calvario",
                ["BarberGreetingV2_1"] = "Siéntate, peludo. ¿Qué te quito hoy, el pelo o la dignidad?",
                ["BarberGreetingV2_2"] = "Pasa, pasa. Esa melena no se va a arrancar sola, y yo cobro por minuto.",
                ["BarberGreetingV2_3"] = "Otra vez tú. Cada día más frente y menos vergüenza, así me gusta.",
                ["BarberOptItemsV3"] = "Traigo una reliquia",
                ["BarberOptCatalog"] = "Enséñame el catálogo",
                ["BarberOptCarne"] = "Vengo a sellar el Carné de Calvo",
                ["BarberOptBye"] = "Nada, solo miraba",
                ["BarberOptBack"] = "Volver",
                ["BarberOptStamp"] = "Séllame lo que traigo",
                ["BarberItemsIntroV3"] = "A ver qué reliquias traes en esos bolsillos. Si es un bocadillo, no cuenta.",
                ["BarberItemsNoneV3"] = "No llevas ninguna reliquia encima. Vuelve cuando hayas matado algo, que así no se asciende.",
                ["BarberItemLine"] = "{0} (tienes {1}): {2}",
                ["BarberTrophyUsedV2"] = "{0}: +{1}. A la pared de los trofeos, al lado del peluquín del último valiente.",
                ["BarberCarneIntroV2"] = "Enséñame el carné. Llevas {0} de {1} colores sellados. Carnés completos: {2}. El Ministerio no tiene prisa, pero yo sí.",
                ["BarberCarneStamped"] = "Sellados: {0}",
                ["BarberCarneMissingV2"] = "Te faltan: {0}. Ponte a matar, que no se sellan solos.",
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
                ["CalvarioYouV3"] = "Tu alopecia: <color=#e0a526>{0}</color>  —  {1}",
                ["CalvarioNextV3"] = "Hacia <color=#e0a526>{0}</color>: te faltan {1}. Sigue matando, que no se pela solo.",
                ["CalvarioTop"] = "Cima capilar alcanzada. Ya no queda nada que arrancar.",
                ["CalvarioTabRankingV2"] = "RANKING",
                ["CalvarioTabHall"] = "SALÓN DE LA FAMA",
                ["CalvarioTabBounties"] = "CABEZAS",
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
                ["CalvarioRankingEmptyV2"] = "Aún no hay nadie en el ranking. La isla está llena de pelo.",
                ["CalvarioRankingLine"] = "{0}.  {1}",
                ["CalvarioRankingYouV3"] = "Tu puesto: <color=#e0a526>{0}º</color> de {1}. Te faltan <color=#e0a526>{2}</color> para adelantar a {3}. Venga, que ese tiene hasta cejas.",
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
                ["CalvarioDescBleachV2"] = "Champú de la casa y la única reliquia que puede salir mal. {0} %: te abrasa el cuero cabelludo (+{1}). Si no, mechón rebelde (-{2}).",
                ["CalvarioDescDuctTape"] = "Parche para la calva: si mueres, el pelo ni se entera. Una vez.",
                ["CalvarioDescBattery"] = "Para la maquinilla: x{0} a todo lo que ganes durante {1} min. Bzzzz.",
                ["CalvarioDescDogTag"] = "Recuerdo de un científico con flequillo. +{0}.",
                ["CalvarioDescBlueDogTags"] = "Arrancadas a un heavy con melena. +{0}.",
                ["CalvarioDescRedDogTags"] = "Del piloto que perdió el tupé con el helicóptero. +{0}.",
                ["CalvarioDescGems"] = "Joya de la corona de Su Calvísima Majestad. Brilla como tu cabeza. +{0}.",
                ["CatalogSection"] = "RELIQUIAS",
                ["CatalogIntro"] = "Todas las reliquias de la casa. Lo que sale en gris no lo llevas encima: mirar es gratis.",
                ["CatalogBack"] = "VOLVER AL BARBERO",
                ["CatalogHave"] = "Llevas: {0}",
                ["CatalogUse"] = "USAR",
                ["CatalogNone"] = "NO LLEVAS",
                ["CatalogWisdom"] = "SABIDURÍA CALVA",
                ["CatalogCarneTitle"] = "CARNÉ DE CALVO  ·  Ministerio de Alopecia de la Isla   ({0}/{1})   ·   Carnés completos: {2}",
                ["CatalogCarneStamped"] = "SELLADA",
                ["CatalogCarneMissing"] = "FALTA",
                ["CatalogCarneDeliver"] = "SELLAR",
                ["CatalogCarneNone"] = "NADA QUE SELLAR",
                ["ItemFoundV4"] = "Has encontrado: <color=#e0a526>{0}</color>. Llévalo al Calvario de la peluquería (<color=#e0a526>/peluqueria</color>), que en el bolsillo no hace nada.",
                ["CalvarioGoToBarberV3"] = "Las reliquias se usan en el Calvario de la peluquería. Ve con <color=#e0a526>/peluqueria</color> y háblale al barbero.",
                ["ItemNoneV2"] = "No llevas {0} encima. Ni eso.",
                ["ItemShieldAlready"] = "Ya llevas la calva tapada con cinta. Muere primero.",
                ["ItemBatteryAlreadyV4"] = "La maquinilla ya está en marcha (quedan {0} min). Más rápido no va a ir, que es una maquinilla, no un Fórmula 1.",
                ["ItemBleachWinV2"] = "La lejía te ha abrasado el cuero cabelludo: +{0}. Escuece, pero brilla.",
                ["ItemBleachFail"] = "La lejía te ha dejado un mechón rebelde. Vergüenza: -{0}.",
                ["ItemShieldOnV2"] = "Te has tapado la calva con cinta americana. Tu próxima muerte no restará. Elegante no es, pero funciona.",
                ["ItemShieldUsed"] = "La cinta americana ha protegido tu calva: esta muerte no resta.",
                ["ItemBatteryOnV2"] = "Maquinilla en marcha: x{0} durante {1} min. Bzzzz.",
                ["ItemBatteryOffV2"] = "Se le ha acabado la pila a la maquinilla. Vuelves a pelarte a mano, como los pobres.",
                ["CarneNothingV2"] = "No llevas ninguna tarjeta de un color que te falte. Las repes, a tu primo.",
                ["CarneDeliveredV2"] = "Has entregado {0} tarjeta(s): +{1}. El funcionario ni te ha mirado.",
                ["CarneCompletedV3"] = "<color=#e0a526>{0}</color> ha completado el CARNÉ DE CALVO y gana +{1}. El Ministerio de Alopecia está orgulloso. Su madre, no tanto.",
                ["ReasonItemUseV2"] = "reliquia: {0}",
                ["ReasonCarne"] = "carné de calvo",
                ["SupremeBaldnessV6"] = "<color=#e0a526>{0} HA ALCANZADO LA CALVICIE SUPREMA</color>. Ya no tienes que preocuparte por el champú.",
                ["TitleUpV3"] = "<color=#e0a526>{0}</color> asciende a <color=#e0a526>{1}</color>. Su peluquero ya ha pedido el paro.",
                ["TitleDropV2"] = "<color=#e0662f>A {0} le está saliendo pelo</color> (ahora es {1})",
                ["NoPermissionV4"] = "No tienes permiso para usar este comando. Buen intento, figura.",
                ["AdminUsageV5"] = "Uso: /calvoadmin set <jugador> <valor> | /calvoadmin reset <jugador> | /calvoadmin debug on|off | /calvoadmin evento <hora|champu|peludo|alopecia|parar> | /calvoadmin salon guardar|cerrar [forzar]|borrar <n> | /calvoadmin cabeza quitar <jugador> | /calvoadmin calvodeldia ahora",
                ["AdminInvalidValue"] = "El valor tiene que ser un número entero igual o mayor que {0}.",
                ["PlayerNotFound"] = "No se ha encontrado ningún jugador con '{0}'.",
                ["PlayerAmbiguous"] = "Hay {0} jugadores que coinciden con '{1}'. Sé más concreto o usa el SteamID.",
                ["AdminSetV2"] = "Alopecia de {0} fijada en {1}.",
                ["AdminResetV2"] = "Alopecia de {0} reseteada a {1}.",
                ["DebugOnV2"] = "Debug activado: verás en el chat cada cambio de alopecia y su motivo.",
                ["DebugOff"] = "Debug desactivado.",
                ["DebugChange"] = "[debug] {0}: {1} → {2} ({3}{4}) · {5}",
                ["DebugNoRewardV2"] = "[debug] {0}: sin alopecia · {1}",
                ["DebugRpV2"] = "[debug] {0}: +{1} PdC ({2})",
                ["DebugNoRpV2"] = "[debug] {0}: sin PdC · {1}",
                ["NoRpAfk"] = "no se ha movido (AFK)",
                ["NoRpPlugin"] = "Server Rewards no está cargado",
                ["NoRpRefused"] = "Server Rewards no aceptó el pago",
                ["NoRpCapV2"] = "saldo de Puntos de Chola al tope de Server Rewards",
                ["RpEarnedV5"] = "<color=#e0a526>+{0} Puntos de Chola</color> por lucir calva de <color=#e0a526>{1}</color>. Cobra y calla.",
                ["ReasonPlayerKill"] = "kill a {0}",
                ["ReasonPlayerHeadshotKillV2"] = "rapado por headshot a {0}",
                ["ReasonDeath"] = "muerte (-{0} %)",
                ["ReasonHeadshotDeath"] = "muerte por headshot (-{0} %)",
                ["ReasonSurvival"] = "supervivencia",
                ["ReasonNpcKill"] = "NPC {0} (T{1})",
                ["ReasonEventParticipant"] = "evento {0} (T{1}), le hizo daño",
                ["ReasonEventTeammate"] = "evento {0} (T{1}), compañero de equipo cerca",
                ["ReasonAdmin"] = "admin",
                ["NoRewardSleeperV2"] = "víctima dormida o desconectada ({0}). Has sido muy valiente, enhorabuena.",
                ["NoRewardCooldown"] = "cooldown con {0}",
                ["NoRewardNpcDisabled"] = "NPC {0} desactivado en la config",
                ["NoRewardNpcUnlisted"] = "NPC {0} no está en NpcTiers",
                ["NoRewardTierMissing"] = "NPC {0} (T{1}) sin valor en TierRewards",
                ["NoRewardNpcDeath"] = "muerte por NPC (desactivado en la config)",
                ["EventNameBaldHourV3"] = "Hora de la calvicie",
                ["EventNameShampooRainV3"] = "Lluvia de champú",
                ["EventNameHairiestHuntV3"] = "Cacería del peludo",
                ["EventNameBladeStormV3"] = "Brote de alopecia",
                ["EventTag"] = " [{0} x{1}]",
                ["EventBaldHourStartV4"] = "<color=#e0a526>HORA DE LA CALVICIE</color>: durante {0} min todo da x{1} de alopecia. Salid a matar, que la frente no se despeja sola.",
                ["EventBaldHourEndV2"] = "Se acabó la Hora de la calvicie. Volvéis a pelaros a precio normal.",
                ["EventShampooRainStartV2"] = "<color=#e0662f>LLUVIA DE CHAMPÚ</color>: durante {0} min morir resta x{1}. Con este tiempo el pelo crece que da gusto.",
                ["EventShampooRainEndV2"] = "Ha escampado. Ya podéis palmar tranquilos.",
                ["EventHuntStartV4"] = "<color=#e0a526>CACERÍA DEL PELUDO</color>: {0} es el más peludo de la isla ({1}). Quien lo mate gana +{2}. Si aguanta {3} min, gana él +{4}. A por él, que esa melena no se va a cortar sola.",
                ["EventHuntKilledV4"] = "{0} ha cazado al más peludo, {1}, y gana +{2}. Gracias por este gran servicio a la comunidad.",
                ["EventHuntSurvivedV3"] = "{0} ha sobrevivido a la cacería con todo su pelo y gana +{1}. Paquetes.",
                ["EventHuntDiedV4"] = "{0}, el más peludo de la isla, la ha palmado solito, sin que nadie le meta un tiro. Se ha muerto como vivió: con el pelo en la cara y sin que nadie le haga ni puto caso. Se acabó la cacería.",
                ["EventHuntEscapedV4"] = "{0} se ha pirado de la isla con su melena, como una rata con extensiones. Volverá cuando se le acabe el acondicionador. Se acabó la cacería.",
                ["EventBladeStormStartV4"] = "<color=#e0a526>BROTE DE ALOPECIA</color>: durante {0} min el heli, la Bradley y el Chinook dan x{1}. Al rape.",
                ["EventBladeStormEndV2"] = "Se acabó el brote de alopecia. El heli vuelve a pagar lo de siempre, como un funcionario.",
                ["EventStoppedByAdminV2"] = "Un admin ha cancelado el evento {0}. Las quejas, a su peluquero.",
                ["ReasonHuntKillV2"] = "cacería del peludo ({0})",
                ["ReasonHuntSurvived"] = "sobrevivir a la cacería",
                ["AdminEventUsageV3"] = "Uso: /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminEventBusy"] = "Ya hay un evento en marcha: {0}. Páralo antes con /calvoadmin evento parar.",
                ["AdminEventCannotStart"] = "No se puede lanzar {0} ahora (¿pocos jugadores conectados o desactivado en la config?).",
                ["AdminEventNone"] = "No hay ningún evento en marcha.",
                ["BatteryTag"] = " [pila x{0}]",
                ["HallEmpty"] = "Aún no se ha cerrado ningún mapa. El primero entra con el próximo wipe: ve puliendo la frente.",
                ["HallMapClosed"] = "Mapa cerrado el {0}",
                ["HallNumber"] = "#{0}",
                ["HallPodiumPlace"] = "{0}º  {1}",
                ["HallTopKiller"] = "Más kills: <color=#e0a526>{0}</color> ({1})",
                ["HallTopDeaths"] = "Más muertes: <color=#e0662f>{0}</color> ({1}). Con tanto morir, ya puede hacerse trenzas.",
                ["HallTopDeathsPlain"] = "Más muertes: <color=#e0662f>{0}</color> ({1})",
                ["HallSnapshot"] = "Foto del mapa del {0}",
                ["HallWipeWinner"] = "<color=#e0a526>{0}</color> se lleva el mapa con <color=#e0a526>{1}</color> de alopecia (el podio, en el Salón de la fama de <color=#e0a526>/calvos</color>). Frente soberana.",
                ["AdminHallUsageV2"] = "Uso: /calvoadmin salon guardar | /calvoadmin salon cerrar [forzar] | /calvoadmin salon borrar <n>",
                ["AdminHallCloseUsage"] = "Uso: isla.salon cerrar [forzar]",
                ["AdminHallClosed"] = "Mapa cerrado: entrada #{0} guardada en el Salón de la fama. Kills y muertes del mapa a 0.",
                ["AdminHallClosedEmpty"] = "Mapa cerrado sin entrada en el Salón de la fama (nadie ha matado ni muerto en este mapa, o el salón está desactivado). Kills y muertes del mapa a 0.",
                ["AdminHallClosedReset"] = "Alopecia de todos a 0 (la config tiene el reset por wipe activado).",
                ["AdminHallCloseRecent"] = "No se ha cerrado nada: el mapa ya se cerró el {0} a las {1}, hace menos de {2} horas. Para cerrarlo otra vez, añade 'forzar'.",
                ["AdminHallSaved"] = "Entrada #{0} guardada en el Salón de la fama.",
                ["AdminHallNothing"] = "No se ha guardado nada: nadie tiene alopecia ni kills en este mapa.",
                ["AdminHallDeleted"] = "Entrada #{0} ({1}) borrada del Salón de la fama.",
                ["AdminHallNotFound"] = "No hay ninguna entrada #{0} en el Salón de la fama.",
                ["BountyUsage"] = "Uso: <color=#e0a526>/cabeza <jugador> <cantidad></color> (mínimo {0}). Lo que pones no se devuelve.",
                ["BountyTooLow"] = "El mínimo son {0}. Con menos no le cortas ni las patillas.",
                ["BountySelf"] = "No puedes poner precio a tu propia cabellera, por mucho que te sobre.",
                ["BountyNoBalanceV2"] = "No te llega: tienes {0}. Mucho rencor para tan poco saldo.",
                ["BountyNotFound"] = "Nadie en la isla se llama '{0}'. Para odiar a alguien, primero apréndete su nombre.",
                ["BountyAmbiguous"] = "Hay {0} jugadores con '{1}' en el nombre. Escribe más, que esto no es una rifa.",
                ["BountyClosed"] = "Las recompensas por cabeza están cerradas.",
                ["BountyFailed"] = "Algo ha fallado con los Puntos de Chola y no se ha puesto nada. Prueba otra vez.",
                ["BountyPlaced"] = "<color=#e0a526>{0}</color> ha puesto <color=#e0a526>{1}</color> por la cabellera de <color=#e0662f>{2}</color>. Quien lo mate, se lo lleva. Se busca, vivo o calvo.",
                ["BountyRaisedV2"] = "<color=#e0a526>{0}</color> añade <color=#e0a526>{1}</color> por la cabellera de <color=#e0662f>{2}</color>: el bote ya va por <color=#e0a526>{3}</color>. Cotiza al alza.",
                ["BountyClaimed"] = "<color=#e0a526>{0}</color> se cobra la cabellera de <color=#e0662f>{1}</color> y se lleva <color=#e0a526>{2}</color>. Rapado y pagado.",
                ["BountyListEmpty"] = "Ninguna cabellera tiene precio. O hay paz en la isla, o nadie tiene un Punto de Chola.",
                ["BountyPriceShort"] = "{0} PdC",
                ["BountyMenuHint"] = "Pon precio con <color=#e0a526>/cabeza <jugador> <cantidad></color>: se lo lleva quien lo mate en PvP. Lo que pones no se devuelve.",
                ["BountyMenuYou"] = "Por tu cabellera dan <color=#e0662f>{0}</color>. Con esa calva se te ve desde la otra punta de la isla.",
                ["DebugBountyNoClaim"] = "[debug] {0}: sin cobrar la cabellera de {1} · {2}",
                ["NoBountyTeam"] = "es de su equipo",
                ["NoBountySleeper"] = "víctima dormida o desconectada",
                ["AdminBountyUsage"] = "Uso: /calvoadmin cabeza quitar <jugador>",
                ["AdminBountyRemoved"] = "Anulado el precio por la cabellera de {0} ({1}).",
                ["AdminBountyNone"] = "Nadie ha puesto precio a la cabellera de {0}.",
                ["CalvoDelDiaName"] = "Calvo del Día",
                ["CalvoDelDiaChatV3"] = "<color=#e0a526>{0}</color> es el <color=#e0a526>CALVO DEL DÍA</color>: +{1} de alopecia desde la última elección. Hasta mañana, se le habla de usted.",
                ["CalvoDelDiaBannerV2"] = "¡{0} es el CALVO DEL DÍA! Firma autógrafos en la calva.",
                ["CalvoDelDiaMenu"] = "Calvo del Día: <color=#e0a526>{0}</color> (+{1})",
                ["CalvoDelDiaPrize"] = "<color=#e0a526>Premio de Calvo del Día:</color> {0}. Que no se te suba a la cabeza, que ahí arriba ya no queda nada.",
                ["CalvoDelDiaPrizeItems"] = "<color=#e0a526>Premio de Calvo del Día:</color> ya lo tienes en el inventario (o a tus pies, si no te cabe).",
                ["AdminCalvoDelDiaUsage"] = "Uso: /calvoadmin calvodeldia ahora",
                ["AdminCalvoDelDiaOff"] = "El Calvo del Día está desactivado en la config.",
                ["AdminCalvoDelDiaNone"] = "Nadie ha ganado alopecia desde la última elección: no hay Calvo del Día.",
                ["HudWallet"] = "<color=#9a9288>PdC</color> <color=#e0a526>{0}</color>     <color=#9a9288>PELONES</color> <color=#e0a526>{1}</color>",
                ["HudCounterV3"] = "<size=11><color=#9a9288>ALOPECIA</color></size>  <color=#e0a526>{0}</color>\n<size=10><color=#d8d8d8>{1}</color></size>"
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

        // Short form for narrow spots (counter, +X/-X popup, ranking): full number below a thousand million,
        // then M (10^6), B (10^12) or T (10^18) with one decimal, truncated so it never shows more than there is.
        private static string FormatCompact(long value)
        {
            if (value > -1000000000L && value < 1000000000L)
            {
                return FormatBaldness(value);
            }

            string sign = value < 0 ? "-" : string.Empty;
            ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
            ulong unit = magnitude >= 1000000000000000000UL ? 1000000000000000000UL : magnitude >= 1000000000000UL ? 1000000000000UL : 1000000UL;
            string suffix = unit == 1000000000000000000UL ? "T" : unit == 1000000000000UL ? "B" : "M";
            ulong whole = magnitude / unit;
            ulong tenth = magnitude % unit / (unit / 10);
            string number = whole.ToString("#,0", BaldnessFormat) + (tenth > 0 ? "," + tenth.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return sign + number + " " + suffix;
        }

        // Wallet figures: full number below a million, then "12,3 M" and, from a thousand million, "1,5 mil M"
        // (one decimal, truncated like FormatCompact).
        private static string FormatWallet(long value)
        {
            if (value > -1000000L && value < 1000000L)
            {
                return FormatBaldness(value);
            }

            string sign = value < 0 ? "-" : string.Empty;
            ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
            bool thousandMillions = magnitude >= 1000000000UL;
            ulong unit = thousandMillions ? 1000000000UL : 1000000UL;
            ulong whole = magnitude / unit;
            ulong tenth = magnitude % unit / (unit / 10);
            string number = whole.ToString("#,0", BaldnessFormat) + (tenth > 0 ? "," + tenth.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return sign + number + (thousandMillions ? " mil M" : " M");
        }

        // Baldness is a long; these keep huge values at long.MaxValue instead of wrapping to negative.
        private static long SaturatingAdd(long a, long b)
        {
            if (b > 0 && a > long.MaxValue - b) return long.MaxValue;
            if (b < 0 && a < long.MinValue - b) return long.MinValue;
            return a + b;
        }

        private static long SaturatingMultiply(long a, long b)
        {
            if (a == 0 || b == 0) return 0;
            if (a > 0 && b > 0 && a > long.MaxValue / b) return long.MaxValue;
            return a * b;
        }

        #endregion

        #region Lifecycle Hooks

        private void Init()
        {
            permission.RegisterPermission(PermAdmin, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            serverReady = true;
            if (storedData.PendingWipeHook != 0)
            {
                timer.Once(WipeHookDelaySeconds, DeliverPendingWipeHook);
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player))
                {
                    GetOrCreateData(player);
                }
            }

            timer.Every(SurvivalTickSeconds, SurvivalTick);
            if (config.Ui.ShowCounter && config.Ui.ShowWallet)
            {
                timer.Every(WalletPollSeconds, PollWallets);
            }

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
            CheckServerRewardsPatch(ServerRewards);

            if (config.SyncTitleGroups)
            {
                foreach (string group in titleGroups.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    EnsureGroup(group);
                }

                foreach (BasePlayer player in BasePlayer.activePlayerList)
                {
                    if (IsRealPlayer(player) && storedData.Players.TryGetValue((ulong)player.userID, out PlayerData data))
                    {
                        SyncTitleGroup(data);
                    }
                }
            }

            if (config.CalvoDelDia.Enabled)
            {
                InitCalvoDelDia();
                timer.Every(CalvoDelDiaCheckSeconds, CheckCalvoDelDia);
            }
            else
            {
                // Switched off: nobody keeps the group (and its perks). The current one is kept in the data for when it comes back.
                SyncCalvoDelDiaGroup(0UL);
            }
        }

        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "ServerRewards")
            {
                CheckServerRewardsPatch(plugin);
            }
        }

        private void OnPluginUnloaded(Plugin plugin)
        {
            if (plugin != null && plugin.Name == "ServerRewards")
            {
                warnedMissingRpPatch = false;
            }
        }

        // The 64-bit patch adds CheckPointsLong; without it the plugin silently falls back to the int API, so say it loud once.
        // The server forwards console warnings to Telegram. Console text for the admins, in Spanish on purpose.
        private void CheckServerRewardsPatch(Plugin serverRewards)
        {
            if (warnedMissingRpPatch || serverRewards == null || !serverRewards.IsLoaded)
            {
                return;
            }

            if (serverRewards.Call("CheckPointsLong", 0UL) is long)
            {
                return;
            }

            warnedMissingRpPatch = true;
            PrintWarning("AVISO: Server Rewards no tiene el parche de 64 bits: Puntos de Chola limitados a 2.147.483.647. Avisa a Jano.");
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
            if (storedData == null)
            {
                return;
            }

            CloseMap(false, "OnNewSave");
        }

        #endregion

        #region Player Hooks

        private void OnPlayerConnected(BasePlayer player)
        {
            if (IsRealPlayer(player))
            {
                PlayerData data = GetOrCreateData(player);
                SyncTitleGroup(data);
                MarkSeenForCalvoDelDia(data.Id);
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
            victimData.WipeDeaths++;
            victimData.SurvivalSeconds = 0f;
            victimData.SurvivalMoved = false;
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
            killerData.WipeKills++;
            if (headshot)
            {
                killerData.HeadshotKills++;
            }

            dataDirty = true;

            // Independent of the kill cooldown: a bounty is paid once and then it is gone.
            TryClaimBounty(killer, killerData, victim, victimData);

            if (!IsKillRewardable(killerData, (ulong)killer.userID, victim, victimData.Name))
            {
                return;
            }

            GainBaldness(killerData, headshot ? config.HeadshotKillReward : config.KillReward,
                Lang(headshot ? "ReasonPlayerHeadshotKillV2" : "ReasonPlayerKill", null, victimData.Name));
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

            if (activeEvent == GlobalEvent.BladeStorm)
            {
                reward = SaturatingMultiply(reward, config.GlobalEvents.BladeStorm.Multiplier);
                prefab += EventTag(GlobalEvent.BladeStorm, config.GlobalEvents.BladeStorm.Multiplier);
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
                OpenCalvos(player, MenuTab.Ranking, 0);
            }
        }

        // /cabeza <player> <amount>: the amount is the last word, so names with spaces work without quotes.
        [ChatCommand("cabeza")]
        private void CmdCabeza(BasePlayer player, string command, string[] args)
        {
            if (!IsRealPlayer(player))
            {
                return;
            }

            BountyConfig bounties = config.Bounties;
            string userId = player.UserIDString;
            if (!bounties.Enabled || !RpAvailable)
            {
                Reply(player, "BountyClosed");
                return;
            }

            if (args.Length < 2 || !long.TryParse(args[args.Length - 1].Replace(".", string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount) || amount <= 0)
            {
                Reply(player, "BountyUsage", UnitText(true, bounties.MinAmount, userId));
                return;
            }

            if (amount < bounties.MinAmount)
            {
                Reply(player, "BountyTooLow", UnitText(true, bounties.MinAmount, userId));
                return;
            }

            string query = string.Join(" ", args, 0, args.Length - 1);
            List<KeyValuePair<ulong, PlayerData>> matches = FindStoredPlayers(query);
            if (matches.Count == 0)
            {
                Reply(player, "BountyNotFound", query);
                return;
            }

            if (matches.Count > 1)
            {
                Reply(player, "BountyAmbiguous", matches.Count, query);
                return;
            }

            PlayerData placer = GetOrCreateData(player);
            PlayerData target = matches[0].Value;
            if (target.Id == placer.Id)
            {
                Reply(player, "BountySelf");
                return;
            }

            long balance = CheckRp(placer.Id);
            if (balance < amount)
            {
                Reply(player, "BountyNoBalanceV2", UnitText(true, balance, userId));
                return;
            }

            // Charged first; the bounty only goes up if Server Rewards took the points.
            if (!TakeRp(placer.Id, amount))
            {
                Reply(player, "BountyFailed");
                return;
            }

            long total = SaturatingAdd(storedData.Bounties.TryGetValue(target.Id, out long previous) ? previous : 0, amount);
            storedData.Bounties[target.Id] = total;
            dataDirty = true;

            if (total > amount)
            {
                Broadcast("BountyRaisedV2", placer.Name, UnitText(true, amount, null), target.Name, UnitText(true, total, null));
            }
            else
            {
                Broadcast("BountyPlaced", placer.Name, UnitText(true, amount, null), target.Name);
            }

            Interface.CallHook("OnIslaBountyPlaced", placer.Id, placer.Name ?? string.Empty, target.Id, target.Name ?? string.Empty, amount, total);
            Puts($"Bounty: {placer.Name} ({placer.Id}) put {amount} Puntos de Chola on {target.Name} ({target.Id}); total {total}.");
        }

        [ChatCommand("cabezas")]
        private void CmdCabezas(BasePlayer player, string command, string[] args)
        {
            if (!IsRealPlayer(player))
            {
                return;
            }

            if (!config.Bounties.Enabled)
            {
                Reply(player, "BountyClosed");
                return;
            }

            OpenCalvos(player, MenuTab.Bounties, 0);
        }

        [ChatCommand("calvoadmin")]
        private void CmdCalvoAdmin(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, PermAdmin))
            {
                Reply(player, "NoPermissionV4");
                return;
            }

            if (args.Length < 2)
            {
                Reply(player, "AdminUsageV5");
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

            if (action == "salon" || action == "salón")
            {
                AdminHall(player, args);
                return;
            }

            if (action == "cabeza")
            {
                AdminBounty(player, args);
                return;
            }

            if (action == "calvodeldia")
            {
                AdminCalvoDelDia(player, args[1]);
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
                Reply(player, "AdminUsageV5");
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

            // Admin changes are silent: no global announcements. Nor do they count for the Calvo del Día.
            long before = target.Baldness;
            ChangeBaldness(target, value - target.Baldness, false, Lang("ReasonAdmin"));
            ExcludeFromCalvoDelDia(target, before);
            Reply(player, action == "set" ? "AdminSetV2" : "AdminResetV2", target.Name, FormatBaldness(target.Baldness));
            Puts($"{player.displayName} ({player.UserIDString}) {action} baldness of {target.Name} ({matches[0].Key}) to {target.Baldness}.");
        }

        private void ToggleDebug(BasePlayer player, string mode)
        {
            ulong id = (ulong)player.userID;
            switch (mode.ToLowerInvariant())
            {
                case "on":
                    debugAdmins.Add(id);
                    Reply(player, "DebugOnV2");
                    break;
                case "off":
                    debugAdmins.Remove(id);
                    Reply(player, "DebugOff");
                    break;
                default:
                    Reply(player, "AdminUsageV5");
                    break;
            }
        }

        // /calvoadmin salon guardar | /calvoadmin salon cerrar [forzar] | /calvoadmin salon borrar <n>
        private void AdminHall(BasePlayer player, string[] args)
        {
            switch (args[1].ToLowerInvariant())
            {
                case "guardar":
                    // A snapshot: the map goes on. Not announced in chat, like every other admin change; other plugins still get the hook.
                    HallEntry saved = SaveHallEntry(true);
                    if (saved == null)
                    {
                        Reply(player, "AdminHallNothing");
                        return;
                    }

                    CallHallHook(saved);
                    Reply(player, "AdminHallSaved", saved.Number);
                    Puts($"{player.displayName} ({player.UserIDString}) saved hall of fame entry #{saved.Number} by hand.");
                    return;
                case "cerrar":
                    // The whole map close, for when OnNewSave did not arrive. Same repeat check, unless "forzar".
                    bool force = args.Length > 2 && args[2].ToLowerInvariant() == "forzar";
                    if (args.Length > 2 && !force)
                    {
                        Reply(player, "AdminHallUsageV2");
                        return;
                    }

                    Puts($"{player.displayName} ({player.UserIDString}) closed the map by hand{(force ? " (forced)" : string.Empty)}.");
                    foreach (string line in CloseMapReplies(CloseMap(force, "/calvoadmin salon cerrar"), player.UserIDString))
                    {
                        SendChat(player, line);
                    }

                    return;
                case "borrar":
                    if (args.Length < 3 || !int.TryParse(args[2].TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
                    {
                        Reply(player, "AdminHallUsageV2");
                        return;
                    }

                    HallEntry entry = storedData.HallOfFame.FirstOrDefault(e => e.Number == number);
                    if (entry == null)
                    {
                        Reply(player, "AdminHallNotFound", number);
                        return;
                    }

                    storedData.HallOfFame.Remove(entry);
                    if (storedData.PendingWipeAnnouncement == number)
                    {
                        // Another entry of the same close (a repeated wipe before 1.8.2) keeps the announcement going.
                        HallEntry other = storedData.HallOfFame
                            .Where(e => !e.Manual && e.Podium.Count > 0 && Math.Abs((e.Date - entry.Date).TotalHours) < MapCloseRepeatHours)
                            .OrderByDescending(e => e.Number)
                            .FirstOrDefault();
                        storedData.PendingWipeAnnouncement = other?.Number ?? 0;
                    }

                    if (storedData.PendingWipeHook == number)
                    {
                        storedData.PendingWipeHook = 0;
                    }

                    dataDirty = true;
                    Reply(player, "AdminHallDeleted", number, FormatDate(entry.Date));
                    Puts($"{player.displayName} ({player.UserIDString}) deleted hall of fame entry #{number}.");
                    return;
                default:
                    Reply(player, "AdminHallUsageV2");
                    return;
            }
        }

        // /calvoadmin cabeza quitar <player>: the bounty is gone, and nobody gets it back.
        private void AdminBounty(BasePlayer player, string[] args)
        {
            if (args.Length < 3 || args[1].ToLowerInvariant() != "quitar")
            {
                Reply(player, "AdminBountyUsage");
                return;
            }

            string query = string.Join(" ", args, 2, args.Length - 2);
            List<KeyValuePair<ulong, PlayerData>> matches = FindStoredPlayers(query);
            if (matches.Count == 0)
            {
                Reply(player, "PlayerNotFound", query);
                return;
            }

            if (matches.Count > 1)
            {
                Reply(player, "PlayerAmbiguous", matches.Count, query);
                return;
            }

            PlayerData target = matches[0].Value;
            if (!storedData.Bounties.TryGetValue(target.Id, out long total))
            {
                Reply(player, "AdminBountyNone", target.Name);
                return;
            }

            storedData.Bounties.Remove(target.Id);
            dataDirty = true;
            Reply(player, "AdminBountyRemoved", target.Name, UnitText(true, total, player.UserIDString));
            Puts($"{player.displayName} ({player.UserIDString}) removed the bounty on {target.Name} ({target.Id}): {total} Puntos de Chola.");
        }

        // /calvoadmin calvodeldia ahora: a full pick right now. It counts as today's pick: the scheduled one waits until tomorrow.
        private void AdminCalvoDelDia(BasePlayer player, string mode)
        {
            if (mode.ToLowerInvariant() != "ahora")
            {
                Reply(player, "AdminCalvoDelDiaUsage");
                return;
            }

            if (!config.CalvoDelDia.Enabled)
            {
                Reply(player, "AdminCalvoDelDiaOff");
                return;
            }

            // It is today's pick: the scheduled one does not run again today (no second prize).
            storedData.CalvoDelDia.LastPickDate = TodayKey();
            dataDirty = true;
            Puts($"{player.displayName} ({player.UserIDString}) forced the Calvo del Día pick (it counts as today's).");
            if (PickCalvoDelDia() == null)
            {
                Reply(player, "AdminCalvoDelDiaNone");
            }
        }

        // isla.salon cerrar [forzar]: "/calvoadmin salon cerrar" from the server console or RCON. Replies in the console.
        [ConsoleCommand("isla.salon")]
        private void CcmdIslaSalon(ConsoleSystem.Arg arg)
        {
            // A player's client always has a connection; the server console and RCON do not.
            if (arg.Connection != null)
            {
                return;
            }

            string[] args = MenuArgs(arg);
            bool force = args.Length == 2 && args[1].ToLowerInvariant() == "forzar";
            if (args.Length == 0 || args.Length > 2 || args[0].ToLowerInvariant() != "cerrar" || (args.Length == 2 && !force))
            {
                arg.ReplyWith(Lang("AdminHallCloseUsage"));
                return;
            }

            Puts($"Map closed by hand from the console{(force ? " (forced)" : string.Empty)}.");
            arg.ReplyWith(string.Join("\n", CloseMapReplies(CloseMap(force, "isla.salon cerrar"), null).ToArray()));
        }

        // Server console and RCON only (Jano's weekly report): every player as one line of JSON.
        [ConsoleCommand("isla.ranking")]
        private void CcmdIslaRanking(ConsoleSystem.Arg arg)
        {
            // A player's client always has a connection; the server console and RCON do not.
            if (arg.Connection != null)
            {
                return;
            }

            var players = storedData.Players.Values
                .OrderByDescending(d => d.Baldness)
                .ThenByDescending(d => d.Kills)
                .Select(d => new
                {
                    name = d.Name ?? string.Empty,
                    // As text: a SteamID64 does not fit in a JavaScript number.
                    id = d.Id.ToString(CultureInfo.InvariantCulture),
                    alopecia = d.Baldness,
                    title = TitleName(GetTitleIndex(d.Baldness)),
                    kills = d.Kills,
                    deaths = d.Deaths,
                    wipeKills = d.WipeKills,
                    wipeDeaths = d.WipeDeaths,
                    online = IsOnline(d.Id)
                })
                .ToList();

            arg.ReplyWith(JsonConvert.SerializeObject(players, Formatting.None));
        }

        #endregion

        #region Global Events

        private enum GlobalEvent
        {
            None,
            BaldHour,
            ShampooRain,
            HairiestHunt,
            BladeStorm
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
                pendingExchanges.Remove((ulong)player.userID);
                walletTexts.Remove((ulong)player.userID);
            }

            if (activeEvent == GlobalEvent.HairiestHunt && player != null && (ulong)player.userID == huntTargetId)
            {
                BroadcastEvent("EventHuntEscapedV4", player.displayName);
                CallHuntEnded(huntTargetId, player.displayName, "escaped", null);
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

            var candidates = new List<GlobalEvent> { GlobalEvent.BaldHour, GlobalEvent.ShampooRain, GlobalEvent.HairiestHunt, GlobalEvent.BladeStorm };
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
                    BroadcastEvent("EventBaldHourStartV4", minutes, events.BaldHour.Multiplier);
                    break;
                case GlobalEvent.ShampooRain:
                    if (!events.ShampooRain.Enabled) return false;
                    minutes = events.ShampooRain.DurationMinutes;
                    BroadcastEvent("EventShampooRainStartV2", minutes, events.ShampooRain.Multiplier);
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
                    BroadcastEvent("EventHuntStartV4", target.displayName, FormatBaldness(lowest), FormatBaldness(hunt.KillerBonus), minutes, FormatBaldness(hunt.SurvivorBonus));
                    break;
                case GlobalEvent.BladeStorm:
                    if (!events.BladeStorm.Enabled) return false;
                    minutes = events.BladeStorm.DurationMinutes;
                    BroadcastEvent("EventBladeStormStartV4", minutes, events.BladeStorm.Multiplier);
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
                        BroadcastEvent("EventShampooRainEndV2");
                        break;
                    case GlobalEvent.HairiestHunt:
                        if (storedData.Players.TryGetValue(huntTargetId, out PlayerData target))
                        {
                            long bonus = config.GlobalEvents.HairiestHunt.SurvivorBonus;
                            BroadcastEvent("EventHuntSurvivedV3", target.Name, FormatBaldness(bonus));
                            ChangeBaldness(target, bonus, true, Lang("ReasonHuntSurvived"));
                            CallHuntEnded(huntTargetId, target.Name, "survived", null);
                        }

                        break;
                    case GlobalEvent.BladeStorm:
                        BroadcastEvent("EventBladeStormEndV2");
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
                BroadcastEvent("EventHuntDiedV4", targetData.Name);
                CallHuntEnded(targetData.Id, targetData.Name, "died", null);
            }
            else
            {
                long bonus = config.GlobalEvents.HairiestHunt.KillerBonus;
                PlayerData killerData = GetOrCreateData(killer);
                BroadcastEvent("EventHuntKilledV4", killerData.Name, targetData.Name, FormatBaldness(bonus));
                ChangeBaldness(killerData, bonus, true, Lang("ReasonHuntKillV2", null, targetData.Name));
                CallHuntEnded(targetData.Id, targetData.Name, "killed", killerData.Name);
            }

            EndEvent(false);
        }

        // For other plugins (JanoBridge): outcome is "killed", "survived", "died", "escaped" or "stopped" (an admin stopped it
        // with /calvoadmin evento parar); killerName is empty unless killed.
        private static void CallHuntEnded(ulong targetId, string targetName, string outcome, string killerName) =>
            Interface.CallHook("OnIslaHuntEnded", targetId, targetName ?? string.Empty, outcome, killerName ?? string.Empty);

        private void AdminEvent(BasePlayer player, string name)
        {
            GlobalEvent requested;
            switch (name.ToLowerInvariant())
            {
                case "hora": requested = GlobalEvent.BaldHour; break;
                case "champu":
                case "champú": requested = GlobalEvent.ShampooRain; break;
                case "peludo": requested = GlobalEvent.HairiestHunt; break;
                case "alopecia":
                case "cuchillas": requested = GlobalEvent.BladeStorm; break;
                case "parar":
                    if (activeEvent == GlobalEvent.None)
                    {
                        Reply(player, "AdminEventNone");
                        return;
                    }

                    GlobalEvent stopped = activeEvent;
                    ulong huntTarget = huntTargetId;
                    BroadcastEvent("EventStoppedByAdminV2", EventName(activeEvent));
                    EndEvent(false);
                    if (stopped == GlobalEvent.HairiestHunt && huntTarget != 0UL)
                    {
                        CallHuntEnded(huntTarget, storedData.Players.TryGetValue(huntTarget, out PlayerData target) ? target.Name : null, "stopped", null);
                    }

                    return;
                default:
                    Reply(player, "AdminEventUsageV3");
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

        private string EventName(GlobalEvent globalEvent) => Lang("EventName" + globalEvent + "V3");

        private string EventTag(GlobalEvent globalEvent, int multiplier) => Lang("EventTag", null, EventName(globalEvent), multiplier);

        // Real players who are connected, alive and awake.
        private static List<BasePlayer> GetActivePlayers() =>
            BasePlayer.activePlayerList.Where(p => IsRealPlayer(p) && p.IsConnected && !p.IsDead() && !p.IsSleeping()).ToList();

        // Every baldness gain from gameplay goes through here so the Bald hour can multiply it.
        private void GainBaldness(PlayerData data, long amount, string reason)
        {
            if (activeEvent == GlobalEvent.BaldHour)
            {
                amount = SaturatingMultiply(amount, config.GlobalEvents.BaldHour.Multiplier);
                reason += EventTag(GlobalEvent.BaldHour, config.GlobalEvents.BaldHour.Multiplier);
            }

            if (IsBatteryActive(data.Id))
            {
                amount = SaturatingMultiply(amount, config.CursedItems.Battery.Multiplier);
                reason += Lang("BatteryTag", null, config.CursedItems.Battery.Multiplier);
            }

            ChangeBaldness(data, amount, true, reason);
        }

        #endregion

        #region On-screen UI

        private const string UiCounter = "IslaDeCalvos.Counter";
        private const string UiDelta = "IslaDeCalvos.Delta";
        private const string UiBanner = "IslaDeCalvos.Banner";
        private const string UiWallet = "IslaDeCalvos.Wallet";
        // Pelones and Puntos de Chola change outside this plugin (shops, currency exchange), so the wallet is polled.
        private const float WalletPollSeconds = 3f;

        private readonly Dictionary<ulong, Timer> deltaTimers = new Dictionary<ulong, Timer>();
        // Last wallet text drawn per player: the poll only redraws when it changes.
        private readonly Dictionary<ulong, string> walletTexts = new Dictionary<ulong, string>();
        private Timer bannerTimer;

        // The client is ready for UI once the player wakes up (after connecting and after every respawn).
        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (IsRealPlayer(player))
            {
                DrawCounter(player);
                AnnounceWipeWinner(player);
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
                    Text = Lang("HudCounterV3", player.UserIDString, FormatCompact(data.Baldness), GetTitle(data.Baldness)),
                    FontSize = 14,
                    Align = TextAnchor.MiddleCenter,
                    Color = "1 1 1 1"
                },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiCounter);
            CuiHelper.AddUi(player, container);
            DrawWallet(player, true);
        }

        // With neither Server Rewards nor Economics loaded there is nothing to show.
        private bool WalletActive => config.Ui.ShowCounter && config.Ui.ShowWallet && (RpAvailable || CoinsAvailable);

        // Thin strip glued under the counter, same width and anchor. force: redraw even if the figures did not change.
        private void DrawWallet(BasePlayer player, bool force)
        {
            if (player == null || !player.IsConnected)
            {
                return;
            }

            ulong userId = (ulong)player.userID;
            if (!WalletActive)
            {
                if (walletTexts.Remove(userId))
                {
                    CuiHelper.DestroyUi(player, UiWallet);
                }

                return;
            }

            if (player.IsSleeping())
            {
                return;
            }

            string text = Lang("HudWallet", player.UserIDString,
                RpAvailable ? FormatWallet(CheckRp(userId)) : "-", CoinsAvailable ? FormatWallet(CoinBalance(userId)) : "-");
            if (!force && walletTexts.TryGetValue(userId, out string previous) && previous == text)
            {
                return;
            }

            walletTexts[userId] = text;
            UiConfig ui = config.Ui;
            var container = new CuiElementContainer();
            container.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.45" },
                RectTransform = { AnchorMin = ui.CounterAnchorMin, AnchorMax = ui.CounterAnchorMax, OffsetMin = ShiftY(ui.CounterOffsetMin, -20), OffsetMax = ShiftY(ui.CounterOffsetMin, -1, ui.CounterOffsetMax) }
            }, "Hud", UiWallet, UiWallet);
            container.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
            }, UiWallet);
            CuiHelper.AddUi(player, container);
        }

        private void PollWallets()
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player))
                {
                    DrawWallet(player, false);
                }
            }
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
            // Same width as the counter: below it (and below the wallet, if shown) when the counter is in the top half
            // of the screen, above otherwise.
            bool below = CounterIsOnTop();
            float walletGap = WalletActive ? 20f : 0f;
            string popupMin = below ? ShiftY(ui.CounterOffsetMin, -28 - walletGap) : ShiftY(ui.CounterOffsetMax, 2, ui.CounterOffsetMin);
            string popupMax = below ? ShiftY(ui.CounterOffsetMin, -2 - walletGap, ui.CounterOffsetMax) : ShiftY(ui.CounterOffsetMax, 28);

            var container = new CuiElementContainer();
            container.Add(new CuiLabel
            {
                Text =
                {
                    Text = (delta > 0 ? "+" : string.Empty) + FormatCompact(delta),
                    FontSize = 18,
                    Align = TextAnchor.MiddleCenter,
                    Color = delta > 0 ? ColorGold : ColorRust,
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

            ShowBanner(Lang(key, null, args), config.Ui.BannerSeconds);
        }

        // Big text in the middle of the screen for everyone connected.
        private void ShowBanner(string message, float seconds)
        {
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
            bannerTimer = timer.Once(seconds, () =>
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
            CuiHelper.DestroyUi(player, UiWallet);
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
            Carne,
            Exchange,
            ExchangeAmount,
            ExchangeConfirm
        }

        private enum ExchangeMode
        {
            SellForRp,
            SellForCoins,
            BuyWithRp,
            BuyWithCoins
        }

        private class PendingExchange
        {
            public ExchangeMode Mode;
            public long Baldness;
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
                case "bleach": return Lang("CalvarioDescBleachV2", null, Mathf.RoundToInt(c.Bleach.WinChance * 100f), FormatBaldness(c.Bleach.WinAmount), FormatBaldness(c.Bleach.LoseAmount));
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

        // Only items carrying the plugin's mark skin count (see CursedItemsConfig.MarkSkin). The inventory walk follows
        // GUIShop 2.4.48: containerMain, containerBelt and containerWear, matching item.info.itemid and item.skin.
        private List<global::Item> FindMarkedItems(BasePlayer player, string shortname)
        {
            var found = new List<global::Item>();
            ItemDefinition definition = FindItemDefinition(shortname);
            if (definition == null || player == null || player.inventory == null)
            {
                return found;
            }

            ulong mark = config.CursedItems.MarkSkin;
            foreach (ItemContainer container in new[] { player.inventory.containerMain, player.inventory.containerBelt, player.inventory.containerWear })
            {
                if (container?.itemList == null)
                {
                    continue;
                }

                foreach (global::Item item in container.itemList)
                {
                    if (item != null && item.info != null && item.info.itemid == definition.itemid && item.skin == mark && item.amount > 0)
                    {
                        found.Add(item);
                    }
                }
            }

            return found;
        }

        private int CountItem(BasePlayer player, string shortname) => FindMarkedItems(player, shortname).Sum(item => item.amount);

        // Gives the item straight to the inventory, or drops it at the player's feet if it is full. Cursed items get the
        // mark skin; tier prize items are ordinary items.
        private bool GiveItem(BasePlayer player, string shortname, int amount = 1, bool marked = true)
        {
            ItemDefinition definition = FindItemDefinition(shortname);
            if (definition == null || player == null || player.inventory == null)
            {
                return false;
            }

            global::Item item = ItemManager.CreateByItemID(definition.itemid, Math.Max(1, amount), marked ? config.CursedItems.MarkSkin : 0UL);
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
            global::Item item = FindMarkedItems(player, shortname).FirstOrDefault();
            if (item == null)
            {
                return false;
            }

            item.UseItem(1);
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
                Reply(player, "ItemFoundV4", displayName ?? CursedItemName(KeyOf(item)));
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
                return Lang("ItemBatteryAlreadyV4", player.UserIDString, BatteryMinutesLeft(data.Id));
            }

            if (!TakeItem(player, item.Shortname))
            {
                return null;
            }

            string reason = Lang("ReasonItemUseV2", null, name);
            switch (key)
            {
                case "bleach":
                    BleachConfig bleach = config.CursedItems.Bleach;
                    if (random.NextDouble() < bleach.WinChance)
                    {
                        GainBaldness(data, bleach.WinAmount, reason);
                        return Lang("ItemBleachWinV2", player.UserIDString, FormatBaldness(bleach.WinAmount));
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
                    return Lang("BarberTrophyUsedV2", player.UserIDString, name, FormatBaldness(reward));
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
                BroadcastEvent("CarneCompletedV3", data.Name, FormatBaldness(tags.CollectionBonus));
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
            MenuTab tab = parts.Length > 0 ? ParseTab(parts[0]) : MenuTab.Ranking;
            int page = parts.Length > 1 && int.TryParse(parts[1], out int p) ? p : 0;
            OpenCalvos(player, tab, page);
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

            // "calvos.use <key> catalog" comes from the catalog window and answers there.
            string line = UseCursedItem(player, parts[0]);
            if (parts.Length > 1 && parts[1] == "catalog")
            {
                OpenCatalog(player, line);
                return;
            }

            OpenBarber(player, BarberPage.Items, line);
        }

        [ConsoleCommand("calvos.carne")]
        private void CcmdCarne(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] parts = MenuArgs(arg);
            if (!IsRealPlayer(player) || !RequireCalvarioNpc(player))
            {
                return;
            }

            string line = DeliverIdTags(player);
            if (parts.Length > 0 && parts[0] == "catalog")
            {
                OpenCatalog(player, line);
                return;
            }

            OpenBarber(player, BarberPage.Carne, line);
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
            Reply(player, "CalvarioGoToBarberV3");
            return false;
        }

        private static string[] MenuArgs(ConsoleSystem.Arg arg) =>
            arg.HasArgs() ? arg.FullString.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries) : new string[0];

        // Barber-shop palette: leather browns, bald-head cream and barber-pole stripes.
        private const string ColorWindow = "0.12 0.07 0.06 0.97";
        private const string ColorCard = "0.2 0.12 0.1 1";
        private const string ColorCardDark = "0.26 0.17 0.14 1";
        private const string ColorScalp = "0.96 0.83 0.66 1";
        private const string ColorOnScalp = "0.12 0.07 0.06 1"; // dark text on a scalp-colored label (active tab)
        private const string ColorText = "0.88 0.82 0.75 1";
        private const string ColorMuted = "0.604 0.573 0.533 1"; // #9a9288, house gray for footnotes
        private const string ColorGold = "0.878 0.647 0.149 1"; // #e0a526, house gold for commands and good figures
        private const string ColorRust = "0.878 0.4 0.184 1"; // #e0662f, house rust for losses and warnings
        private const string ColorPoleRed = "0.72 0.14 0.14 1";
        private const string ColorPoleWhite = "0.93 0.9 0.85 1";
        private const string ColorPoleBlue = "0.16 0.3 0.62 1";
        private const string ColorGood = "0.35 0.5 0.25 1";
        private const string ColorDisabled = "0.28 0.2 0.18 1";
        // Proverb 6 was removed in 1.4.1; its lang key is gone, so the numbers skip it.
        private static readonly int[] ProverbNumbers = { 1, 2, 3, 4, 5, 7, 8 };

        // Tabs of the /calvos window.
        private enum MenuTab
        {
            Ranking,
            Hall,
            Bounties
        }

        // Gold, silver and bronze bars of the ranking and the hall of fame podium.
        private static readonly string[] PodiumColors = { "0.96 0.8 0.3 1", "0.82 0.82 0.86 1", "0.8 0.55 0.35 1" };

        // Word of each tab in "calvos.tab <tab> <page>".
        private static string TabCommand(MenuTab tab) => tab == MenuTab.Hall ? "salon" : tab == MenuTab.Bounties ? "cabezas" : "ranking";

        private static MenuTab ParseTab(string word) => word == "salon" ? MenuTab.Hall : word == "cabezas" ? MenuTab.Bounties : MenuTab.Ranking;

        // /calvos: ranking, hall of fame and bounties. The cursed items live with the barber (OpenBarber).
        private void OpenCalvos(BasePlayer player, MenuTab tab, int page)
        {
            if (tab == MenuTab.Bounties && !config.Bounties.Enabled)
            {
                tab = MenuTab.Ranking;
            }

            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            var ui = new CuiElementContainer();
            string window = DrawCalvarioWindow(ui, data, userId);

            DrawTabs(ui, window, tab, userId);
            DrawCalvoDelDia(ui, window, userId);
            switch (tab)
            {
                case MenuTab.Hall:
                    DrawHallTab(ui, window, page, userId);
                    break;
                case MenuTab.Bounties:
                    DrawBountiesTab(ui, window, data, page, userId);
                    break;
                default:
                    DrawRankingTab(ui, window, data, page, userId);
                    break;
            }

            CuiHelper.AddUi(player, ui);
        }

        // Big El Calvario window shared by /calvos and the barber's catalog: dim layer, barber pole, header, progress and X.
        private string DrawCalvarioWindow(CuiElementContainer ui, PlayerData data, string userId)
        {
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
            AddText(ui, window, Lang("CalvarioYouV3", userId, FormatCompact(data.Baldness), GetTitle(data.Baldness)), 15, TextAnchor.MiddleRight, "0.45 0.93", "0.935 0.975");
            DrawTitleProgress(ui, window, data.Baldness, userId);
            AddButton(ui, window, Lang("CalvarioClose", userId), "0.956 0.935", "0.99 0.985", ColorPoleRed, null, UiMenu, 18);
            return window;
        }

        // The active tab looks like the old single section label; the others are buttons.
        private void DrawTabs(CuiElementContainer ui, string window, MenuTab active, string userId)
        {
            var tabs = new List<KeyValuePair<MenuTab, string>>
            {
                new KeyValuePair<MenuTab, string>(MenuTab.Ranking, "CalvarioTabRankingV2"),
                new KeyValuePair<MenuTab, string>(MenuTab.Hall, "CalvarioTabHall")
            };

            if (config.Bounties.Enabled)
            {
                tabs.Add(new KeyValuePair<MenuTab, string>(MenuTab.Bounties, "CalvarioTabBounties"));
            }

            const float width = 0.18f, gap = 0.01f;
            for (int i = 0; i < tabs.Count; i++)
            {
                float x0 = 0.03f + i * (width + gap);
                bool isActive = tabs[i].Key == active;
                AddButton(ui, window, Lang(tabs[i].Value, userId), Anchor(x0, 0.81f), Anchor(x0 + width, 0.86f), isActive ? ColorScalp : ColorCardDark,
                    isActive ? null : "calvos.tab " + TabCommand(tabs[i].Key) + " 0", null, 13, isActive ? ColorOnScalp : ColorText);
            }
        }

        // Right of the tabs, on every tab: the current Calvo del Día, if there is one.
        private void DrawCalvoDelDia(CuiElementContainer ui, string window, string userId)
        {
            CalvoDelDiaRecord current = CurrentCalvoDelDia();
            if (current != null)
            {
                AddText(ui, window, Lang("CalvoDelDiaMenu", userId, current.Name, FormatCompact(current.Gained)), 12, TextAnchor.MiddleRight, "0.61 0.81", "0.97 0.86", ColorText);
            }
        }

        // One block per finished map, newest first: date, podium (gold, silver, bronze), most kills and most deaths.
        private void DrawHallTab(CuiElementContainer ui, string window, int page, string userId)
        {
            List<HallEntry> entries = Enumerable.Reverse(storedData.HallOfFame).ToList();
            if (entries.Count == 0)
            {
                AddText(ui, window, Lang("HallEmpty", userId), 16, TextAnchor.MiddleCenter, "0.03 0.4", "0.97 0.6", ColorText);
                return;
            }

            int pages = (entries.Count + HallPageSize - 1) / HallPageSize;
            page = Math.Max(0, Math.Min(page, pages - 1));

            const float blockHeight = 0.215f, blockGap = 0.01f;
            for (int i = 0; i < HallPageSize; i++)
            {
                int index = page * HallPageSize + i;
                if (index >= entries.Count)
                {
                    break;
                }

                HallEntry entry = entries[index];
                float y1 = 0.79f - i * (blockHeight + blockGap), y0 = y1 - blockHeight;
                string block = AddPanel(ui, window, ColorCard, Anchor(0.03f, y0), Anchor(0.97f, y1));
                AddText(ui, block, Lang(entry.Manual ? "HallSnapshot" : "HallMapClosed", userId, FormatDate(entry.Date)), 13, TextAnchor.MiddleLeft, "0.015 0.8", "0.8 0.98", ColorScalp);
                AddText(ui, block, Lang("HallNumber", userId, entry.Number), 11, TextAnchor.MiddleRight, "0.8 0.8", "0.985 0.98", ColorMuted);

                for (int place = 0; place < entry.Podium.Count && place < PodiumColors.Length; place++)
                {
                    HallPlayer podium = entry.Podium[place];
                    float x0 = 0.015f + place * 0.325f;
                    string card = AddPanel(ui, block, ColorCardDark, Anchor(x0, 0.4f), Anchor(x0 + 0.315f, 0.78f));
                    AddPanel(ui, card, PodiumColors[place], "0 0", "0.02 1");
                    AddText(ui, card, Lang("HallPodiumPlace", userId, place + 1, podium.Name), 13, TextAnchor.MiddleLeft, "0.05 0.5", "0.98 1", PodiumColors[place]);
                    AddText(ui, card, FormatCompact(podium.Value), 12, TextAnchor.MiddleLeft, "0.05 0", "0.98 0.5", ColorGold);
                }

                if (entry.TopKiller != null)
                {
                    AddText(ui, block, Lang("HallTopKiller", userId, entry.TopKiller.Name, FormatBaldness(entry.TopKiller.Value)), 12, TextAnchor.MiddleLeft, "0.015 0.2", "0.985 0.38", ColorText);
                }

                if (entry.TopDeaths != null)
                {
                    // The joke only on the newest entry, so it is not repeated in every block.
                    AddText(ui, block, Lang(index == 0 ? "HallTopDeaths" : "HallTopDeathsPlain", userId, entry.TopDeaths.Name, FormatBaldness(entry.TopDeaths.Value)), 12, TextAnchor.MiddleLeft, "0.015 0.02", "0.985 0.2", ColorText);
                }
            }

            DrawPager(ui, window, page, pages, MenuTab.Hall, userId);
        }

        // Every head with a price, highest first, with the command and your own price at the bottom.
        private void DrawBountiesTab(CuiElementContainer ui, string window, PlayerData me, int page, string userId)
        {
            List<KeyValuePair<string, long>> bounties = storedData.Bounties
                .Where(b => b.Value > 0)
                .Select(b => new KeyValuePair<string, long>(storedData.Players.TryGetValue(b.Key, out PlayerData target) ? target.Name : b.Key.ToString(CultureInfo.InvariantCulture), b.Value))
                .OrderByDescending(b => b.Value)
                .ThenBy(b => b.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AddText(ui, window, Lang("BountyMenuHint", userId), 12, TextAnchor.MiddleLeft, "0.03 0.135", "0.97 0.175", ColorText);
            if (storedData.Bounties.TryGetValue(me.Id, out long mine) && mine > 0)
            {
                AddText(ui, window, Lang("BountyMenuYou", userId, UnitText(true, mine, userId)), 12, TextAnchor.MiddleLeft, "0.03 0.095", "0.97 0.135", ColorText);
            }

            if (bounties.Count == 0)
            {
                AddText(ui, window, Lang("BountyListEmpty", userId), 16, TextAnchor.MiddleCenter, "0.03 0.4", "0.97 0.6", ColorText);
                return;
            }

            int pages = (bounties.Count + RankingPageSize - 1) / RankingPageSize;
            page = Math.Max(0, Math.Min(page, pages - 1));
            for (int i = 0; i < RankingPageSize; i++)
            {
                int index = page * RankingPageSize + i;
                if (index >= bounties.Count)
                {
                    break;
                }

                float y1 = 0.79f - i * 0.06f, y0 = y1 - 0.055f;
                string row = AddPanel(ui, window, i % 2 == 0 ? ColorCard : ColorCardDark, Anchor(0.03f, y0), Anchor(0.97f, y1));
                AddText(ui, row, Lang("CalvarioRankingLine", userId, index + 1, bounties[index].Key), 14, TextAnchor.MiddleLeft, "0.02 0", "0.6 1", ColorText);
                AddText(ui, row, Lang("BountyPriceShort", userId, FormatCompact(bounties[index].Value)), 14, TextAnchor.MiddleRight, "0.6 0", "0.98 1", ColorGold);
            }

            DrawPager(ui, window, page, pages, MenuTab.Bounties, userId);
        }

        private void DrawPager(CuiElementContainer ui, string window, int page, int pages, MenuTab tab, string userId)
        {
            string command = "calvos.tab " + TabCommand(tab) + " ";
            AddText(ui, window, Lang("CalvarioPage", userId, page + 1, pages), 13, TextAnchor.MiddleCenter, "0.42 0.03", "0.58 0.09", ColorMuted);
            AddButton(ui, window, Lang("CalvarioPrev", userId), "0.25 0.03", "0.4 0.09", page > 0 ? ColorCardDark : ColorCard, page > 0 ? command + (page - 1) : null, null, 13, page > 0 ? ColorText : ColorMuted);
            AddButton(ui, window, Lang("CalvarioNextPage", userId), "0.6 0.03", "0.75 0.09", page < pages - 1 ? ColorCardDark : ColorCard, page < pages - 1 ? command + (page + 1) : null, null, 13, page < pages - 1 ? ColorText : ColorMuted);
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

            AddText(ui, window, Lang("CalvarioNextV3", userId, next.Name, FormatBaldness(next.MinBaldness - baldness)), 11, TextAnchor.MiddleRight, "0.45 0.898", "0.935 0.925", ColorMuted);
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
                ? Lang("BarberGreetingV2_" + (pick + 1), userId)
                : Lang("CalvarioProverb" + ProverbNumbers[pick - BarberGreetingCount], userId);
        }

        private void AddExchangeOption(List<KeyValuePair<string, string>> options, ExchangeMode mode, string text, string userId)
        {
            bool available = ExchangeModeAvailable(mode);
            string plugin = mode == ExchangeMode.SellForRp || mode == ExchangeMode.BuyWithRp ? "Server Rewards" : "Economics";
            options.Add(new KeyValuePair<string, string>(available ? text : Lang("BarberExClosedV3", userId, text, plugin),
                available ? "calvos.exchange mode " + mode : null));
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
                case "catalog":
                    OpenCatalog(player, null);
                    break;
                case "carne":
                    OpenBarber(player, BarberPage.Carne, null);
                    break;
                case "exchange":
                    pendingExchanges.Remove((ulong)player.userID);
                    OpenBarber(player, BarberPage.Exchange, null);
                    break;
                case "bye":
                    CuiHelper.DestroyUi(player, UiMenu);
                    break;
                default:
                    OpenBarber(player, BarberPage.Main, BarberGreeting(player.UserIDString));
                    break;
            }
        }

        [ConsoleCommand("calvos.exchange")]
        private void CcmdExchange(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] parts = MenuArgs(arg);
            if (!IsRealPlayer(player) || parts.Length == 0 || !config.Exchange.Enabled || !RequireCalvarioNpc(player))
            {
                return;
            }

            ulong id = (ulong)player.userID;
            switch (parts[0])
            {
                case "mode":
                    if (parts.Length > 1 && Enum.TryParse(parts[1], out ExchangeMode mode) && Enum.IsDefined(typeof(ExchangeMode), mode) && ExchangeModeAvailable(mode))
                    {
                        pendingExchanges[id] = new PendingExchange { Mode = mode };
                        OpenBarber(player, BarberPage.ExchangeAmount, null);
                    }

                    break;
                case "amount":
                    if (pendingExchanges.TryGetValue(id, out PendingExchange pending) && parts.Length > 1
                        && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long amount)
                        && config.Exchange.Amounts.Contains(amount) && ExchangeAmountValid(pending.Mode, amount))
                    {
                        pending.Baldness = amount;
                        OpenBarber(player, BarberPage.ExchangeConfirm, null);
                    }

                    break;
                case "confirm":
                    // The amount comes from the server-side pending exchange, never from the button.
                    if (pendingExchanges.TryGetValue(id, out PendingExchange confirmed) && confirmed.Baldness > 0)
                    {
                        pendingExchanges.Remove(id);
                        OpenBarber(player, BarberPage.Exchange, DoExchange(player, confirmed.Mode, confirmed.Baldness));
                    }

                    break;
            }
        }

        private bool ExchangeModeAvailable(ExchangeMode mode) =>
            mode == ExchangeMode.SellForRp || mode == ExchangeMode.BuyWithRp ? RpAvailable
                : mode == ExchangeMode.SellForCoins ? CoinsAvailable && config.Exchange.SellCoinsPer100 > 0 : CoinsAvailable;

        private bool IsSell(ExchangeMode mode) => mode == ExchangeMode.SellForRp || mode == ExchangeMode.SellForCoins;

        // Selling needs whole RP/coins: at least the minimum and an exact multiple of the rate.
        private bool ExchangeAmountValid(ExchangeMode mode, long baldness)
        {
            ExchangeConfig ex = config.Exchange;
            switch (mode)
            {
                case ExchangeMode.SellForRp: return baldness >= ex.MinSell && baldness % ex.SellBaldnessPerRp == 0;
                case ExchangeMode.SellForCoins: return baldness >= ex.MinSell && baldness % 100 == 0;
                default: return baldness > 0;
            }
        }

        // RP or coins the player gets (selling) or pays (buying) for that much baldness.
        private long ExchangePrice(ExchangeMode mode, long baldness)
        {
            ExchangeConfig ex = config.Exchange;
            switch (mode)
            {
                case ExchangeMode.SellForRp: return baldness / ex.SellBaldnessPerRp;
                case ExchangeMode.SellForCoins: return SaturatingMultiply(baldness / 100, ex.SellCoinsPer100);
                case ExchangeMode.BuyWithRp: return SaturatingMultiply(baldness, ex.BuyRpPerBaldness);
                default: return SaturatingMultiply(baldness, ex.BuyCoinsPerBaldness);
            }
        }

        private string ExchangePriceText(ExchangeMode mode, long price, string userId) =>
            UnitText(mode == ExchangeMode.SellForRp || mode == ExchangeMode.BuyWithRp, price, userId);

        // "1 Punto de Chola" / "2 Puntos de Chola", "1 pelón" / "2 pelones".
        private string UnitText(bool rp, long amount, string userId) =>
            Lang(rp ? (amount == 1 ? "UnitRpOneV2" : "UnitRpV2") : (amount == 1 ? "UnitCoinsOneV2" : "UnitCoinsV2"), userId, FormatBaldness(amount));

        private bool CanAffordExchange(PlayerData data, ExchangeMode mode, long baldness)
        {
            long price = ExchangePrice(mode, baldness);
            switch (mode)
            {
                case ExchangeMode.SellForRp:
                case ExchangeMode.SellForCoins:
                    return data.Baldness >= baldness;
                case ExchangeMode.BuyWithRp:
                    return CheckRp(data.Id) >= price;
                default:
                    return CoinBalance(data.Id) >= price;
            }
        }

        // The other plugin is paid or charged first; baldness only changes if that worked.
        private string DoExchange(BasePlayer player, ExchangeMode mode, long baldness)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            if (!ExchangeModeAvailable(mode) || !ExchangeAmountValid(mode, baldness))
            {
                return Lang("BarberExFailed", userId);
            }

            if (!CanAffordExchange(data, mode, baldness))
            {
                return Lang("BarberExNotEnough", userId);
            }

            long price = ExchangePrice(mode, baldness);
            bool done;
            switch (mode)
            {
                case ExchangeMode.SellForRp: done = AddRp(data.Id, price); break;
                case ExchangeMode.SellForCoins: done = DepositCoins(data.Id, price); break;
                case ExchangeMode.BuyWithRp: done = TakeRp(data.Id, price); break;
                default: done = WithdrawCoins(data.Id, price); break;
            }

            if (!done)
            {
                return Lang("BarberExFailed", userId);
            }

            string priceText = ExchangePriceText(mode, price, userId);
            if (IsSell(mode))
            {
                ChangeBaldness(data, -baldness, true, Lang("ReasonExchange"));
                return Lang("BarberExDoneSellV2", userId, FormatBaldness(baldness), priceText);
            }

            // Bought baldness is not multiplied by events or the battery, only pays tier prizes if the config says so and
            // does not count for the Calvo del Día.
            long before = data.Baldness;
            ChangeBaldness(data, baldness, true, Lang("ReasonExchange"), true);
            ExcludeFromCalvoDelDia(data, before);
            return Lang("BarberExDoneBuyV2", userId, FormatBaldness(baldness), priceText);
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
                        var status = new List<string> { Lang(options.Count > 0 ? "BarberItemsIntroV3" : "BarberItemsNoneV3", userId) };
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
                    carne.Add(Lang("BarberCarneIntroV2", userId, stamped.Length, tags.Shortnames.Count, data.CarnesCompleted));
                    if (stamped.Length > 0) carne.Add(Lang("BarberCarneStamped", userId, string.Join(", ", stamped)));
                    if (missing.Length > 0) carne.Add(Lang("BarberCarneMissingV2", userId, string.Join(", ", missing)));
                    carne.Add(Lang("CalvarioCarneHintV2", userId, FormatBaldness(tags.Reward), FormatBaldness(tags.CollectionBonus)));
                    line = string.Join("\n", carne.ToArray());

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptStamp", userId), "calvos.carne"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                case BarberPage.Exchange:
                    ExchangeConfig ex = config.Exchange;
                    if (line != null)
                    {
                        line += "\n";
                    }

                    line += Lang("BarberExIntroV4", userId, FormatBaldness(data.Baldness),
                        RpAvailable ? FormatBaldness(CheckRp(data.Id)) : "-", CoinsAvailable ? FormatBaldness(CoinBalance(data.Id)) : "-");
                    AddExchangeOption(options, ExchangeMode.SellForRp, Lang("BarberExSellRpV4", userId, FormatBaldness(ex.SellBaldnessPerRp)), userId);
                    AddExchangeOption(options, ExchangeMode.SellForCoins, Lang("BarberExSellCoinsV4", userId, FormatBaldness(ex.SellCoinsPer100)), userId);
                    AddExchangeOption(options, ExchangeMode.BuyWithRp, Lang("BarberExBuyRpV5", userId, FormatBaldness(ex.BuyRpPerBaldness)), userId);
                    AddExchangeOption(options, ExchangeMode.BuyWithCoins, Lang("BarberExBuyCoinsV4", userId, FormatBaldness(ex.BuyCoinsPerBaldness)), userId);
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                case BarberPage.ExchangeAmount:
                    if (!pendingExchanges.TryGetValue((ulong)player.userID, out PendingExchange amountFor))
                    {
                        goto case BarberPage.Exchange;
                    }

                    line = Lang("BarberExPickAmount", userId);
                    foreach (long amount in config.Exchange.Amounts)
                    {
                        if (!ExchangeAmountValid(amountFor.Mode, amount))
                        {
                            continue;
                        }

                        string text = Lang(IsSell(amountFor.Mode) ? "BarberExSellLineV2" : "BarberExBuyLineV2", userId,
                            FormatBaldness(amount), ExchangePriceText(amountFor.Mode, ExchangePrice(amountFor.Mode, amount), userId));
                        bool affordable = CanAffordExchange(data, amountFor.Mode, amount);
                        options.Add(new KeyValuePair<string, string>(affordable ? text : Lang("BarberExTooMuchV3", userId, text),
                            affordable ? "calvos.exchange amount " + amount.ToString(CultureInfo.InvariantCulture) : null));
                    }

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber exchange"));
                    break;
                case BarberPage.ExchangeConfirm:
                    if (!pendingExchanges.TryGetValue((ulong)player.userID, out PendingExchange toConfirm) || toConfirm.Baldness <= 0)
                    {
                        goto case BarberPage.Exchange;
                    }

                    line = Lang(IsSell(toConfirm.Mode) ? "BarberExConfirmSellV2" : "BarberExConfirmBuyV2", userId, FormatBaldness(toConfirm.Baldness),
                        ExchangePriceText(toConfirm.Mode, ExchangePrice(toConfirm.Mode, toConfirm.Baldness), userId));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberExConfirm", userId), "calvos.exchange confirm"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberExCancel", userId), "calvos.barber exchange"));
                    break;
                default:
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptItemsV3", userId), "calvos.barber items"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptCatalog", userId), "calvos.barber catalog"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptCarne", userId), "calvos.barber carne"));
                    if (config.Exchange.Enabled)
                    {
                        options.Add(new KeyValuePair<string, string>(Lang("BarberOptExchangeV2", userId), "calvos.barber exchange"));
                    }

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
            AddText(ui, box, Lang("CalvarioYouV3", userId, FormatCompact(data.Baldness), GetTitle(data.Baldness)), 12, TextAnchor.MiddleRight, "0.5 0.88", "0.93 0.98", ColorMuted);
            AddButton(ui, box, Lang("CalvarioClose", userId), "0.945 0.9", "0.99 0.98", ColorPoleRed, null, UiMenu, 14);
            AddPanel(ui, box, ColorScalp, "0.03 0.872", "0.97 0.876");
            AddText(ui, box, line ?? string.Empty, 14, TextAnchor.UpperLeft, "0.03 0.5", "0.97 0.855", ColorText);

            // Answers stacked from the bottom, like the vanilla dialogue options.
            const float rowHeight = 0.052f, gap = 0.006f;
            for (int i = 0; i < options.Count; i++)
            {
                int fromBottom = options.Count - 1 - i;
                float y0 = 0.03f + fromBottom * (rowHeight + gap);
                bool enabled = options[i].Value != null;
                AddButton(ui, box, (i + 1) + ". " + options[i].Key, Anchor(0.03f, y0), Anchor(0.97f, y0 + rowHeight),
                    enabled ? ColorDialogOption : ColorDisabled, options[i].Value, null, 12, enabled ? ColorText : ColorMuted, TextAnchor.MiddleLeft);
            }

            CuiHelper.AddUi(player, ui);
        }

        // The barber's catalog: the 1.3 El Calvario window (every relic with its icon, what it does and how many you carry,
        // and the Carne de Calvo row), opened from the barber's main page. Buttons answer back in this window.
        private void OpenCatalog(BasePlayer player, string line)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            var ui = new CuiElementContainer();
            string window = DrawCalvarioWindow(ui, data, userId);

            // Section label, the barber's last word (or the intro) and the way back to the conversation.
            string section = AddPanel(ui, window, ColorScalp, "0.03 0.81", "0.21 0.86");
            AddText(ui, section, Lang("CatalogSection", userId), 13, TextAnchor.MiddleCenter, "0 0", "1 1", ColorOnScalp);
            AddText(ui, window, line ?? Lang("CatalogIntro", userId), 12, TextAnchor.MiddleLeft, "0.225 0.805", "0.78 0.865", line != null ? ColorScalp : ColorText);
            AddButton(ui, window, Lang("CatalogBack", userId), "0.79 0.81", "0.97 0.86", ColorCardDark, "calvos.barber main", null, 12, ColorText);

            // 7 relic cards (4 + 3) and a proverb card in the eighth slot.
            for (int i = 0; i <= CursedItemKeys.Length; i++)
            {
                int row = i / 4, col = i % 4;
                float x0 = 0.03f + col * 0.2375f, x1 = x0 + 0.2275f;
                float y1 = 0.79f - row * 0.245f, y0 = y1 - 0.235f;
                string card = AddPanel(ui, window, ColorCard, Anchor(x0, y0), Anchor(x1, y1));

                if (i == CursedItemKeys.Length)
                {
                    AddText(ui, card, Lang("CatalogWisdom", userId), 14, TextAnchor.UpperCenter, "0.05 0.7", "0.95 0.93", ColorScalp);
                    AddText(ui, card, "\"" + Lang("CalvarioProverb" + ProverbNumbers[random.Next(ProverbNumbers.Length)], userId) + "\"", 14, TextAnchor.MiddleCenter, "0.07 0.1", "0.93 0.7", ColorText);
                    break;
                }

                string key = CursedItemKeys[i];
                ItemDropConfig item = CursedItemConfig(key);
                int count = CountItem(player, item.Shortname);
                AddPanel(ui, card, ColorCardDark, "0.04 0.52", "0.3 0.95");
                AddIcon(ui, card, item.Shortname, "0.06 0.55", "0.28 0.92", count > 0 ? "1 1 1 1" : "1 1 1 0.35");
                AddText(ui, card, CursedItemName(key), 14, TextAnchor.UpperLeft, "0.34 0.74", "0.98 0.95", count > 0 ? ColorScalp : ColorMuted);
                AddText(ui, card, Lang("CatalogHave", userId, count), 12, TextAnchor.UpperLeft, "0.34 0.54", "0.98 0.74", count > 0 ? ColorText : ColorMuted);

                string status = null;
                if (key == "ducttape" && data.HasDeathShield) status = Lang("CalvarioShieldOn", userId);
                if (key == "battery" && IsBatteryActive(data.Id)) status = Lang("CalvarioBatteryOn", userId, BatteryMinutesLeft(data.Id));
                AddText(ui, card, status ?? CursedItemDescription(key), 11, TextAnchor.UpperLeft, "0.05 0.22", "0.97 0.5", status != null ? ColorGold : count > 0 ? ColorText : ColorMuted);

                AddButton(ui, card, Lang(count > 0 ? "CatalogUse" : "CatalogNone", userId), "0.05 0.05", "0.95 0.2",
                    count > 0 ? ColorPoleRed : ColorDisabled, count > 0 ? "calvos.use " + key + " catalog" : null, null, 13, count > 0 ? "1 1 1 1" : ColorMuted);
            }

            // Carne de Calvo: one slot per ID tag color, stamped or still missing.
            IdTagsConfig tags = config.CursedItems.IdTags;
            string carne = AddPanel(ui, window, ColorCard, "0.03 0.03", "0.97 0.295");

            int done = tags.Shortnames.Count(c => data.CarneColors.Contains(c));
            AddText(ui, carne, Lang("CatalogCarneTitle", userId, done, tags.Shortnames.Count, data.CarnesCompleted), 14, TextAnchor.MiddleLeft, "0.02 0.78", "0.98 0.97", ColorScalp);
            AddText(ui, carne, Lang("CalvarioCarneHintV2", userId, FormatBaldness(tags.Reward), FormatBaldness(tags.CollectionBonus)), 11, TextAnchor.MiddleLeft, "0.02 0.62", "0.98 0.78", ColorText);

            bool canDeliver = false;
            int slots = Math.Max(1, tags.Shortnames.Count);
            float slotWidth = 0.82f / slots;
            for (int i = 0; i < tags.Shortnames.Count; i++)
            {
                string color = tags.Shortnames[i];
                bool delivered = data.CarneColors.Contains(color);
                int carried = CountItem(player, color);
                canDeliver |= !delivered && carried > 0;

                float x0 = 0.02f + i * slotWidth, x1 = x0 + slotWidth - 0.006f;
                string slot = AddPanel(ui, carne, delivered ? ColorGood : ColorCardDark, Anchor(x0, 0.08f), Anchor(x1, 0.58f));
                AddIcon(ui, slot, color, "0.1 0.28", "0.9 0.95", delivered || carried > 0 ? "1 1 1 1" : "1 1 1 0.25");
                if (delivered)
                {
                    AddText(ui, slot, Lang("CatalogCarneStamped", userId), 9, TextAnchor.LowerCenter, "0 0.02", "1 0.28", ColorScalp);
                }
                else if (carried > 0)
                {
                    AddText(ui, slot, "x" + carried, 11, TextAnchor.LowerCenter, "0 0.02", "1 0.28");
                }
                else
                {
                    AddText(ui, slot, Lang("CatalogCarneMissing", userId), 9, TextAnchor.LowerCenter, "0 0.02", "1 0.28", ColorMuted);
                }
            }

            AddButton(ui, carne, Lang(canDeliver ? "CatalogCarneDeliver" : "CatalogCarneNone", userId), "0.86 0.12", "0.98 0.52",
                canDeliver ? ColorPoleRed : ColorDisabled, canDeliver ? "calvos.carne catalog" : null, null, 12, canDeliver ? "1 1 1 1" : ColorMuted);

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
                AddText(ui, window, Lang("CalvarioRankingEmptyV2", userId), 16, TextAnchor.MiddleCenter, "0.03 0.4", "0.97 0.6", ColorText);
                return;
            }

            string[] medals = PodiumColors;
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
                AddText(ui, row, FormatCompact(entry.Baldness), 14, TextAnchor.MiddleRight, "0.5 0", "0.68 1", ColorGold);
                AddText(ui, row, GetTitle(entry.Baldness), 13, TextAnchor.MiddleRight, "0.68 0", "0.98 1", ColorText);
            }

            int myIndex = ranking.IndexOf(me);
            string footer = myIndex <= 0
                ? Lang("CalvarioRankingFirstV2", userId)
                : Lang("CalvarioRankingYouV3", userId, myIndex + 1, ranking.Count, FormatBaldness(ranking[myIndex - 1].Baldness - me.Baldness + 1), ranking[myIndex - 1].Name);
            AddText(ui, window, footer, 14, TextAnchor.MiddleLeft, "0.03 0.1", "0.97 0.17", ColorText);
            DrawPager(ui, window, page, pages, MenuTab.Ranking, userId);
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

        #region Hall of Fame, Bounties and Calvo del Día

        // Saves the current state as a hall of fame entry. Null (nothing saved) if the map is empty: for a map close, nobody
        // killed or died on it (alopecia is no sign of activity: without the wipe reset it never goes back to 0); for a
        // snapshot by hand, nobody has alopecia or kills. The caller calls the hook.
        private HallEntry SaveHallEntry(bool manual)
        {
            List<PlayerData> players = storedData.Players.Values.Where(d => d != null).ToList();
            if (!manual && !players.Any(d => d.WipeKills > 0 || d.WipeDeaths > 0))
            {
                Puts("Hall of fame: nothing to save (nobody killed or died on this map).");
                return null;
            }

            if (manual && !players.Any(d => d.Baldness > 0 || d.WipeKills > 0))
            {
                Puts("Hall of fame: nothing to save (nobody has alopecia or kills on this map).");
                return null;
            }

            // Same order as the ranking.
            List<HallPlayer> podium = players
                .Where(d => d.Baldness > 0)
                .OrderByDescending(d => d.Baldness)
                .ThenByDescending(d => d.Kills)
                .Take(PodiumColors.Length)
                .Select(d => ToHallPlayer(d, d.Baldness))
                .ToList();
            PlayerData killer = players.Where(d => d.WipeKills > 0).OrderByDescending(d => d.WipeKills).ThenByDescending(d => d.Baldness).FirstOrDefault();
            PlayerData dead = players.Where(d => d.WipeDeaths > 0).OrderByDescending(d => d.WipeDeaths).ThenBy(d => d.Baldness).FirstOrDefault();

            var entry = new HallEntry
            {
                Number = storedData.HallNextNumber++,
                Date = DateTime.Now,
                Manual = manual,
                Podium = podium,
                TopKiller = killer == null ? null : ToHallPlayer(killer, killer.WipeKills),
                TopDeaths = dead == null ? null : ToHallPlayer(dead, dead.WipeDeaths)
            };

            storedData.HallOfFame.Add(entry);
            int max = config.HallOfFame.MaxEntries;
            if (max > 0 && storedData.HallOfFame.Count > max)
            {
                storedData.HallOfFame.RemoveRange(0, storedData.HallOfFame.Count - max);
            }

            dataDirty = true;
            Puts($"Hall of fame: entry #{entry.Number} saved ({(manual ? "snapshot by hand" : "map close")}).");
            return entry;
        }

        // For other plugins (JanoBridge): the saved entry as one line of JSON.
        private static void CallHallHook(HallEntry entry) => Interface.CallHook("OnIslaWipeHallOfFame", HallEntryJson(entry));

        // False until OnServerInitialized: OnNewSave runs while the world loads, when nobody can hear the hook yet.
        private bool serverReady;

        private void DeliverPendingWipeHook()
        {
            int number = storedData.PendingWipeHook;
            if (number == 0)
            {
                return;
            }

            storedData.PendingWipeHook = 0;
            dataDirty = true;
            HallEntry entry = storedData.HallOfFame.FirstOrDefault(e => e.Number == number);
            if (entry != null)
            {
                CallHallHook(entry);
            }
        }

        private class MapCloseResult
        {
            // Same wipe as the last close (LastClose): nothing was done.
            public bool Skipped;
            public DateTime LastClose;

            // Hall of fame entry saved (null = none) and whether alopecia went back to 0.
            public HallEntry Entry;
            public bool Reset;
        }

        // The last map close: the stored time, or the newest entry saved by a close (data from before 1.8.2).
        private DateTime LastMapCloseTime()
        {
            DateTime last = storedData.LastMapClose;
            foreach (HallEntry entry in storedData.HallOfFame)
            {
                if (!entry.Manual && entry.Date > last)
                {
                    last = entry.Date;
                }
            }

            return last;
        }

        // Everything a map wipe does: the hall of fame entry (with its announcement and hook), per-map kills and deaths to 0
        // and, only if the config says so, alopecia to 0. Called by OnNewSave, "/calvoadmin salon cerrar" and "isla.salon
        // cerrar". A second close within MapCloseRepeatHours is the same wipe and does nothing, unless forced.
        private MapCloseResult CloseMap(bool force, string source)
        {
            var result = new MapCloseResult();
            DateTime last = LastMapCloseTime();
            if (!force && last != default(DateTime) && DateTime.Now - last < TimeSpan.FromHours(MapCloseRepeatHours))
            {
                result.Skipped = true;
                result.LastClose = last;
                PrintWarning($"Map close ({source}) ignored: the map was already closed on {last:yyyy-MM-dd HH:mm}, less than {MapCloseRepeatHours} hours ago (same wipe). Nothing saved, announced or reset.");
                return result;
            }

            storedData.LastMapClose = DateTime.Now;

            // A new map: the previous map's announcement is over.
            storedData.PendingWipeAnnouncement = 0;
            storedData.WipeAnnouncementSeen.Clear();
            dataDirty = true;

            // The hall of fame keeps the finished map's numbers, so it goes first, before any reset.
            if (config.HallOfFame.Enabled)
            {
                HallEntry entry = SaveHallEntry(false);
                result.Entry = entry;
                if (entry != null)
                {
                    if (entry.Podium.Count > 0 && config.HallOfFame.AnnounceWinner)
                    {
                        // Each player gets it the first time they wake up on the new map.
                        storedData.PendingWipeAnnouncement = entry.Number;
                    }

                    if (serverReady)
                    {
                        CallHallHook(entry);
                    }
                    else
                    {
                        storedData.PendingWipeHook = entry.Number;
                    }
                }
            }

            foreach (PlayerData data in storedData.Players.Values)
            {
                data.WipeKills = 0;
                data.WipeDeaths = 0;
            }

            if (!config.ResetBaldnessOnWipe)
            {
                SaveData();
                Puts($"Map closed ({source}): per-map kills and deaths reset (baldness kept).");
                return result;
            }

            result.Reset = true;
            foreach (PlayerData data in storedData.Players.Values)
            {
                data.Baldness = MinBaldness;
                data.SurvivalSeconds = 0f;
            }

            // Everybody starts from 0, so the Calvo del Día measures gains from 0 too.
            storedData.CalvoDelDia.Snapshot.Clear();
            killCooldowns.Clear();
            SaveData();

            // Everybody is back to 0, so nobody keeps a title group (offline players too: groups work by id).
            foreach (PlayerData data in storedData.Players.Values)
            {
                SyncTitleGroup(data);
            }

            // Online players see their counter at 0 right away (after a real wipe nobody is online yet).
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player) && !player.IsSleeping())
                {
                    DrawCounter(player);
                }
            }

            Puts($"Map closed ({source}): baldness and per-map kills and deaths reset for all players (stats kept).");
            return result;
        }

        // Admin replies for a map close, for the chat (userId) or the console (null).
        private List<string> CloseMapReplies(MapCloseResult result, string userId)
        {
            var lines = new List<string>();
            if (result.Skipped)
            {
                lines.Add(Lang("AdminHallCloseRecent", userId, FormatDate(result.LastClose),
                    result.LastClose.ToString("HH:mm", CultureInfo.InvariantCulture), MapCloseRepeatHours));
                return lines;
            }

            lines.Add(result.Entry != null ? Lang("AdminHallClosed", userId, result.Entry.Number) : Lang("AdminHallClosedEmpty", userId));
            if (result.Reset)
            {
                lines.Add(Lang("AdminHallClosedReset", userId));
            }

            return lines;
        }

        private static HallPlayer ToHallPlayer(PlayerData data, long value) =>
            new HallPlayer { Id = data.Id, Name = data.Name ?? string.Empty, Value = value };

        // SteamIDs go as text: a SteamID64 does not fit in a JavaScript number.
        private static string HallEntryJson(HallEntry entry) => JsonConvert.SerializeObject(new
        {
            number = entry.Number,
            date = entry.Date.ToString("s", CultureInfo.InvariantCulture),
            manual = entry.Manual,
            podium = entry.Podium.Select(p => new { id = p.Id.ToString(CultureInfo.InvariantCulture), name = p.Name ?? string.Empty, alopecia = p.Value }).ToList(),
            topKiller = entry.TopKiller == null ? null
                : (object)new { id = entry.TopKiller.Id.ToString(CultureInfo.InvariantCulture), name = entry.TopKiller.Name ?? string.Empty, kills = entry.TopKiller.Value },
            topDeaths = entry.TopDeaths == null ? null
                : (object)new { id = entry.TopDeaths.Id.ToString(CultureInfo.InvariantCulture), name = entry.TopDeaths.Name ?? string.Empty, deaths = entry.TopDeaths.Value }
        }, Formatting.None);

        // Players whose announcement is waiting for its timer. In memory only.
        private readonly HashSet<ulong> wipeAnnouncementQueued = new HashSet<ulong>();

        // Called when a player wakes up: the first time after a map close, the finished map's winner in their own chat.
        private void AnnounceWipeWinner(BasePlayer player)
        {
            ulong id = (ulong)player.userID;
            if (storedData.PendingWipeAnnouncement == 0 || storedData.WipeAnnouncementSeen.Contains(id) || !wipeAnnouncementQueued.Add(id))
            {
                return;
            }

            timer.Once(WipeAnnouncementDelaySeconds, () =>
            {
                wipeAnnouncementQueued.Remove(id);
                int number = storedData.PendingWipeAnnouncement;
                HallEntry entry = number == 0 ? null : storedData.HallOfFame.FirstOrDefault(e => e.Number == number);
                if (entry == null || entry.Podium.Count == 0 || player == null || !player.IsConnected || !storedData.WipeAnnouncementSeen.Add(id))
                {
                    return;
                }

                dataDirty = true;
                Reply(player, "HallWipeWinner", entry.Podium[0].Name, FormatBaldness(entry.Podium[0].Value));
            });
        }

        private static string FormatDate(DateTime date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

        // A PvP kill pays the victim's whole bounty to the killer, unless they are teammates or (by config) the victim was
        // asleep or offline. If Server Rewards does not pay, the bounty stays for the next one.
        private void TryClaimBounty(BasePlayer killer, PlayerData killerData, BasePlayer victim, PlayerData victimData)
        {
            if (!config.Bounties.Enabled || !storedData.Bounties.TryGetValue(victimData.Id, out long total) || total <= 0)
            {
                return;
            }

            string whyNot = null;
            if (killer.currentTeam != 0UL && killer.currentTeam == victim.currentTeam)
            {
                whyNot = Lang("NoBountyTeam");
            }
            else if (config.Bounties.NotOnSleepers && (victim.IsSleeping() || !victim.IsConnected))
            {
                whyNot = Lang("NoBountySleeper");
            }
            else if (!RpAvailable)
            {
                whyNot = Lang("NoRpPlugin");
            }
            else if (!AddRp(killerData.Id, total))
            {
                whyNot = Lang("NoRpRefused");
            }

            if (whyNot != null)
            {
                SendDebug("DebugBountyNoClaim", killerData.Name, victimData.Name, whyNot);
                return;
            }

            storedData.Bounties.Remove(victimData.Id);
            dataDirty = true;
            Broadcast("BountyClaimed", killerData.Name, victimData.Name, UnitText(true, total, null));
            Interface.CallHook("OnIslaBountyClaimed", killerData.Id, killerData.Name ?? string.Empty, victimData.Id, victimData.Name ?? string.Empty, total);
            Puts($"Bounty: {killerData.Name} ({killerData.Id}) claimed {total} Puntos de Chola for {victimData.Name} ({victimData.Id}).");
        }

        private static bool IsOnline(ulong id)
        {
            BasePlayer player = BasePlayer.FindByID(id);
            return player != null && player.IsConnected;
        }

        private static string TodayKey() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private void InitCalvoDelDia()
        {
            CalvoDelDiaData day = storedData.CalvoDelDia;
            if (day.SnapshotTime == default(DateTime))
            {
                // First load with the Calvo del Día: gains count from now on. Past today's pick time, the first pick is tomorrow.
                TakeCalvoDelDiaSnapshot(day);
                if (DateTime.Now.TimeOfDay.TotalMinutes >= calvoDelDiaMinutes)
                {
                    day.LastPickDate = TodayKey();
                }

                Puts("Calvo del Día: first alopecia snapshot taken.");
            }

            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player) && player.IsConnected)
                {
                    day.Seen.Add((ulong)player.userID);
                }
            }

            dataDirty = true;
            SyncCalvoDelDiaGroup(day.CurrentId);
        }

        // Every minute: once a day, at the configured time or the first check after it (e.g. the server was down at pick time).
        private void CheckCalvoDelDia()
        {
            string today = TodayKey();
            if (storedData.CalvoDelDia.LastPickDate == today || DateTime.Now.TimeOfDay.TotalMinutes < calvoDelDiaMinutes)
            {
                return;
            }

            storedData.CalvoDelDia.LastPickDate = today;
            dataDirty = true;
            PickCalvoDelDia();
        }

        private void MarkSeenForCalvoDelDia(ulong id)
        {
            if (config.CalvoDelDia.Enabled && storedData.CalvoDelDia.Seen.Add(id))
            {
                dataDirty = true;
            }
        }

        // Bought and admin alopecia do not count for the Calvo del Día, up or down: the player's snapshot moves with it.
        private void ExcludeFromCalvoDelDia(PlayerData data, long before)
        {
            long delta = data.Baldness - before;
            if (delta == 0)
            {
                return;
            }

            Dictionary<ulong, long> snapshot = storedData.CalvoDelDia.Snapshot;
            snapshot[data.Id] = SaturatingAdd(snapshot.TryGetValue(data.Id, out long old) ? old : 0, delta);
            dataDirty = true;
        }

        // Alopecia of every player right now; only the players online now count as seen for the next pick.
        private void TakeCalvoDelDiaSnapshot(CalvoDelDiaData day)
        {
            day.Snapshot = storedData.Players.ToDictionary(p => p.Key, p => p.Value.Baldness);
            day.SnapshotTime = DateTime.Now;
            day.Seen = new HashSet<ulong>(BasePlayer.activePlayerList.Where(p => IsRealPlayer(p) && p.IsConnected).Select(p => (ulong)p.userID));
            dataDirty = true;
        }

        // The player who gained the most alopecia since the last snapshot (net: deaths count), among those seen online since
        // then, becomes the Calvo del Día; then a new snapshot is taken. Null if nobody gained anything: no Calvo del Día.
        private CalvoDelDiaRecord PickCalvoDelDia()
        {
            CalvoDelDiaData day = storedData.CalvoDelDia;
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player) && player.IsConnected)
                {
                    day.Seen.Add((ulong)player.userID);
                }
            }

            PlayerData best = null;
            long bestGain = 0;
            foreach (ulong id in day.Seen)
            {
                if (!storedData.Players.TryGetValue(id, out PlayerData data))
                {
                    continue;
                }

                long gained = data.Baldness - (day.Snapshot.TryGetValue(id, out long before) ? before : 0);
                if (gained <= 0)
                {
                    continue;
                }

                // Ties: more alopecia, then the lower id, so the result does not depend on the set's order.
                if (best == null || gained > bestGain || (gained == bestGain && (data.Baldness > best.Baldness || (data.Baldness == best.Baldness && id < best.Id))))
                {
                    best = data;
                    bestGain = gained;
                }
            }

            TakeCalvoDelDiaSnapshot(day);

            CalvoDelDiaRecord record = null;
            if (best != null)
            {
                record = new CalvoDelDiaRecord { Date = DateTime.Now, Id = best.Id, Name = best.Name ?? string.Empty, Gained = bestGain };
                day.History.Add(record);
                int max = config.CalvoDelDia.HistorySize;
                if (day.History.Count > max)
                {
                    day.History.RemoveRange(0, day.History.Count - max);
                }
            }

            day.CurrentId = best?.Id ?? 0UL;
            dataDirty = true;
            SyncCalvoDelDiaGroup(day.CurrentId);

            if (record == null)
            {
                Puts("Calvo del Día: nobody gained alopecia since the last pick; no Calvo del Día until the next one.");
                return null;
            }

            Broadcast("CalvoDelDiaChatV3", record.Name, FormatBaldness(record.Gained));
            ShowBanner(Lang("CalvoDelDiaBannerV2", null, record.Name), config.Ui.TitleUpBannerSeconds);
            PayCalvoDelDiaPrize(best);
            Interface.CallHook("OnIslaCalvoDelDia", record.Id, record.Name, record.Gained);
            Puts($"Calvo del Día: {record.Name} ({record.Id}), +{record.Gained} alopecia.");
            return record;
        }

        private CalvoDelDiaRecord CurrentCalvoDelDia()
        {
            CalvoDelDiaData day = storedData.CalvoDelDia;
            if (!config.CalvoDelDia.Enabled || day.CurrentId == 0UL || day.History.Count == 0)
            {
                return null;
            }

            CalvoDelDiaRecord last = day.History[day.History.Count - 1];
            return last.Id == day.CurrentId ? last : null;
        }

        // Only the current Calvo del Día is in the group: everybody else leaves it (by id, so offline players too).
        private void SyncCalvoDelDiaGroup(ulong current)
        {
            if (calvoDelDiaGroup == null)
            {
                return;
            }

            EnsureGroup(calvoDelDiaGroup);
            string currentId = current == 0UL ? null : current.ToString(CultureInfo.InvariantCulture);

            // Oxide lists members as "<id> (<last nickname>)".
            foreach (string member in permission.GetUsersInGroup(calvoDelDiaGroup))
            {
                string memberId = member.Split(' ')[0];
                if (!string.Equals(memberId, currentId, StringComparison.OrdinalIgnoreCase))
                {
                    permission.RemoveUserGroup(memberId, calvoDelDiaGroup);
                }
            }

            if (currentId != null && !permission.UserHasGroup(currentId, calvoDelDiaGroup))
            {
                permission.AddUserGroup(currentId, calvoDelDiaGroup);
            }
        }

        // Like a tier prize: Puntos de Chola and pelones always, items only if the winner is online at the pick.
        private void PayCalvoDelDiaPrize(PlayerData data)
        {
            TierPrize prize = config.CalvoDelDia.Prize;
            if (prize == null || prize.IsEmpty)
            {
                return;
            }

            BasePlayer player = BasePlayer.FindByID(data.Id);
            var parts = new List<string>();
            var given = new List<string>();
            GivePrize(data, player, prize, parts, given);
            SendDebug("DebugTierPrize", data.Name, Lang("CalvoDelDiaName"), parts.Count + given.Count > 0 ? string.Join(", ", parts.Concat(given).ToArray()) : "-");
            if (player == null || !player.IsConnected)
            {
                return;
            }

            if (parts.Count > 0)
            {
                Reply(player, "CalvoDelDiaPrize", string.Join(", ", parts.ToArray()));
            }
            else if (given.Count > 0)
            {
                Reply(player, "CalvoDelDiaPrizeItems");
            }

            if (!string.IsNullOrEmpty(prize.Message))
            {
                SendChat(player, prize.Message);
            }
        }

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

            // Building pieces, doors and deployables of a raided base (RaidableBases spawns them with OwnerID 0) are not
            // NPCs: door.hinged.metal, wall.frame, locker.deployed, chair.deployed, repairbench_deployed...
            BaseEntity baseEntity = entity;
            if (baseEntity is BuildingBlock || baseEntity is Door || baseEntity is DecayEntity || IsDeployedPrefab(entity.ShortPrefabName))
            {
                return false;
            }

            return entity.OwnerID == 0UL && !(baseEntity is LootContainer) && !(baseEntity is ResourceEntity);
        }

        // Deployables are named "<item>.deployed" or "<item>_deployed" (chair.deployed, repairbench_deployed).
        private static bool IsDeployedPrefab(string prefab) =>
            prefab.EndsWith(".deployed", StringComparison.OrdinalIgnoreCase) || prefab.EndsWith("_deployed", StringComparison.OrdinalIgnoreCase);

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
                DebugNoReward(killerData, Lang("NoRewardSleeperV2", null, victimName));
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
                TrackMovement(player, data);
                data.SurvivalSeconds += SurvivalTickSeconds;
                dataDirty = true;

                if (data.SurvivalSeconds >= interval)
                {
                    data.SurvivalSeconds -= interval;
                    bool moved = data.SurvivalMoved;
                    data.SurvivalMoved = false;
                    if (config.SurvivalRequireMovement && !moved)
                    {
                        DebugNoReward(data, Lang("NoRpAfk"));
                    }
                    else
                    {
                        GainBaldness(data, config.SurvivalReward, Lang("ReasonSurvival"));
                    }
                }

                RewardPointsTick(player, data);
            }
        }

        // One position check per tick feeds both the survival reward and the RP payout (anti-AFK).
        private void TrackMovement(BasePlayer player, PlayerData data)
        {
            Vector3 position = player.transform.position;
            if (lastPositions.TryGetValue(data.Id, out Vector3 last) && Vector3.Distance(last, position) >= config.ServerRewards.MinMoveMeters)
            {
                data.SurvivalMoved = true;
                data.RpMoved = true;
            }

            lastPositions[data.Id] = position;
        }

        private void RewardPointsTick(BasePlayer player, PlayerData data)
        {
            ServerRewardsConfig rp = config.ServerRewards;
            if (!rp.Enabled)
            {
                return;
            }

            data.RpSeconds += SurvivalTickSeconds;
            if (data.RpSeconds < rp.IntervalMinutes * 60f)
            {
                return;
            }

            data.RpSeconds = 0f;
            bool moved = data.RpMoved;
            data.RpMoved = false;

            long amount = GetRpRate(data.Baldness);
            if (amount <= 0)
            {
                return;
            }

            if (rp.RequireMovement && !moved)
            {
                SendDebug("DebugNoRpV2", data.Name, Lang("NoRpAfk"));
                return;
            }

            if (ServerRewards == null || !ServerRewards.IsLoaded)
            {
                if (!warnedNoServerRewards)
                {
                    warnedNoServerRewards = true;
                    PrintWarning("Server Rewards is not loaded; no Puntos de Chola are being paid.");
                }

                SendDebug("DebugNoRpV2", data.Name, Lang("NoRpPlugin"));
                return;
            }

            long room = RpRoom(data.Id);
            if (amount > room)
            {
                if (!warnedRpCap)
                {
                    warnedRpCap = true;
                    PrintWarning($"{data.Name} is at Server Rewards' Puntos de Chola limit; payouts are capped so the balance does not wrap to negative.");
                }

                amount = room;
                if (amount <= 0)
                {
                    SendDebug("DebugNoRpV2", data.Name, Lang("NoRpCapV2"));
                    return;
                }
            }

            if (!AddRp(data.Id, amount))
            {
                SendDebug("DebugNoRpV2", data.Name, Lang("NoRpRefused"));
                return;
            }

            SendDebug("DebugRpV2", data.Name, FormatBaldness(amount), GetTitle(data.Baldness));
            if (rp.NotifyPlayer)
            {
                Reply(player, "RpEarnedV5", FormatBaldness(amount), GetTitle(data.Baldness));
            }
        }

        private long GetRpRate(long baldness)
        {
            long perX = config.ServerRewards.RpPerBaldness;
            if (perX > 0)
            {
                return Math.Max(0, baldness) / perX;
            }

            long amount = 0;
            foreach (KeyValuePair<long, int> rate in rpRates)
            {
                if (baldness >= rate.Key)
                {
                    amount = rate.Value;
                }
            }

            return amount;
        }

        private void ChangeBaldness(PlayerData data, long delta, bool announce, string reason, bool bought = false)
        {
            long oldValue = data.Baldness;
            long newValue = Math.Max(MinBaldness, SaturatingAdd(oldValue, delta));

            SendDebug("DebugChange", data.Name, FormatBaldness(oldValue), FormatBaldness(newValue),
                delta >= 0 ? "+" : string.Empty, FormatBaldness(delta), reason);

            if (newValue == oldValue)
            {
                return;
            }

            data.Baldness = newValue;
            dataDirty = true;
            RefreshCounter(data, newValue - oldValue);

            // The title the player actually holds (-1 = none, at 0). Groups follow it always, admin and bought changes too.
            int oldTitle = GetTitleIndex(oldValue);
            int newTitle = GetTitleIndex(newValue);
            if (newTitle != oldTitle)
            {
                SyncTitleGroup(data);
            }

            if (!announce)
            {
                return;
            }

            // Announcements keep their pre-1.7.0 rule: going between 0 and the first title is not announced.
            int oldTier = GetTierIndex(oldValue);
            int newTier = GetTierIndex(newValue);
            if (newTier > oldTier)
            {
                // Reaching the highest title gets its own announcement instead of the generic one.
                if (newTier == config.Titles.Count - 1 && config.AnnounceSupremeBaldness)
                {
                    Broadcast("SupremeBaldnessV6", data.Name);
                }
                else if (config.AnnounceTitleUp)
                {
                    Broadcast("TitleUpV3", data.Name, GetTitle(newValue));
                }

                if (config.Ui.ShowTitleUpBanner)
                {
                    ShowBanner(Lang("TitleUpBanner", null, data.Name, GetTitle(newValue).ToUpperInvariant()), config.Ui.TitleUpBannerSeconds);
                }
            }
            else if (newTier < oldTier)
            {
                if (config.AnnounceTitleDrop)
                {
                    Broadcast("TitleDropV2", data.Name, GetTitle(newValue));
                }

                if (config.Ui.ShowTitleDropBanner)
                {
                    ShowBanner(Lang("TitleDropBanner", null, data.Name, GetTitle(newValue).ToUpperInvariant()), config.Ui.TitleUpBannerSeconds);
                }
            }

            if (newTitle > oldTitle)
            {
                // From -1 too, so the first title (Greñas Sucias) has its prize.
                PayTierPrizes(data, oldTitle, newTitle, bought);
            }

            if (newTitle != oldTitle)
            {
                // For other plugins (JanoBridge). Empty title = none.
                Interface.CallHook("OnIslaTitleChanged", data.Id, data.Name ?? string.Empty, TitleName(oldTitle), TitleName(newTitle),
                    newTitle > oldTitle, newValue);
            }
        }

        // Pays each newly reached title's prize once. Bought baldness only counts if the config allows it.
        private void PayTierPrizes(PlayerData data, int oldTier, int newTier, bool bought)
        {
            if (data.PrizedTier < 0)
            {
                // First tier change since 1.6.0: the titles the player already had are not paid.
                data.PrizedTier = oldTier;
            }

            if (newTier <= data.PrizedTier)
            {
                return;
            }

            int from = data.PrizedTier + 1;
            data.PrizedTier = newTier;
            dataDirty = true;
            if (bought && !config.TierPrizesForBoughtBaldness)
            {
                return;
            }

            BasePlayer player = BasePlayer.FindByID(data.Id);
            for (int tier = from; tier <= newTier; tier++)
            {
                if (!tierPrizes.TryGetValue(tier, out TierPrize prize) || prize.IsEmpty)
                {
                    continue;
                }

                var parts = new List<string>();
                var given = new List<string>();
                GivePrize(data, player, prize, parts, given);
                SendDebug("DebugTierPrize", data.Name, config.Titles[tier].Name, parts.Count + given.Count > 0 ? string.Join(", ", parts.Concat(given).ToArray()) : "-");
                if (player == null || !player.IsConnected)
                {
                    continue;
                }

                if (parts.Count > 0)
                {
                    Reply(player, "TierPrizeV2", config.Titles[tier].Name, string.Join(", ", parts.ToArray()));
                }
                else if (given.Count > 0)
                {
                    Reply(player, "TierPrizeItems", config.Titles[tier].Name);
                }

                if (!string.IsNullOrEmpty(prize.Message))
                {
                    SendChat(player, prize.Message);
                }
            }
        }

        // Puntos de Chola and pelones go by id (online or not); items only to an online player. Fills in what was actually
        // given (parts: Puntos de Chola and pelones; given: items), for the messages.
        private void GivePrize(PlayerData data, BasePlayer player, TierPrize prize, List<string> parts, List<string> given)
        {
            if (prize.Rp > 0 && AddRp(data.Id, prize.Rp)) parts.Add(UnitText(true, prize.Rp, null));
            if (prize.Coins > 0 && DepositCoins(data.Id, prize.Coins)) parts.Add(UnitText(false, prize.Coins, null));
            if (prize.Items != null && player != null && player.IsConnected)
            {
                // Silent: Rust shows its own pickup notice for each item.
                foreach (PrizeItem item in prize.Items)
                {
                    if (item != null && item.Amount > 0 && !string.IsNullOrEmpty(item.Shortname) && GiveItem(player, item.Shortname, item.Amount, false))
                    {
                        given.Add(item.Shortname + " x" + item.Amount);
                    }
                }
            }
        }

        private bool RpAvailable => ServerRewards != null && ServerRewards.IsLoaded;
        private bool CoinsAvailable => Economics != null && Economics.IsLoaded;

        // Server Rewards 2.x API: AddPoints/TakePoints(ulong, int) -> bool, CheckPoints(ulong) -> int. It keeps
        // balances in an int and adds without an overflow check, so the int path never pushes one past int.MaxValue.
        // A Server Rewards patched to long can add AddPointsLong/TakePointsLong(ulong, long) -> bool and
        // CheckPointsLong(ulong) -> long; Oxide returns null for a method the plugin does not have (CSPlugin.OnCallHook
        // finds no hook), so a null result means "not available" and the int API is used instead.
        private bool AddRp(ulong id, long amount)
        {
            if (!RpAvailable || amount <= 0)
            {
                return false;
            }

            object result = ServerRewards.Call("AddPointsLong", id, amount);
            if (result != null)
            {
                return result is bool ok && ok;
            }

            return amount <= RpRoom(id) && ServerRewards.Call("AddPoints", id, (int)amount) is bool paid && paid;
        }

        private bool TakeRp(ulong id, long amount)
        {
            if (!RpAvailable || amount <= 0)
            {
                return false;
            }

            object result = ServerRewards.Call("TakePointsLong", id, amount);
            if (result != null)
            {
                return result is bool ok && ok;
            }

            return amount <= int.MaxValue && ServerRewards.Call("TakePoints", id, (int)amount) is bool taken && taken;
        }

        private long CheckRp(ulong id)
        {
            if (!RpAvailable)
            {
                return 0;
            }

            if (ServerRewards.Call("CheckPointsLong", id) is long balance)
            {
                return balance;
            }

            return ServerRewards.Call("CheckPoints", id) is int points ? points : 0;
        }

        // How many RP can still be added before the balance hits Server Rewards' limit (int or long).
        private long RpRoom(ulong id)
        {
            if (!RpAvailable)
            {
                return 0;
            }

            if (ServerRewards.Call("CheckPointsLong", id) is long balance)
            {
                return long.MaxValue - Math.Max(0, balance);
            }

            long points = ServerRewards.Call("CheckPoints", id) is int value ? value : 0;
            return int.MaxValue - Math.Max(0, points);
        }

        // Economics 3.9 API: Deposit/Withdraw(string playerId, double) -> bool, Balance(string playerId) -> double.
        private bool DepositCoins(ulong id, long amount) =>
            CoinsAvailable && amount > 0 && Economics.Call("Deposit", id.ToString(), (double)amount) is bool ok && ok;

        private bool WithdrawCoins(ulong id, long amount) =>
            CoinsAvailable && amount > 0 && Economics.Call("Withdraw", id.ToString(), (double)amount) is bool ok && ok;

        private long CoinBalance(ulong id) =>
            CoinsAvailable && Economics.Call("Balance", id.ToString()) is double balance ? (long)Math.Floor(balance) : 0;

        private void DebugNoReward(PlayerData data, string reason) => SendDebug("DebugNoRewardV2", data.Name, reason);

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

        // Unlike GetTierIndex, -1 below the first title: at 0 baldness a player holds no title.
        private int GetTitleIndex(long baldness) => baldness >= config.Titles[0].MinBaldness ? GetTierIndex(baldness) : -1;

        private string TitleName(int index) => index >= 0 && index < config.Titles.Count ? config.Titles[index].Name : string.Empty;

        private void EnsureGroup(string group)
        {
            if (!permission.GroupExists(group))
            {
                permission.CreateGroup(group, group, 0);
                Puts($"Oxide group '{group}' created.");
            }
        }

        // Puts the player in their current title's group and takes them out of every other title group. Quiet on purpose.
        private void SyncTitleGroup(PlayerData data)
        {
            if (!config.SyncTitleGroups || data == null || titleGroups.Count == 0)
            {
                return;
            }

            string userId = data.Id.ToString(CultureInfo.InvariantCulture);
            string target = titleGroups.TryGetValue(GetTitleIndex(data.Baldness), out string group) ? group : null;
            foreach (string other in titleGroups.Values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!string.Equals(other, target, StringComparison.OrdinalIgnoreCase) && permission.UserHasGroup(userId, other))
                {
                    permission.RemoveUserGroup(userId, other);
                }
            }

            if (target != null)
            {
                EnsureGroup(target);
                if (!permission.UserHasGroup(userId, target))
                {
                    permission.AddUserGroup(userId, target);
                }
            }
        }

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
