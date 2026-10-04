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
    [Info("Isla de Calvos", "Igor Monasterio", "1.13.0")]
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

        // Hair revenge: victim -> killer -> time of the last time that killer killed them. In memory only.
        private readonly Dictionary<ulong, Dictionary<ulong, DateTime>> grudges = new Dictionary<ulong, Dictionary<ulong, DateTime>>();

        // Consolation kit: recent deaths (UTC) per player and players who get the kit when they wake up. In memory only.
        private readonly Dictionary<ulong, List<DateTime>> recentDeaths = new Dictionary<ulong, List<DateTime>>();
        private readonly HashSet<ulong> pendingKits = new HashSet<ulong>();

        // Barber's jobs: reset time in minutes after midnight (UTC), the valid catalog by Id and the animal prefabs. From the config.
        private int jobResetMinutes;
        private Dictionary<string, JobDefinition> jobsById = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> animalPrefabs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Minutes in a row alive, connected and moving, for the Survive jobs. Death and disconnecting reset it. In memory only.
        private readonly Dictionary<ulong, int> surviveStreaks = new Dictionary<ulong, int>();

        // Monuments on this map by their English name (displayPhrase.english), for the KillScientists jobs. Built at startup.
        private readonly Dictionary<string, List<MonumentInfo>> monumentsByName = new Dictionary<string, List<MonumentInfo>>(StringComparer.OrdinalIgnoreCase);

        // Raidable Bases (Casas de Padre Jano): only its OnRaidableBaseCompleted hook is used; the reference just tells
        // whether RaidBase jobs can be handed out.
        [PluginReference] private Plugin RaidableBases = null;

        // Hair insurance price shown to each player on the confirm page. In memory only.
        private readonly Dictionary<ulong, long> insuranceQuotes = new Dictionary<ulong, long>();

        private class WoundRecord
        {
            public BasePlayer Attacker;
            public bool Headshot;
        }

        #endregion

        #region Configuration

        // Bump when a release must overwrite values already saved in existing config files.
        private const int CurrentConfigVersion = 1130;

        // The Caballero de la Tonsura prize since 1.11.0 (Rust has no "minicopter" item to hand out).
        private const string AttackHelicopterPrefab = "assets/content/vehicles/attackhelicopter/attackhelicopter.entity.prefab";

        // The Greñas Sucias prize message. The 1.13.0 migration only adds its translations where the config still has it.
        private const string GreasyMopPrizeMessage = "Toma este trozo de hueso afilado. Empieza a raparte solito.";

        private static Dictionary<string, string> GreasyMopPrizeMessages() => new Dictionary<string, string>
        {
            ["en"] = "Have this sharpened bit of bone. Start shaving yourself like a big boy.",
            ["ru"] = "Держи заточенную косточку. Начинай бриться сам, ты уже большой."
        };

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
                    Coins = 2500,
                    Items = new List<PrizeItem> { new PrizeItem { Shortname = "knife.bone", Amount = 1 } },
                    Message = GreasyMopPrizeMessage,
                    Messages = GreasyMopPrizeMessages()
                },
                ["1000"] = new TierPrize { Rp = 100, Coins = 10000 },
                ["10000"] = new TierPrize { Rp = 500, Coins = 50000 },
                ["100000"] = new TierPrize
                {
                    Rp = 2500,
                    Items = new List<PrizeItem> { new PrizeItem { Shortname = "explosive.timed", Amount = 4 } }
                },
                ["1000000"] = new TierPrize
                {
                    Rp = 15000,
                    SpawnPrefabs = new List<string> { AttackHelicopterPrefab }
                },
                ["10000000"] = new TierPrize
                {
                    Rp = 50000,
                    Items = new List<PrizeItem>
                    {
                        new PrizeItem { Shortname = "metal.facemask", Amount = 1 },
                        new PrizeItem { Shortname = "metal.plate.torso", Amount = 1 }
                    }
                },
                ["100000000"] = new TierPrize { Rp = 250000 }
            };

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

            [JsonProperty("Hair revenge (kill your killer back)")]
            public RevengeConfig Revenge = new RevengeConfig();

            [JsonProperty("Barber's jobs (daily quests)")]
            public JobsConfig Jobs = new JobsConfig();

            [JsonProperty("Hair insurance (duct tape for Puntos de Chola at the barber)")]
            public InsuranceConfig Insurance = new InsuranceConfig();

            [JsonProperty("Consolation kit (after dying several times in a row)")]
            public ConsolationKitConfig ConsolationKit = new ConsolationKitConfig();
        }

        // If A kills B and B kills A back within the window, B's kill pays this many times the normal kill reward.
        private class RevengeConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Window (minutes)")] public int WindowMinutes = 30;
            [JsonProperty("Alopecia multiplier")] public int Multiplier = 2;
        }

        // The duct tape relic sold by the barber: price = max(minimum, alopecia / 1000 x surcharge) Puntos de Chola, rounded
        // up. At 1.5 it is 50 % more than what a death (-10 %) takes, counting 100 alopecia = 1 Punto de Chola.
        private class InsuranceConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Minimum price (Puntos de Chola)")] public long MinPrice = 100;
            [JsonProperty("Surcharge (price = alopecia / 1000 x this)")] public double Surcharge = 1.5;
        }

        // Dying this many times within the window (suicides do not count) gives the items on the next respawn, silently.
        private class ConsolationKitConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Deaths")] public int Deaths = 3;
            [JsonProperty("Within (minutes)")] public int WindowMinutes = 15;
            [JsonProperty("At most one kit every (minutes)")] public int CooldownMinutes = 60;

            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<PrizeItem> Items = new List<PrizeItem>
            {
                new PrizeItem { Shortname = "bandage", Amount = 5 },
                new PrizeItem { Shortname = "pistol.revolver", Amount = 1 },
                new PrizeItem { Shortname = "ammo.pistol", Amount = 24 }
            };
        }

        // Daily quests: every player gets "Jobs per day" jobs picked at random from the catalog, renewed at the reset time
        // (UTC). They are seen in /calvos and at the barber, and paid (Puntos de Chola) by the barber.
        private class JobsConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Jobs per day")] public int PerDay = 3;
            [JsonProperty("New jobs every day at (UTC, HH:mm)")] public string ResetTimeUtc = "04:00";

            // What counts for the KillAnimals jobs (ShortPrefabName, like NpcTiers).
            [JsonProperty("Animal prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> AnimalPrefabs = new List<string>
            {
                "chicken", "snake.entity", "boar", "stag", "wolf", "wolf2", "panther", "tiger", "bear", "polarbear", "crocodile", "simpleshark"
            };

            [JsonProperty("Catalog", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<JobDefinition> Catalog = DefaultJobs();
        }

        // Type: KillScientists, BreakBarrels, KillAnimals, RaidBase, Gather or Survive. Amount is minutes for Survive and
        // bases for RaidBase. The task itself ("Break 20 barrels") comes from the lang of each player; "Text" is the barber's
        // remark after it, in Spanish, and "Text in other languages" has it per language code (en, ru...).
        private class JobDefinition
        {
            [JsonProperty("Id")] public string Id = string.Empty;
            [JsonProperty("Type")] public string Type = string.Empty;
            [JsonProperty("Amount")] public long Amount = 1;
            [JsonProperty("Reward (Puntos de Chola)")] public long Reward;

            // KillScientists only: the monument's English name on the map (displayPhrase.english), e.g. "Train Yard".
            [JsonProperty("Monument", NullValueHandling = NullValueHandling.Ignore)] public string Monument;

            // Gather only: item shortname (wood, stones, metal.ore, sulfur.ore...).
            [JsonProperty("Resource", NullValueHandling = NullValueHandling.Ignore)] public string Resource;

            // RaidBase only: 0 easy, 1 medium, 2 hard, 3 expert, 4 nightmare. Raidable Bases 3.x has no difficulties.
            [JsonProperty("Minimum difficulty", NullValueHandling = NullValueHandling.Ignore)] public int? MinDifficulty;

            [JsonProperty("Text")] public string Text = string.Empty;

            [JsonProperty("Text in other languages", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Texts = new Dictionary<string, string>();

            [JsonIgnore] public JobType ParsedType;
        }

        private enum JobType
        {
            KillScientists,
            BreakBarrels,
            KillAnimals,
            RaidBase,
            Gather,
            Survive
        }

        // About 15 jobs for an x5 server (gathering and loot x5): easy 250, medium 750, hard 2.000 Puntos de Chola.
        private static List<JobDefinition> DefaultJobs()
        {
            JobDefinition Job(string id, JobType type, long amount, long reward, string es, string en, string ru) => new JobDefinition
            {
                Id = id, Type = type.ToString(), Amount = amount, Reward = reward, Text = es,
                Texts = new Dictionary<string, string> { ["en"] = en, ["ru"] = ru }
            };

            JobDefinition AtMonument(JobDefinition job, string monument) { job.Monument = monument; return job; }
            JobDefinition Of(JobDefinition job, string resource) { job.Resource = resource; return job; }
            JobDefinition Level(JobDefinition job, int difficulty) { job.MinDifficulty = difficulty; return job; }

            return new List<JobDefinition>
            {
                Job("barrels20", JobType.BreakBarrels, 20, 250,
                    "No preguntes para qué los quiero.", "Don't ask what I want them for.", "Не спрашивай, зачем они мне."),
                Job("barrels60", JobType.BreakBarrels, 60, 750,
                    "Estoy montando una batería. De barriles.", "I'm starting a band. A barrel band.", "Собираю ударную установку. Из бочек."),
                Job("animals5", JobType.KillAnimals, 5, 250,
                    "Con su pelo me hago una peluca para el enemigo.", "Their fur makes a lovely wig for the enemy.", "Из их шерсти сошью парик врагу."),
                Job("animals15", JobType.KillAnimals, 15, 750,
                    "Si tiene pelo y se mueve, es competencia.", "If it's hairy and it moves, it's competition.", "Если оно волосатое и шевелится, это конкурент."),
                Job("scientists5", JobType.KillScientists, 5, 250,
                    "Llevan casco para que no se les vea la coronilla.", "They wear helmets so you can't see the bald spot.", "Каски носят, чтобы макушку не видели."),
                Job("scientists15", JobType.KillScientists, 15, 750,
                    "Bata blanca y flequillo: imperdonable.", "Lab coat and a fringe: unforgivable.", "Белый халат и чёлка. Непростительно."),
                AtMonument(Job("trainyard5", JobType.KillScientists, 5, 750,
                    "Allí hay uno con coleta. Tráeme la coleta.", "There's one with a ponytail. Bring me the ponytail.", "Там один с хвостиком. Принеси хвостик."), "Train Yard"),
                AtMonument(Job("tunnel10", JobType.KillScientists, 10, 2000,
                    "Ahí abajo no llega la luz, pero tu frente sí.", "No light gets down there. Your forehead will do.", "Там темно, но твой лоб посветит."), "Military Tunnel"),
                AtMonument(Job("launch10", JobType.KillScientists, 10, 2000,
                    "Que despeguen ellos, que tú ya brillas.", "Let them take off. You already shine.", "Пусть они взлетают, ты и так сияешь."), "Launch Site"),
                Of(Job("wood10k", JobType.Gather, 10000, 250,
                    "Leña para el horno de las pelucas.", "Firewood for the wig furnace.", "Дрова для печи, где жгут парики."), "wood"),
                Of(Job("stones10k", JobType.Gather, 10000, 250,
                    "Para pulir calvas. A mano.", "For polishing bald heads. By hand.", "Для полировки лысин. Вручную."), "stones"),
                Of(Job("metal10k", JobType.Gather, 10000, 750,
                    "Maquinillas nuevas, que las viejas ya no cortan.", "New clippers. The old ones have gone blunt.", "На новые машинки, старые уже не стригут."), "metal.ore"),
                Of(Job("sulfur10k", JobType.Gather, 10000, 2000,
                    "Para una permanente que no se te olvide.", "For a perm you'll never forget.", "Для химзавивки, которую ты не забудешь."), "sulfur.ore"),
                Job("survive60", JobType.Survive, 60, 250,
                    "Sin palmar. Ya sé que es pedirte mucho.", "Without dying. I know it's a big ask.", "Не помирая. Знаю, это непросто."),
                Job("survive180", JobType.Survive, 180, 750,
                    "Tres horas sin que te salga pelo. A ver.", "Three hours without growing any hair. Let's see.", "Три часа без новых волос. Посмотрим."),
                Job("raid1", JobType.RaidBase, 1, 750,
                    "Padre Jano no se va a enfadar. Mucho.", "Father Jano won't mind. Much.", "Отец Яно не обидится. Почти."),
                Level(Job("raidhard1", JobType.RaidBase, 1, 2000,
                    "De las gordas, que de las fáciles ya sé que eres capaz. Más o menos.", "A big one. I know you can do the easy ones. More or less.", "Из серьёзных. С лёгкими ты вроде справляешься. Вроде."), 2)
            };
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

            // Entities spawned in front of the player (full prefab paths), e.g. a vehicle. Only for an online player.
            [JsonProperty("Spawn prefabs", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> SpawnPrefabs = new List<string>();

            // Said to the player along with the prize, as written (no lang key: each prize has its own). In Spanish; the
            // other languages go in "Message in other languages" by language code (en, ru...), and without one, this.
            [JsonProperty("Message")] public string Message = string.Empty;

            [JsonProperty("Message in other languages", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, string> Messages = new Dictionary<string, string>();

            [JsonIgnore]
            public bool IsEmpty => Rp <= 0 && Coins <= 0 && (Items == null || Items.All(i => i == null || i.Amount <= 0))
                && (SpawnPrefabs == null || SpawnPrefabs.All(string.IsNullOrEmpty)) && string.IsNullOrEmpty(Message);
        }

        private class PrizeItem
        {
            [JsonProperty("Item shortname")] public string Shortname = string.Empty;
            [JsonProperty("Amount")] public int Amount = 1;
        }

        // Selling baldness is cheap and buying it is expensive on purpose: baldness pays RP every 30 min forever.
        // Since 1.11.0 the rates are per 1000 (selling for coins) and per 10 (buying) baldness, to fit the x10 scale.
        private class ExchangeConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Sell: baldness for 1 RP")] public long SellBaldnessPerRp = 1000;
            [JsonProperty("Sell: coins per 1000 baldness")] public long SellCoinsPer1000 = 25;
            [JsonProperty("Buy: RP per 10 baldness")] public long BuyRpPer10 = 1;
            [JsonProperty("Buy: coins per 10 baldness")] public long BuyCoinsPer10 = 25;
            [JsonProperty("Minimum baldness to sell")] public long MinSell = 1000;

            [JsonProperty("Amounts offered (baldness)", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<long> Amounts = new List<long> { 100, 1000, 10000, 100000 };

            // Pre-1.11.0 keys: read only to migrate them (null = not in the file), never written back.
            [JsonProperty("Sell: coins per 100 baldness", NullValueHandling = NullValueHandling.Ignore)] public long? OldSellCoinsPer100;
            [JsonProperty("Buy: RP per 1 baldness", NullValueHandling = NullValueHandling.Ignore)] public long? OldBuyRpPerBaldness;
            [JsonProperty("Buy: coins per 1 baldness", NullValueHandling = NullValueHandling.Ignore)] public long? OldBuyCoinsPerBaldness;
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
            [JsonProperty("Baldness on win")] public long WinAmount = 5000;
            [JsonProperty("Baldness lost on fail")] public long LoseAmount = 5000;
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
            [JsonProperty("Baldness per delivered tag")] public long Reward = 1000;
            [JsonProperty("Bonus for completing all colors")] public long CollectionBonus = 100000;
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
            public TrophyConfig DogTag = new TrophyConfig { Shortname = "dogtagneutral", Reward = 2000, DropChance = 0.5f, MinTier = 8, MaxTier = 12 };

            [JsonProperty("Blue dog tags")]
            public TrophyConfig BlueDogTags = new TrophyConfig { Shortname = "bluedogtags", Reward = 5000, DropChance = 0.3f, MinTier = 13, MaxTier = 17 };

            [JsonProperty("Red dog tags (heli/Bradley/CH47, every paid player; tiers ignored)")]
            public TrophyConfig RedDogTags = new TrophyConfig { Shortname = "reddogtags", Reward = 15000, DropChance = 1f, MinTier = 18, MaxTier = 20 };

            [JsonProperty("Gems")]
            public TrophyConfig Gems = new TrophyConfig { Shortname = "kickgems", Reward = 50000, DropChance = 0.01f, MinTier = 12, MaxTier = 20 };

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

        // Geometric curve from 10 (tier 1) to 10000 (tier 20), about x1.44 per tier: the 1.0 curve (1 to 1000) times 10,
        // as on the live server since 2026-10-01.
        private static Dictionary<string, long> DefaultTierRewards()
        {
            long[] rewards =
            {
                10, 20, 30, 40, 50, 60, 90, 130, 180, 260,
                380, 550, 780, 1130, 1620, 2340, 3360, 4830, 6950, 10000
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

        // 1.11.0 puts the exchange on the x10 baldness scale: every baldness amount of the old rates is worth x10.
        // The renamed rates keep their number with the new unit (25 coins per 100 -> 25 per 1000, 1 RP per 1 -> 1 per 10);
        // the two that keep their key are multiplied by 10. Only when the old keys are in the file (a pre-1.11.0 exchange).
        private void MigrateExchangeTo1110(ExchangeConfig ex)
        {
            if (!ex.OldSellCoinsPer100.HasValue && !ex.OldBuyRpPerBaldness.HasValue && !ex.OldBuyCoinsPerBaldness.HasValue)
            {
                return;
            }

            if (ex.OldSellCoinsPer100.HasValue) ex.SellCoinsPer1000 = ex.OldSellCoinsPer100.Value;
            if (ex.OldBuyRpPerBaldness.HasValue) ex.BuyRpPer10 = ex.OldBuyRpPerBaldness.Value;
            if (ex.OldBuyCoinsPerBaldness.HasValue) ex.BuyCoinsPer10 = ex.OldBuyCoinsPerBaldness.Value;
            ex.SellBaldnessPerRp = SaturatingMultiply(ex.SellBaldnessPerRp, 10);
            ex.MinSell = SaturatingMultiply(ex.MinSell, 10);
            PrintWarning($"Config updated to 1.11.0: baldness exchange on the x10 scale (sell {ex.SellBaldnessPerRp} for 1 RP, "
                + $"{ex.SellCoinsPer1000} coins per 1000; buy {ex.BuyRpPer10} RP or {ex.BuyCoinsPer10} coins per 10; minimum {ex.MinSell}).");
        }

        // "minicopter" is not an item in Rust, so a prize with it gave nothing: 1.11.0 swaps it for a spawned attack helicopter.
        private void MigrateMinicopterPrizes()
        {
            if (config.TierPrizes == null) return;
            foreach (KeyValuePair<string, TierPrize> entry in config.TierPrizes)
            {
                TierPrize prize = entry.Value;
                if (prize?.Items == null || prize.Items.RemoveAll(i => i != null && string.Equals(i.Shortname, "minicopter", StringComparison.OrdinalIgnoreCase)) == 0)
                {
                    continue;
                }

                if (prize.SpawnPrefabs == null) prize.SpawnPrefabs = new List<string>();
                if (prize.SpawnPrefabs.Count == 0) prize.SpawnPrefabs.Add(AttackHelicopterPrefab);
                PrintWarning($"Config updated to 1.11.0: tier prize '{entry.Key}' spawns an attack helicopter instead of the 'minicopter' item, which does not exist.");
            }
        }

        // 1.13.0 only adds things: the four new sections come with their defaults by themselves (missing keys keep the
        // class defaults), and the Greñas Sucias prize message gets its English and Russian versions if it is still the
        // default one and has none. Nothing already in the file changes.
        private void MigrateTo1130()
        {
            TierPrize first;
            if (config.TierPrizes != null && config.TierPrizes.TryGetValue("1", out first) && first != null
                && first.Message == GreasyMopPrizeMessage && (first.Messages == null || first.Messages.Count == 0))
            {
                first.Messages = GreasyMopPrizeMessages();
            }

            PrintWarning("Config updated to 1.13.0: hair revenge, barber's jobs, hair insurance and consolation kit (new sections with their defaults).");
        }

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

            if (config.Exchange == null) config.Exchange = new ExchangeConfig();
            if (config.ConfigVersion < 1110)
            {
                MigrateExchangeTo1110(config.Exchange);
                MigrateMinicopterPrizes();
            }

            // Old exchange keys never go back to the file, whatever the version says.
            config.Exchange.OldSellCoinsPer100 = null;
            config.Exchange.OldBuyRpPerBaldness = null;
            config.Exchange.OldBuyCoinsPerBaldness = null;

            if (config.ConfigVersion < 1130)
            {
                MigrateTo1130();
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
            ex.SellCoinsPer1000 = Math.Max(0, ex.SellCoinsPer1000);
            ex.BuyRpPer10 = Math.Max(1, ex.BuyRpPer10);
            ex.BuyCoinsPer10 = Math.Max(1, ex.BuyCoinsPer10);
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

            if (config.Revenge == null) config.Revenge = new RevengeConfig();
            config.Revenge.WindowMinutes = Math.Max(1, config.Revenge.WindowMinutes);
            config.Revenge.Multiplier = Math.Max(1, config.Revenge.Multiplier);

            if (config.Insurance == null) config.Insurance = new InsuranceConfig();
            config.Insurance.MinPrice = Math.Max(1, config.Insurance.MinPrice);
            if (double.IsNaN(config.Insurance.Surcharge) || config.Insurance.Surcharge < 0) config.Insurance.Surcharge = 0;

            if (config.ConsolationKit == null) config.ConsolationKit = new ConsolationKitConfig();
            ConsolationKitConfig kit = config.ConsolationKit;
            kit.Deaths = Math.Max(1, kit.Deaths);
            kit.WindowMinutes = Math.Max(1, kit.WindowMinutes);
            kit.CooldownMinutes = Math.Max(0, kit.CooldownMinutes);
            if (kit.Items == null) kit.Items = new List<PrizeItem>();
            kit.Items.RemoveAll(i => i == null || string.IsNullOrEmpty(i.Shortname) || i.Amount <= 0);

            ValidateJobs();
        }

        // Builds the job lookups and drops (with a warning) every catalog entry that cannot work.
        private void ValidateJobs()
        {
            if (config.Jobs == null) config.Jobs = new JobsConfig();
            JobsConfig jobs = config.Jobs;
            jobs.PerDay = Math.Max(0, jobs.PerDay);
            if (!TryParseClock(jobs.ResetTimeUtc, out jobResetMinutes))
            {
                string fallback = new JobsConfig().ResetTimeUtc;
                PrintWarning($"Barber's jobs: '{jobs.ResetTimeUtc}' is not a time (HH:mm); using {fallback}.");
                jobs.ResetTimeUtc = fallback;
                TryParseClock(fallback, out jobResetMinutes);
            }

            if (jobs.AnimalPrefabs == null) jobs.AnimalPrefabs = new List<string>();
            animalPrefabs = new HashSet<string>(jobs.AnimalPrefabs.Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);

            if (jobs.Catalog == null) jobs.Catalog = new List<JobDefinition>();
            jobsById = new Dictionary<string, JobDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (JobDefinition job in jobs.Catalog)
            {
                if (job == null) continue;
                if (job.Texts == null) job.Texts = new Dictionary<string, string>();
                string problem = null;
                if (string.IsNullOrEmpty(job.Id)) problem = "it has no Id";
                else if (jobsById.ContainsKey(job.Id)) problem = "its Id is repeated";
                else if (!Enum.TryParse(job.Type, true, out job.ParsedType) || !Enum.IsDefined(typeof(JobType), job.ParsedType)) problem = $"'{job.Type}' is not a job type";
                else if (job.Amount <= 0) problem = "its amount is not above 0";
                else if (job.ParsedType == JobType.Gather && string.IsNullOrEmpty(job.Resource)) problem = "a Gather job needs a Resource";

                if (problem != null)
                {
                    PrintWarning($"Barber's jobs: job '{job.Id}' ignored ({problem}).");
                    continue;
                }

                job.Reward = Math.Max(0, job.Reward);
                jobsById[job.Id] = job;
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

            // Who put Puntos de Chola on each head and since when, only for isla.cabezas (1.12.0). Bounties placed before
            // 1.12.0 have no entry; it goes away with the bounty.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<ulong, BountyRecord> BountyInfo = new Dictionary<ulong, BountyRecord>();

            public CalvoDelDiaData CalvoDelDia = new CalvoDelDiaData();
        }

        private class BountyRecord
        {
            // Server time of the first Puntos de Chola on that head.
            public DateTime Since;

            // Everyone who has put Puntos de Chola on it, in order, once each.
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> PlacedBy = new List<ulong>();
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

        private class JobsData
        {
            // UTC date (yyyy-MM-dd) of the job day these jobs belong to; a job day starts at the reset time.
            public string Day = string.Empty;

            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<JobProgress> List = new List<JobProgress>();
        }

        private class JobProgress
        {
            public string Id = string.Empty;
            public long Progress;
            public bool Done;
            public bool Claimed;
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

            // Barber's jobs of the current job day (1.13.0).
            public JobsData Jobs = new JobsData();

            // Last consolation kit (UTC), for "at most one kit every X minutes". Never = default.
            public DateTime LastConsolationKit;

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
                if (entry.Value.Jobs == null) entry.Value.Jobs = new JobsData();
                if (entry.Value.Jobs.Day == null) entry.Value.Jobs.Day = string.Empty;
                if (entry.Value.Jobs.List == null) entry.Value.Jobs.List = new List<JobProgress>();
                entry.Value.Jobs.List.RemoveAll(j => j == null || string.IsNullOrEmpty(j.Id));
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

        // Spanish is the console's language (and the JSON's, and the hooks'): text with no player to read it comes out in it.
        private const string ConsoleLanguage = "es";

        // Each language its own texts (1.13.0; before, Spanish was registered as "en" too). Oxide gives every player the
        // language of their game client and, when that file is missing, falls back to "en", not to "es": Spanish clients
        // arrive as "es-ES", so Spanish goes in "es" and in "es-ES". English and Russian are real translations.
        protected override void LoadDefaultMessages()
        {
            Dictionary<string, string> spanish = SpanishMessages();
            lang.RegisterMessages(EnglishMessages(), this);
            lang.RegisterMessages(spanish, this, "es");
            lang.RegisterMessages(spanish, this, "es-ES");
            lang.RegisterMessages(RussianMessages(), this, "ru");
        }

        private static Dictionary<string, string> SpanishMessages()
        {
            return new Dictionary<string, string>
            {
                ["LanguageCode"] = "es",
                ["CalvarioTitle"] = "EL CALVARIO",
                ["BarberName"] = "EL BARBERO",
                ["BarberOptExchangeV2"] = "Vengo a vender (o comprar) alopecia",
                ["BarberExIntroV4"] = "Aquí la alopecia se compra y se vende. Vender sale barato y comprar sale caro: esto es un negocio, no una ONG.\nTienes {0} de alopecia · {1} Puntos de Chola · {2} pelones.",
                ["BarberExSellRpV4"] = "Vender alopecia por Puntos de Chola (cada {0} de alopecia, 1 Punto de Chola)",
                ["BarberExSellCoinsV5"] = "Vender alopecia por pelones (cada 1.000 de alopecia, {0} pelones)",
                ["BarberExBuyRpV6"] = "Comprar alopecia con Puntos de Chola ({0} PdC cada 10 de alopecia)",
                ["BarberExBuyCoinsV5"] = "Comprar alopecia con pelones ({0} pelones cada 10 de alopecia)",
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
                ["PrizeSpawned"] = "<color=#e0a526>Tu premio está aparcado delante de ti.</color> Mira al frente, frente soberana.",
                ["PrizeSpawnFailed"] = "<color=#e0662f>Lo que te tocaba aparcar delante no cabe aquí.</color> Avisa a un admin y que te lo dé a mano.",
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
                ["HudCounterV3"] = "<size=11><color=#9a9288>ALOPECIA</color></size>  <color=#e0a526>{0}</color>\n<size=10><color=#d8d8d8>{1}</color></size>",
                ["UnitRpFew"] = "{0} Puntos de Chola",
                ["UnitCoinsFew"] = "{0} pelones",
                ["Title_1"] = "Greñas Sucias",
                ["Title_1000"] = "Pelambrera Lamentable",
                ["Title_10000"] = "Entradas Incipientes",
                ["Title_100000"] = "Coronilla a la Intemperie",
                ["Title_1000000"] = "Caballero de la Tonsura",
                ["Title_10000000"] = "Lord Bola de Billar",
                ["Title_100000000"] = "Su Calvísima Majestad",
                ["RevengeChat"] = "<color=#e0a526>VENGANZA CAPILAR</color>: {0} le ha devuelto la visita a {1}. Alopecia x{2}, y con intereses.",
                ["RevengeTag"] = " [venganza x{0}]",
                ["ConsolationKit"] = "<color=#e0a526>La Seguridad Social Capilar se apiada de ti:</color> tienes un kit de consuelo en el inventario. No te acostumbres.",
                ["DebugConsolationKit"] = "[debug] {0}: kit de consuelo",
                ["DurationHoursMinutes"] = "{0} h {1} min",
                ["CalvarioTabJobs"] = "ENCARGOS",
                ["BarberOptJobs"] = "¿Tienes algún encargo?",
                ["BarberJobsIntro"] = "Los encargos de hoy. Se renuevan dentro de {0}: lo que no cobres antes, se pierde. Y propina, ni lo sueñes.",
                ["BarberJobsNone"] = "Hoy no tengo nada para ti. Vuelve dentro de {0}, a ver si para entonces me acuerdo de tu cara.",
                ["BarberJobClaim"] = "COBRAR: {0} (+{1})",
                ["BarberJobClaimed"] = "{0}  [cobrado]",
                ["BarberJobProgress"] = "{0}  ({1}/{2})  ·  {3}",
                ["BarberJobPaid"] = "Toma, {0}. Y ni una palabra a nadie.",
                ["JobDone"] = "<color=#e0a526>Encargo del Barbero cumplido:</color> {0}. Pásate por la peluquería (<color=#e0a526>/peluqueria</color>) a cobrar {1}.",
                ["JobsMenuHint"] = "Se cobran hablando con el Barbero (<color=#e0a526>/peluqueria</color>). Encargos nuevos dentro de {0}; lo que no cobres antes, se pierde.",
                ["JobsMenuEmpty"] = "Hoy el Barbero no tiene encargos. Disfruta del paro.",
                ["JobsMenuReward"] = "+{0}",
                ["JobsMenuProgress"] = "{0} / {1}",
                ["JobsMenuReady"] = "HECHO: cóbralo en el Barbero",
                ["JobsMenuClaimed"] = "COBRADO",
                ["JobKillScientists"] = "Mata {0} científicos",
                ["JobKillScientistsOne"] = "Mata a un científico",
                ["JobKillScientistsAt"] = "Mata {0} científicos en el {1}",
                ["JobKillScientistsAtOne"] = "Mata a un científico en el {1}",
                ["JobBreakBarrels"] = "Rompe {0} barriles",
                ["JobBreakBarrelsOne"] = "Rompe un barril",
                ["JobKillAnimals"] = "Mata {0} animales",
                ["JobKillAnimalsOne"] = "Mata un animal",
                ["JobRaidBase"] = "Revienta {0} Casas de Padre Jano",
                ["JobRaidBaseOne"] = "Revienta una Casa de Padre Jano",
                ["JobRaidBaseLevel"] = "Revienta {0} Casas de Padre Jano de dificultad {1} o más",
                ["JobRaidBaseLevelOne"] = "Revienta una Casa de Padre Jano de dificultad {1} o más",
                ["RaidDifficulty1"] = "media",
                ["RaidDifficulty2"] = "difícil",
                ["RaidDifficulty3"] = "experta",
                ["RaidDifficulty4"] = "pesadilla",
                ["JobGather"] = "Recolecta {0} de {1}",
                ["Resource_wood"] = "madera",
                ["Resource_stones"] = "piedra",
                ["Resource_metal.ore"] = "mineral de metal",
                ["Resource_sulfur.ore"] = "mineral de azufre",
                ["JobSurvive"] = "Sobrevive {0} minutos seguidos moviéndote",
                ["JobSurviveOne"] = "Sobrevive un minuto moviéndote",
                ["DebugJobDone"] = "[debug] {0}: encargo '{1}' cumplido",
                ["DebugJobPaid"] = "[debug] {0}: encargo '{1}' cobrado ({2})",
                ["BarberOptInsurance"] = "Quiero el seguro capilar ({0})",
                ["BarberInsuranceOffer"] = "Seguro capilar: {0} y tu próxima muerte no te cuesta pelo. Letra pequeña: ninguna.",
                ["BarberInsuranceBuy"] = "TRATO HECHO",
                ["BarberInsuranceDone"] = "Pagado: {0}. Cinta americana puesta: tu próxima muerte no restará. Póliza a todo riesgo, menos el ridículo.",
                ["BarberInsuranceRepriced"] = "Desde que te lo dije has ganado alopecia, así que el seguro ha subido. Mira el precio nuevo.",
                ["DebugInsurance"] = "[debug] {0}: seguro capilar ({1})"
            };
        }

        // English (1.13.0): the same jokes, told in English. Names from Padre Jano's glossary (docs/TONO.md, "Otros idiomas").
        private static Dictionary<string, string> EnglishMessages()
        {
            return new Dictionary<string, string>
            {
                ["LanguageCode"] = "en",
                ["CalvarioTitle"] = "EL CALVARIO",
                ["BarberName"] = "THE BARBER",
                ["BarberOptExchangeV2"] = "I'm here to sell (or buy) alopecia",
                ["BarberExIntroV4"] = "Alopecia is bought and sold here. Selling is cheap and buying is dear: this is a business, not a charity.\nYou have {0} alopecia · {1} Noggin Points · {2} baldies.",
                ["BarberExSellRpV4"] = "Sell alopecia for Noggin Points (1 Noggin Point per {0} alopecia)",
                ["BarberExSellCoinsV5"] = "Sell alopecia for baldies ({0} baldies per 1,000 alopecia)",
                ["BarberExBuyRpV6"] = "Buy alopecia with Noggin Points ({0} NP per 10 alopecia)",
                ["BarberExBuyCoinsV5"] = "Buy alopecia with baldies ({0} baldies per 10 alopecia)",
                ["BarberExClosedV3"] = "{0}  [closed: {1} is missing. Come back when the boss fixes it]",
                ["BarberExPickAmount"] = "How much? Think it through, or you'll be crying about it later.",
                ["BarberExSellLineV2"] = "Give {0} alopecia and take {1}",
                ["BarberExBuyLineV2"] = "Pay {1} and take {0} alopecia",
                ["BarberExTooMuchV3"] = "{0}  [can't afford it, skint]",
                ["BarberExConfirmSellV2"] = "Sure you're swapping {0} alopecia for {1}? Your hair will grow back, and no money pays for that.",
                ["BarberExConfirmBuyV2"] = "Sure you're paying {1} for {0} alopecia? No refunds, no complaints book.",
                ["BarberExConfirm"] = "CONFIRM",
                ["BarberExCancel"] = "LET ME THINK",
                ["BarberExDoneSellV2"] = "Done: -{0} alopecia and +{1}. I can see fuzz sprouting already, traitor.",
                ["BarberExDoneBuyV2"] = "Done: +{0} alopecia and -{1}. You're shining like a freshly waxed billiard ball.",
                ["BarberExNotEnough"] = "Can't afford it. Not even a back-alley shave.",
                ["BarberExFailed"] = "Something broke and nothing was touched. Try again, the clipper has its days.",
                ["UnitRpV2"] = "{0} Noggin Points",
                ["UnitRpOneV2"] = "{0} Noggin Point",
                ["UnitCoinsV2"] = "{0} baldies",
                ["UnitCoinsOneV2"] = "{0} baldy",
                ["TitleUpBanner"] = "{0} is now {1}! Sunglasses on, it's blinding.",
                ["TierPrizeV2"] = "<color=#e0a526>Prize for reaching {0}:</color> {1}. On the house, since you can't even afford a comb.",
                ["TierPrizeItems"] = "<color=#e0a526>Prize for reaching {0}:</color> it's in your inventory already (or at your feet, if it didn't fit). On the house.",
                ["PrizeSpawned"] = "<color=#e0a526>Your prize is parked right in front of you.</color> Eyes front, chrome dome.",
                ["PrizeSpawnFailed"] = "<color=#e0662f>What should be parked in front of you doesn't fit here.</color> Ask an admin to hand it over.",
                ["TitleDropBanner"] = "{0} drops to {1}. You can't see their scalp any more.",
                ["DebugTierPrize"] = "[debug] {0}: {1} prize · {2}",
                ["ReasonExchange"] = "exchange at El Calvario",
                ["BarberGreetingV2_1"] = "Sit down, hairy. What am I taking off today, the hair or the dignity?",
                ["BarberGreetingV2_2"] = "Come in, come in. That mane won't pull itself out, and I charge by the minute.",
                ["BarberGreetingV2_3"] = "You again. More forehead and less shame every day. That's the spirit.",
                ["BarberOptItemsV3"] = "I've brought a relic",
                ["BarberOptCatalog"] = "Show me the catalogue",
                ["BarberOptCarne"] = "I'm here to stamp my Bald Card",
                ["BarberOptBye"] = "Nothing, just looking",
                ["BarberOptBack"] = "Back",
                ["BarberOptStamp"] = "Stamp what I've brought",
                ["BarberItemsIntroV3"] = "Let's see what relics you've got in those pockets. A sandwich doesn't count.",
                ["BarberItemsNoneV3"] = "You're not carrying a single relic. Come back when you've killed something; that's how you move up.",
                ["BarberItemLine"] = "{0} (you have {1}): {2}",
                ["BarberTrophyUsedV2"] = "{0}: +{1}. Up on the trophy wall, next to the last hero's toupee.",
                ["BarberCarneIntroV2"] = "Show me the card. {0} of {1} colours stamped. Cards completed: {2}. The Ministry is in no hurry, but I am.",
                ["BarberCarneStamped"] = "Stamped: {0}",
                ["BarberCarneMissingV2"] = "Still missing: {0}. Get killing, they don't stamp themselves.",
                ["TagColorBlue"] = "blue",
                ["TagColorGray"] = "grey",
                ["TagColorGreen"] = "green",
                ["TagColorLavender"] = "lavender",
                ["TagColorMint"] = "mint",
                ["TagColorOrange"] = "orange",
                ["TagColorPink"] = "pink",
                ["TagColorPurple"] = "purple",
                ["TagColorRed"] = "red",
                ["TagColorWhite"] = "white",
                ["TagColorYellow"] = "yellow",
                ["CalvarioSubtitleV2"] = "Voluntary alopecia clinic  ·  Walk in with hair, walk out with dignity",
                ["CalvarioYouV3"] = "Your alopecia: <color=#e0a526>{0}</color>  —  {1}",
                ["CalvarioNextV3"] = "To <color=#e0a526>{0}</color>: {1} to go. Keep killing, it won't shave itself.",
                ["CalvarioTop"] = "Peak baldness reached. Nothing left to pull out.",
                ["CalvarioTabRankingV2"] = "RANKING",
                ["CalvarioTabHall"] = "HALL OF FAME",
                ["CalvarioTabBounties"] = "BOUNTIES",
                ["CalvarioClose"] = "X",
                ["CalvarioShieldOn"] = "Tape on. Your bald head survives the next death.",
                ["CalvarioBatteryOn"] = "Clipper buzzing: {0} min left.",
                ["CalvarioProverb1"] = "God made few perfect heads. The rest he covered with hair.",
                ["CalvarioProverb2"] = "Hair is temporary. Bald is forever.",
                ["CalvarioProverb3"] = "Better the baldy you know than the hairy you don't.",
                ["CalvarioProverb4"] = "A head that shines is a head that rules.",
                ["CalvarioProverb5"] = "It's not a bald spot. It's a solar panel.",
                ["CalvarioProverb7"] = "Baldness isn't lost: it's conquered.",
                ["CalvarioProverb8"] = "Anti-hair-loss shampoo is hairy propaganda.",
                ["CalvarioCarneHintV2"] = "One tag of each colour: +{0} per stamp and +{1} for completing the card. Swap your doubles in the playground.",
                ["CalvarioRankingEmptyV2"] = "Nobody in the ranking yet. The island is full of hair.",
                ["CalvarioRankingLine"] = "{0}.  {1}",
                ["CalvarioRankingYouV3"] = "Your place: <color=#e0a526>#{0}</color> of {1}. You need <color=#e0a526>{2}</color> to overtake {3}. Come on, that one even has eyebrows.",
                ["CalvarioRankingFirstV2"] = "You're the shiniest head on the island. Everyone else uses you as a mirror to comb their hair.",
                ["CalvarioPrev"] = "< PREVIOUS",
                ["CalvarioNextPage"] = "NEXT >",
                ["CalvarioPage"] = "Page {0}/{1}",
                ["CalvarioItemBleach"] = "Bleach",
                ["CalvarioItemDuctTape"] = "Duct tape",
                ["CalvarioItemBattery"] = "Small battery",
                ["CalvarioItemDogTag"] = "Dog tag",
                ["CalvarioItemBlueDogTags"] = "Blue dog tags",
                ["CalvarioItemRedDogTags"] = "Red dog tags",
                ["CalvarioItemGems"] = "Gems",
                ["CalvarioItemIdTag"] = "ID tag",
                ["CalvarioDescBleachV2"] = "House shampoo and the only relic that can go wrong. {0}%: it scorches your scalp (+{1}). If not, a rebel lock of hair (-{2}).",
                ["CalvarioDescDuctTape"] = "A patch for the bald spot: if you die, your hair won't even notice. Once.",
                ["CalvarioDescBattery"] = "For the clipper: x{0} on everything you earn for {1} min. Bzzzz.",
                ["CalvarioDescDogTag"] = "Souvenir from a scientist with a fringe. +{0}.",
                ["CalvarioDescBlueDogTags"] = "Torn off a long-haired heavy. +{0}.",
                ["CalvarioDescRedDogTags"] = "From the pilot who lost his quiff along with the helicopter. +{0}.",
                ["CalvarioDescGems"] = "Crown jewels of His Baldest Majesty. Shiny as your head. +{0}.",
                ["CatalogSection"] = "RELICS",
                ["CatalogIntro"] = "Every relic in the house. What's greyed out you're not carrying: looking is free.",
                ["CatalogBack"] = "BACK TO THE BARBER",
                ["CatalogHave"] = "Carrying: {0}",
                ["CatalogUse"] = "USE",
                ["CatalogNone"] = "NOT CARRYING",
                ["CatalogWisdom"] = "BALD WISDOM",
                ["CatalogCarneTitle"] = "BALD CARD  ·  Island Ministry of Alopecia   ({0}/{1})   ·   Cards completed: {2}",
                ["CatalogCarneStamped"] = "STAMPED",
                ["CatalogCarneMissing"] = "MISSING",
                ["CatalogCarneDeliver"] = "STAMP",
                ["CatalogCarneNone"] = "NOTHING TO STAMP",
                ["ItemFoundV4"] = "You found: <color=#e0a526>{0}</color>. Take it to El Calvario at the barbershop (<color=#e0a526>/peluqueria</color>); in your pocket it does nothing.",
                ["CalvarioGoToBarberV3"] = "Relics are used at El Calvario, in the barbershop. Go with <color=#e0a526>/peluqueria</color> and talk to the Barber.",
                ["ItemNoneV2"] = "You're not carrying {0}. Not even that.",
                ["ItemShieldAlready"] = "Your bald head is already taped up. Die first.",
                ["ItemBatteryAlreadyV4"] = "The clipper is already running ({0} min left). It won't go any faster: it's a clipper, not a Formula 1 car.",
                ["ItemBleachWinV2"] = "The bleach scorched your scalp: +{0}. It stings, but it shines.",
                ["ItemBleachFail"] = "The bleach left you a rebel lock of hair. Shame: -{0}.",
                ["ItemShieldOnV2"] = "Bald head taped up with duct tape. Your next death won't cost you. Not elegant, but it works.",
                ["ItemShieldUsed"] = "The duct tape protected your bald head: this death costs nothing.",
                ["ItemBatteryOnV2"] = "Clipper running: x{0} for {1} min. Bzzzz.",
                ["ItemBatteryOffV2"] = "The clipper's battery is dead. Back to shaving by hand, like peasants.",
                ["CarneNothingV2"] = "You've no tag of a colour you're missing. Give the doubles to your cousin.",
                ["CarneDeliveredV2"] = "You handed in {0} tag(s): +{1}. The clerk didn't even look up.",
                ["CarneCompletedV3"] = "<color=#e0a526>{0}</color> has completed the BALD CARD and wins +{1}. The Ministry of Alopecia is proud. Their mum, not so much.",
                ["ReasonItemUseV2"] = "relic: {0}",
                ["ReasonCarne"] = "bald card",
                ["SupremeBaldnessV6"] = "<color=#e0a526>{0} HAS REACHED SUPREME BALDNESS</color>. No more worrying about shampoo.",
                ["TitleUpV3"] = "<color=#e0a526>{0}</color> rises to <color=#e0a526>{1}</color>. Their barber has already gone on the dole.",
                ["TitleDropV2"] = "<color=#e0662f>{0} is growing hair</color> (now {1})",
                ["NoPermissionV4"] = "You don't have permission to use this command. Nice try, champ.",
                ["AdminUsageV5"] = "Usage: /calvoadmin set <player> <value> | /calvoadmin reset <player> | /calvoadmin debug on|off | /calvoadmin evento <hora|champu|peludo|alopecia|parar> | /calvoadmin salon guardar|cerrar [forzar]|borrar <n> | /calvoadmin cabeza quitar <player> | /calvoadmin calvodeldia ahora",
                ["AdminInvalidValue"] = "The value must be a whole number, {0} or more.",
                ["PlayerNotFound"] = "No player found matching '{0}'.",
                ["PlayerAmbiguous"] = "{0} players match '{1}'. Be more specific or use the SteamID.",
                ["AdminSetV2"] = "{0}'s alopecia set to {1}.",
                ["AdminResetV2"] = "{0}'s alopecia reset to {1}.",
                ["DebugOnV2"] = "Debug on: you'll see every alopecia change and its reason in chat.",
                ["DebugOff"] = "Debug off.",
                ["DebugChange"] = "[debug] {0}: {1} → {2} ({3}{4}) · {5}",
                ["DebugNoRewardV2"] = "[debug] {0}: no alopecia · {1}",
                ["DebugRpV2"] = "[debug] {0}: +{1} NP ({2})",
                ["DebugNoRpV2"] = "[debug] {0}: no NP · {1}",
                ["NoRpAfk"] = "hasn't moved (AFK)",
                ["NoRpPlugin"] = "Server Rewards isn't loaded",
                ["NoRpRefused"] = "Server Rewards refused the payment",
                ["NoRpCapV2"] = "Noggin Points balance at Server Rewards' limit",
                ["RpEarnedV5"] = "<color=#e0a526>+{0} Noggin Points</color> for wearing the scalp of a <color=#e0a526>{1}</color>. Pocket it and hush.",
                ["ReasonPlayerKill"] = "kill on {0}",
                ["ReasonPlayerHeadshotKillV2"] = "headshot buzzcut on {0}",
                ["ReasonDeath"] = "death (-{0}%)",
                ["ReasonHeadshotDeath"] = "headshot death (-{0}%)",
                ["ReasonSurvival"] = "survival",
                ["ReasonNpcKill"] = "NPC {0} (T{1})",
                ["ReasonEventParticipant"] = "event {0} (T{1}), damaged it",
                ["ReasonEventTeammate"] = "event {0} (T{1}), teammate nearby",
                ["ReasonAdmin"] = "admin",
                ["NoRewardSleeperV2"] = "victim asleep or offline ({0}). You were very brave. Congratulations.",
                ["NoRewardCooldown"] = "cooldown on {0}",
                ["NoRewardNpcDisabled"] = "NPC {0} disabled in the config",
                ["NoRewardNpcUnlisted"] = "NPC {0} is not in NpcTiers",
                ["NoRewardTierMissing"] = "NPC {0} (T{1}) has no value in TierRewards",
                ["NoRewardNpcDeath"] = "death by NPC (disabled in the config)",
                ["EventNameBaldHourV3"] = "Bald Hour",
                ["EventNameShampooRainV3"] = "Shampoo Rain",
                ["EventNameHairiestHuntV3"] = "Hairy Hunt",
                ["EventNameBladeStormV3"] = "Alopecia Outbreak",
                ["EventTag"] = " [{0} x{1}]",
                ["EventBaldHourStartV4"] = "<color=#e0a526>BALD HOUR</color>: for {0} min everything gives x{1} alopecia. Get out there and kill; that forehead won't clear itself.",
                ["EventBaldHourEndV2"] = "Bald Hour is over. Back to going bald at the usual rate.",
                ["EventShampooRainStartV2"] = "<color=#e0662f>SHAMPOO RAIN</color>: for {0} min dying costs x{1}. In this weather hair grows like weeds.",
                ["EventShampooRainEndV2"] = "It's stopped raining. You can go and die in peace now.",
                ["EventHuntStartV4"] = "<color=#e0a526>HAIRY HUNT</color>: {0} is the hairiest on the island ({1}). Whoever kills them wins +{2}. If they last {3} min, they win +{4}. Go get them; that mane won't cut itself.",
                ["EventHuntKilledV4"] = "{0} has hunted down the hairiest, {1}, and wins +{2}. Thank you for this great service to the community.",
                ["EventHuntSurvivedV3"] = "{0} survived the hunt with every hair intact and wins +{1}. Muppets.",
                ["EventHuntDiedV4"] = "{0}, the hairiest on the island, has snuffed it all by themselves, without anyone firing a shot. Died as they lived: hair in their face and nobody giving a damn. The hunt is over.",
                ["EventHuntEscapedV4"] = "{0} has legged it off the island with their mane, like a rat with hair extensions. They'll be back when the conditioner runs out. The hunt is over.",
                ["EventBladeStormStartV4"] = "<color=#e0a526>ALOPECIA OUTBREAK</color>: for {0} min the heli, the Bradley and the Chinook pay x{1}. Clippers out.",
                ["EventBladeStormEndV2"] = "The alopecia outbreak is over. The heli pays the usual again, like a civil servant.",
                ["EventStoppedByAdminV2"] = "An admin has cancelled the {0} event. Complaints to their barber.",
                ["ReasonHuntKillV2"] = "hairy hunt ({0})",
                ["ReasonHuntSurvived"] = "survived the hunt",
                ["AdminEventUsageV3"] = "Usage: /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminEventBusy"] = "There's already an event running: {0}. Stop it first with /calvoadmin evento parar.",
                ["AdminEventCannotStart"] = "{0} can't start now (too few players online, or disabled in the config?).",
                ["AdminEventNone"] = "No event is running.",
                ["BatteryTag"] = " [battery x{0}]",
                ["HallEmpty"] = "No map has been closed yet. The first one goes in with the next wipe: start polishing that forehead.",
                ["HallMapClosed"] = "Map closed on {0}",
                ["HallNumber"] = "#{0}",
                ["HallPodiumPlace"] = "{0}.  {1}",
                ["HallTopKiller"] = "Most kills: <color=#e0a526>{0}</color> ({1})",
                ["HallTopDeaths"] = "Most deaths: <color=#e0662f>{0}</color> ({1}). With all that dying, they could grow braids.",
                ["HallTopDeathsPlain"] = "Most deaths: <color=#e0662f>{0}</color> ({1})",
                ["HallSnapshot"] = "Map snapshot of {0}",
                ["HallWipeWinner"] = "<color=#e0a526>{0}</color> takes the map with <color=#e0a526>{1}</color> alopecia (the podium is in the Hall of Fame of <color=#e0a526>/calvos</color>). All hail the forehead.",
                ["AdminHallUsageV2"] = "Usage: /calvoadmin salon guardar | /calvoadmin salon cerrar [forzar] | /calvoadmin salon borrar <n>",
                ["AdminHallCloseUsage"] = "Usage: isla.salon cerrar [forzar]",
                ["AdminHallClosed"] = "Map closed: entry #{0} saved in the Hall of Fame. Map kills and deaths back to 0.",
                ["AdminHallClosedEmpty"] = "Map closed with no Hall of Fame entry (nobody killed or died on this map, or the hall is disabled). Map kills and deaths back to 0.",
                ["AdminHallClosedReset"] = "Everyone's alopecia back to 0 (the config has the wipe reset on).",
                ["AdminHallCloseRecent"] = "Nothing closed: the map was already closed on {0} at {1}, less than {2} hours ago. To close it again, add 'forzar'.",
                ["AdminHallSaved"] = "Entry #{0} saved in the Hall of Fame.",
                ["AdminHallNothing"] = "Nothing saved: nobody has alopecia or kills on this map.",
                ["AdminHallDeleted"] = "Entry #{0} ({1}) deleted from the Hall of Fame.",
                ["AdminHallNotFound"] = "There's no entry #{0} in the Hall of Fame.",
                ["BountyUsage"] = "Usage: <color=#e0a526>/cabeza <player> <amount></color> (minimum {0}). What you put up is not refunded.",
                ["BountyTooLow"] = "The minimum is {0}. With less you won't even trim their sideburns.",
                ["BountySelf"] = "You can't put a price on your own scalp, however much hair you've got to spare.",
                ["BountyNoBalanceV2"] = "You can't afford it: you have {0}. A lot of grudge for so little balance.",
                ["BountyNotFound"] = "Nobody on the island is called '{0}'. To hate someone, learn their name first.",
                ["BountyAmbiguous"] = "{0} players have '{1}' in their name. Type more, this isn't a raffle.",
                ["BountyClosed"] = "Bounties are closed.",
                ["BountyFailed"] = "Something went wrong with the Noggin Points and nothing was put up. Try again.",
                ["BountyPlaced"] = "<color=#e0a526>{0}</color> has put <color=#e0a526>{1}</color> on the scalp of <color=#e0662f>{2}</color>. Whoever kills them takes it. Wanted, dead or bald.",
                ["BountyRaisedV2"] = "<color=#e0a526>{0}</color> adds <color=#e0a526>{1}</color> to the scalp of <color=#e0662f>{2}</color>: the pot is now <color=#e0a526>{3}</color>. Shares are up.",
                ["BountyClaimed"] = "<color=#e0a526>{0}</color> claims the scalp of <color=#e0662f>{1}</color> and pockets <color=#e0a526>{2}</color>. Shaved and paid.",
                ["BountyListEmpty"] = "No scalp has a price. Either there's peace on the island, or nobody has a single Noggin Point.",
                ["BountyPriceShort"] = "{0} NP",
                ["BountyMenuHint"] = "Put a price on someone with <color=#e0a526>/cabeza <player> <amount></color>: whoever kills them in PvP takes it. What you put up is not refunded.",
                ["BountyMenuYou"] = "There's <color=#e0662f>{0}</color> on your scalp. With that bald head you can be seen from the other end of the island.",
                ["DebugBountyNoClaim"] = "[debug] {0}: scalp of {1} not claimed · {2}",
                ["NoBountyTeam"] = "same team",
                ["NoBountySleeper"] = "victim asleep or offline",
                ["AdminBountyUsage"] = "Usage: /calvoadmin cabeza quitar <player>",
                ["AdminBountyRemoved"] = "Bounty on {0}'s scalp cancelled ({1}).",
                ["AdminBountyNone"] = "Nobody has put a price on {0}'s scalp.",
                ["CalvoDelDiaName"] = "Baldy of the Day",
                ["CalvoDelDiaChatV3"] = "<color=#e0a526>{0}</color> is the <color=#e0a526>BALDY OF THE DAY</color>: +{1} alopecia since the last pick. Until tomorrow, it's 'Your Baldness' to you.",
                ["CalvoDelDiaBannerV2"] = "{0} is the BALDY OF THE DAY! Autographs signed on the scalp.",
                ["CalvoDelDiaMenu"] = "Baldy of the Day: <color=#e0a526>{0}</color> (+{1})",
                ["CalvoDelDiaPrize"] = "<color=#e0a526>Baldy of the Day prize:</color> {0}. Don't let it go to your head; there's nothing left up there.",
                ["CalvoDelDiaPrizeItems"] = "<color=#e0a526>Baldy of the Day prize:</color> it's in your inventory already (or at your feet, if it didn't fit).",
                ["AdminCalvoDelDiaUsage"] = "Usage: /calvoadmin calvodeldia ahora",
                ["AdminCalvoDelDiaOff"] = "Baldy of the Day is disabled in the config.",
                ["AdminCalvoDelDiaNone"] = "Nobody has gained alopecia since the last pick: no Baldy of the Day.",
                ["HudWallet"] = "<color=#9a9288>NP</color> <color=#e0a526>{0}</color>     <color=#9a9288>BALDIES</color> <color=#e0a526>{1}</color>",
                ["HudCounterV3"] = "<size=11><color=#9a9288>ALOPECIA</color></size>  <color=#e0a526>{0}</color>\n<size=10><color=#d8d8d8>{1}</color></size>",
                ["UnitRpFew"] = "{0} Noggin Points",
                ["UnitCoinsFew"] = "{0} baldies",
                ["Title_1"] = "Greasy Mop",
                ["Title_1000"] = "Pathetic Mane",
                ["Title_10000"] = "Receding Hairline",
                ["Title_100000"] = "Exposed Crown",
                ["Title_1000000"] = "Knight of the Tonsure",
                ["Title_10000000"] = "Lord Billiard Ball",
                ["Title_100000000"] = "His Baldest Majesty",
                ["RevengeChat"] = "<color=#e0a526>FOLLICLE REVENGE</color>: {0} has paid {1} a return visit. Alopecia x{2}, with interest.",
                ["RevengeTag"] = " [revenge x{0}]",
                ["ConsolationKit"] = "<color=#e0a526>The National Hair Service takes pity on you:</color> there's a consolation kit in your inventory. Don't get used to it.",
                ["DebugConsolationKit"] = "[debug] {0}: consolation kit",
                ["DurationHoursMinutes"] = "{0}h {1}m",
                ["CalvarioTabJobs"] = "JOBS",
                ["BarberOptJobs"] = "Got any jobs for me?",
                ["BarberJobsIntro"] = "Today's jobs. New ones in {0}: whatever you haven't collected by then is gone. And forget about a tip.",
                ["BarberJobsNone"] = "Nothing for you today. Come back in {0}; maybe I'll remember your face by then.",
                ["BarberJobClaim"] = "COLLECT: {0} (+{1})",
                ["BarberJobClaimed"] = "{0}  [collected]",
                ["BarberJobProgress"] = "{0}  ({1}/{2})  ·  {3}",
                ["BarberJobPaid"] = "Here, {0}. And not a word to anyone.",
                ["JobDone"] = "<color=#e0a526>Barber's job done:</color> {0}. Drop by the barbershop (<color=#e0a526>/peluqueria</color>) to collect {1}.",
                ["JobsMenuHint"] = "Collected by talking to the Barber (<color=#e0a526>/peluqueria</color>). New jobs in {0}; whatever you haven't collected by then is gone.",
                ["JobsMenuEmpty"] = "The Barber has no jobs today. Enjoy the dole.",
                ["JobsMenuReward"] = "+{0}",
                ["JobsMenuProgress"] = "{0} / {1}",
                ["JobsMenuReady"] = "DONE: collect it from the Barber",
                ["JobsMenuClaimed"] = "COLLECTED",
                ["JobKillScientists"] = "Kill {0} scientists",
                ["JobKillScientistsOne"] = "Kill a scientist",
                ["JobKillScientistsAt"] = "Kill {0} scientists at {1}",
                ["JobKillScientistsAtOne"] = "Kill a scientist at {1}",
                ["JobBreakBarrels"] = "Break {0} barrels",
                ["JobBreakBarrelsOne"] = "Break a barrel",
                ["JobKillAnimals"] = "Kill {0} animals",
                ["JobKillAnimalsOne"] = "Kill an animal",
                ["JobRaidBase"] = "Raid {0} of Father Jano's Houses",
                ["JobRaidBaseOne"] = "Raid one of Father Jano's Houses",
                ["JobRaidBaseLevel"] = "Raid {0} of Father Jano's Houses (difficulty {1} or above)",
                ["JobRaidBaseLevelOne"] = "Raid one of Father Jano's Houses (difficulty {1} or above)",
                ["RaidDifficulty1"] = "medium",
                ["RaidDifficulty2"] = "hard",
                ["RaidDifficulty3"] = "expert",
                ["RaidDifficulty4"] = "nightmare",
                ["JobGather"] = "Gather {0} {1}",
                ["Resource_wood"] = "wood",
                ["Resource_stones"] = "stone",
                ["Resource_metal.ore"] = "metal ore",
                ["Resource_sulfur.ore"] = "sulfur ore",
                ["JobSurvive"] = "Survive {0} minutes in a row on the move",
                ["JobSurviveOne"] = "Survive one minute on the move",
                ["DebugJobDone"] = "[debug] {0}: job '{1}' done",
                ["DebugJobPaid"] = "[debug] {0}: job '{1}' collected ({2})",
                ["BarberOptInsurance"] = "I want scalp insurance ({0})",
                ["BarberInsuranceOffer"] = "Scalp insurance: {0} and your next death won't cost you a single hair. Small print: none.",
                ["BarberInsuranceBuy"] = "DEAL",
                ["BarberInsuranceDone"] = "Paid: {0}. Duct tape on: your next death won't cost you. Fully comprehensive, except for embarrassment.",
                ["BarberInsuranceRepriced"] = "You've gained alopecia since I quoted you, so the insurance has gone up. Here's the new price.",
                ["DebugInsurance"] = "[debug] {0}: scalp insurance ({1})"
            };
        }

        // Russian (1.13.0): the same jokes, told in Russian. Names from Padre Jano's glossary (docs/TONO.md, "Otros idiomas").
        // Counts go as "Бочки: 20" or with abbreviations (ОЧ, мин), so the noun never has to agree with the number, and the
        // player is never told something in a gendered past tense.
        private static Dictionary<string, string> RussianMessages()
        {
            return new Dictionary<string, string>
            {
                ["LanguageCode"] = "ru",
                ["CalvarioTitle"] = "ЭЛЬ КАЛЬВАРИО",
                ["BarberName"] = "БАРБЕР",
                ["BarberOptExchangeV2"] = "Хочу продать (или купить) алопецию",
                ["BarberExIntroV4"] = "Здесь алопецию покупают и продают. Продать дёшево, купить дорого: это бизнес, а не благотворительность.\nАлопеция: {0} · Очки Черепушки: {1} · Лысики: {2}.",
                ["BarberExSellRpV4"] = "Продать алопецию за Очки Черепушки (1 ОЧ за каждые {0} алопеции)",
                ["BarberExSellCoinsV5"] = "Продать алопецию за лысики (за каждые 1 000 алопеции лысиков: {0})",
                ["BarberExBuyRpV6"] = "Купить алопецию за Очки Черепушки ({0} ОЧ за каждые 10 алопеции)",
                ["BarberExBuyCoinsV5"] = "Купить алопецию за лысики (за каждые 10 алопеции лысиков: {0})",
                ["BarberExClosedV3"] = "{0}  [закрыто: нет {1}. Приходи, когда шеф починит]",
                ["BarberExPickAmount"] = "Сколько? Подумай хорошенько, а то потом будешь плакать.",
                ["BarberExSellLineV2"] = "Отдать {0} алопеции и получить {1}",
                ["BarberExBuyLineV2"] = "Заплатить {1} и получить {0} алопеции",
                ["BarberExTooMuchV3"] = "{0}  [не по карману, бедолага]",
                ["BarberExConfirmSellV2"] = "Точно меняешь {0} алопеции на {1}? Волосы же снова полезут, а это никакими деньгами не окупить.",
                ["BarberExConfirmBuyV2"] = "Точно платишь {1} за {0} алопеции? Возврата нет, жалобной книги тоже.",
                ["BarberExConfirm"] = "ПОДТВЕРДИТЬ",
                ["BarberExCancel"] = "ПОДУМАЮ",
                ["BarberExDoneSellV2"] = "Готово: -{0} алопеции и +{1}. Уже пушок пробивается, предатель.",
                ["BarberExDoneBuyV2"] = "Готово: +{0} алопеции и -{1}. Сияешь, как свеженатёртый бильярдный шар.",
                ["BarberExNotEnough"] = "Не хватает. Даже на бритьё в подворотне.",
                ["BarberExFailed"] = "Что-то сломалось, ничего не тронуто. Попробуй ещё раз, у машинки бывают плохие дни.",
                ["UnitRpV2"] = "{0} Очков Черепушки",
                ["UnitRpOneV2"] = "{0} Очко Черепушки",
                ["UnitCoinsV2"] = "{0} лысиков",
                ["UnitCoinsOneV2"] = "{0} лысик",
                ["TitleUpBanner"] = "{0} теперь {1}! Надевайте тёмные очки, слепит.",
                ["TierPrizeV2"] = "<color=#e0a526>Награда за звание «{0}»:</color> {1}. За счёт заведения, у тебя ж даже на расчёску нет.",
                ["TierPrizeItems"] = "<color=#e0a526>Награда за звание «{0}»:</color> уже в инвентаре (или под ногами, если не влезло). За счёт заведения.",
                ["PrizeSpawned"] = "<color=#e0a526>Твоя награда припаркована прямо перед тобой.</color> Смотри вперёд, лобастый.",
                ["PrizeSpawnFailed"] = "<color=#e0662f>То, что должно стоять перед тобой, здесь не помещается.</color> Попроси админа выдать вручную.",
                ["TitleDropBanner"] = "{0} опускается до звания «{1}». Лысину уже не видно.",
                ["DebugTierPrize"] = "[debug] {0}: награда за «{1}» · {2}",
                ["ReasonExchange"] = "обмен в Эль Кальварио",
                ["BarberGreetingV2_1"] = "Садись, волосатый. Что сегодня снимаем: волосы или достоинство?",
                ["BarberGreetingV2_2"] = "Заходи, заходи. Эта грива сама себя не выдернет, а у меня поминутная оплата.",
                ["BarberGreetingV2_3"] = "Опять ты. С каждым днём лба всё больше, а стыда всё меньше. Так держать.",
                ["BarberOptItemsV3"] = "Принёс реликвию",
                ["BarberOptCatalog"] = "Покажи каталог",
                ["BarberOptCarne"] = "Хочу проштамповать Удостоверение Лысого",
                ["BarberOptBye"] = "Ничего, просто смотрю",
                ["BarberOptBack"] = "Назад",
                ["BarberOptStamp"] = "Проштампуй, что есть",
                ["BarberItemsIntroV3"] = "Ну-ка, что за реликвии у тебя в карманах. Бутерброд не считается.",
                ["BarberItemsNoneV3"] = "У тебя ни одной реликвии. Возвращайся, когда кого-нибудь убьёшь, по-другому тут не растут.",
                ["BarberItemLine"] = "{0} (у тебя {1}): {2}",
                ["BarberTrophyUsedV2"] = "{0}: +{1}. На стену трофеев, рядом с паричком последнего смельчака.",
                ["BarberCarneIntroV2"] = "Показывай удостоверение. Цветов со штампом: {0} из {1}. Заполненных удостоверений: {2}. Министерство не спешит, а я спешу.",
                ["BarberCarneStamped"] = "Со штампом: {0}",
                ["BarberCarneMissingV2"] = "Не хватает: {0}. Иди убивай, сами они не проштампуются.",
                ["TagColorBlue"] = "синий",
                ["TagColorGray"] = "серый",
                ["TagColorGreen"] = "зелёный",
                ["TagColorLavender"] = "лавандовый",
                ["TagColorMint"] = "мятный",
                ["TagColorOrange"] = "оранжевый",
                ["TagColorPink"] = "розовый",
                ["TagColorPurple"] = "фиолетовый",
                ["TagColorRed"] = "красный",
                ["TagColorWhite"] = "белый",
                ["TagColorYellow"] = "жёлтый",
                ["CalvarioSubtitleV2"] = "Клиника добровольной алопеции  ·  Входишь с волосами, выходишь с достоинством",
                ["CalvarioYouV3"] = "Твоя алопеция: <color=#e0a526>{0}</color>  —  {1}",
                ["CalvarioNextV3"] = "До звания «<color=#e0a526>{0}</color>» осталось {1}. Убивай дальше, само не облысеет.",
                ["CalvarioTop"] = "Капиллярная вершина покорена. Выдёргивать больше нечего.",
                ["CalvarioTabRankingV2"] = "РЕЙТИНГ",
                ["CalvarioTabHall"] = "ЗАЛ СЛАВЫ",
                ["CalvarioTabBounties"] = "ГОЛОВЫ",
                ["CalvarioClose"] = "X",
                ["CalvarioShieldOn"] = "Скотч наклеен. Твоя лысина переживёт следующую смерть.",
                ["CalvarioBatteryOn"] = "Машинка жужжит: осталось {0} мин.",
                ["CalvarioProverb1"] = "Бог создал мало идеальных голов. Остальные он прикрыл волосами.",
                ["CalvarioProverb2"] = "Волосы временны. Лысина вечна.",
                ["CalvarioProverb3"] = "Лучше знакомый лысый, чем незнакомый волосатый.",
                ["CalvarioProverb4"] = "Голова блестит, голова рулит.",
                ["CalvarioProverb5"] = "Это не лысина. Это солнечная батарея.",
                ["CalvarioProverb7"] = "Лысину не теряют, её завоёвывают.",
                ["CalvarioProverb8"] = "Шампунь от выпадения волос — волосатая пропаганда.",
                ["CalvarioCarneHintV2"] = "По одной карточке каждого цвета: +{0} за штамп и +{1} за полное удостоверение. Дубли меняй во дворе.",
                ["CalvarioRankingEmptyV2"] = "В рейтинге пока никого. Остров зарос волосами.",
                ["CalvarioRankingLine"] = "{0}.  {1}",
                ["CalvarioRankingYouV3"] = "Твоё место: <color=#e0a526>{0}</color> из {1}. Чтобы обогнать {3}, не хватает <color=#e0a526>{2}</color>. Давай, у него даже брови есть.",
                ["CalvarioRankingFirstV2"] = "Ты самая блестящая голова острова. Остальные причёсываются, глядя в тебя, как в зеркало.",
                ["CalvarioPrev"] = "< НАЗАД",
                ["CalvarioNextPage"] = "ДАЛЬШЕ >",
                ["CalvarioPage"] = "Страница {0}/{1}",
                ["CalvarioItemBleach"] = "Отбеливатель",
                ["CalvarioItemDuctTape"] = "Армированный скотч",
                ["CalvarioItemBattery"] = "Маленькая батарейка",
                ["CalvarioItemDogTag"] = "Жетон",
                ["CalvarioItemBlueDogTags"] = "Синие жетоны",
                ["CalvarioItemRedDogTags"] = "Красные жетоны",
                ["CalvarioItemGems"] = "Самоцветы",
                ["CalvarioItemIdTag"] = "ID-карточка",
                ["CalvarioDescBleachV2"] = "Фирменный шампунь и единственная реликвия, которая может подвести. {0} %: обжигает кожу головы (+{1}). Иначе непокорная прядь (-{2}).",
                ["CalvarioDescDuctTape"] = "Заплатка на лысину: если умрёшь, волосы даже не заметят. Один раз.",
                ["CalvarioDescBattery"] = "Для машинки: x{0} ко всему, что заработаешь, на {1} мин. Ззззз.",
                ["CalvarioDescDogTag"] = "Сувенир от учёного с чёлкой. +{0}.",
                ["CalvarioDescBlueDogTags"] = "Сорваны с хеви с патлами до плеч. +{0}.",
                ["CalvarioDescRedDogTags"] = "От пилота, который вместе с вертолётом лишился и чуба. +{0}.",
                ["CalvarioDescGems"] = "Драгоценности короны Его Лысейшего Величества. Блестят, как твоя голова. +{0}.",
                ["CatalogSection"] = "РЕЛИКВИИ",
                ["CatalogIntro"] = "Все реликвии заведения. Серое у тебя с собой нет: смотреть бесплатно.",
                ["CatalogBack"] = "НАЗАД К БАРБЕРУ",
                ["CatalogHave"] = "С собой: {0}",
                ["CatalogUse"] = "ИСПОЛЬЗОВАТЬ",
                ["CatalogNone"] = "НЕТ С СОБОЙ",
                ["CatalogWisdom"] = "ЛЫСАЯ МУДРОСТЬ",
                ["CatalogCarneTitle"] = "УДОСТОВЕРЕНИЕ ЛЫСОГО  ·  Министерство Алопеции Острова   ({0}/{1})   ·   Заполнено: {2}",
                ["CatalogCarneStamped"] = "ЕСТЬ",
                ["CatalogCarneMissing"] = "НЕТ",
                ["CatalogCarneDeliver"] = "ШТАМП",
                ["CatalogCarneNone"] = "НЕЧЕГО ШТАМПОВАТЬ",
                ["ItemFoundV4"] = "Находка: <color=#e0a526>{0}</color>. Отнеси в Эль Кальварио в парикмахерской (<color=#e0a526>/peluqueria</color>), в кармане от неё толку нет.",
                ["CalvarioGoToBarberV3"] = "Реликвии используют в Эль Кальварио, в парикмахерской. Иди через <color=#e0a526>/peluqueria</color> и поговори с Барбером.",
                ["ItemNoneV2"] = "С собой нет: {0}. Даже этого.",
                ["ItemShieldAlready"] = "Твоя лысина уже заклеена скотчем. Сначала умри.",
                ["ItemBatteryAlreadyV4"] = "Машинка уже работает (осталось {0} мин). Быстрее не поедет: это машинка, а не болид Формулы-1.",
                ["ItemBleachWinV2"] = "Отбеливатель обжёг тебе кожу головы: +{0}. Щиплет, зато блестит.",
                ["ItemBleachFail"] = "Отбеливатель оставил тебе непокорную прядь. Позор: -{0}.",
                ["ItemShieldOnV2"] = "Лысина заклеена армированным скотчем. Следующая смерть ничего не отнимет. Не элегантно, зато работает.",
                ["ItemShieldUsed"] = "Армированный скотч защитил твою лысину: эта смерть ничего не отнимает.",
                ["ItemBatteryOnV2"] = "Машинка включена: x{0} на {1} мин. Ззззз.",
                ["ItemBatteryOffV2"] = "У машинки села батарейка. Снова бреешься вручную, как беднота.",
                ["CarneNothingV2"] = "У тебя нет карточек тех цветов, которых не хватает. Дубли отдай двоюродному брату.",
                ["CarneDeliveredV2"] = "Сдано карточек: {0}, +{1}. Чиновник даже глаз не поднял.",
                ["CarneCompletedV3"] = "<color=#e0a526>{0}</color> получает +{1} за полностью заполненное УДОСТОВЕРЕНИЕ ЛЫСОГО. Министерство Алопеции гордится. Мама не очень.",
                ["ReasonItemUseV2"] = "реликвия: {0}",
                ["ReasonCarne"] = "удостоверение лысого",
                ["SupremeBaldnessV6"] = "<color=#e0a526>{0}: ВЫСШАЯ ЛЫСИНА ДОСТИГНУТА</color>. Про шампунь можно забыть.",
                ["TitleUpV3"] = "<color=#e0a526>{0}</color> теперь <color=#e0a526>{1}</color>. Личный парикмахер уже ушёл на пособие.",
                ["TitleDropV2"] = "<color=#e0662f>У {0} растут волосы</color> (теперь {1})",
                ["NoPermissionV4"] = "У тебя нет прав на эту команду. Хорошая попытка, умник.",
                ["AdminUsageV5"] = "Использование: /calvoadmin set <игрок> <значение> | /calvoadmin reset <игрок> | /calvoadmin debug on|off | /calvoadmin evento <hora|champu|peludo|alopecia|parar> | /calvoadmin salon guardar|cerrar [forzar]|borrar <n> | /calvoadmin cabeza quitar <игрок> | /calvoadmin calvodeldia ahora",
                ["AdminInvalidValue"] = "Значение должно быть целым числом не меньше {0}.",
                ["PlayerNotFound"] = "Игрок «{0}» не найден.",
                ["PlayerAmbiguous"] = "Под «{1}» подходят несколько игроков ({0}). Уточни или используй SteamID.",
                ["AdminSetV2"] = "Алопеция игрока {0}: {1}.",
                ["AdminResetV2"] = "Алопеция игрока {0} сброшена до {1}.",
                ["DebugOnV2"] = "Отладка включена: в чате будет каждое изменение алопеции и его причина.",
                ["DebugOff"] = "Отладка выключена.",
                ["DebugChange"] = "[debug] {0}: {1} → {2} ({3}{4}) · {5}",
                ["DebugNoRewardV2"] = "[debug] {0}: без алопеции · {1}",
                ["DebugRpV2"] = "[debug] {0}: +{1} ОЧ ({2})",
                ["DebugNoRpV2"] = "[debug] {0}: без ОЧ · {1}",
                ["NoRpAfk"] = "без движения (AFK)",
                ["NoRpPlugin"] = "Server Rewards не загружен",
                ["NoRpRefused"] = "Server Rewards отклонил выплату",
                ["NoRpCapV2"] = "баланс Очков Черепушки упёрся в предел Server Rewards",
                ["RpEarnedV5"] = "<color=#e0a526>Очки Черепушки: +{0}</color> за лысину уровня «<color=#e0a526>{1}</color>». Бери и молчи.",
                ["ReasonPlayerKill"] = "убийство: {0}",
                ["ReasonPlayerHeadshotKillV2"] = "под ноль хедшотом: {0}",
                ["ReasonDeath"] = "смерть (-{0} %)",
                ["ReasonHeadshotDeath"] = "смерть от хедшота (-{0} %)",
                ["ReasonSurvival"] = "выживание",
                ["ReasonNpcKill"] = "NPC {0} (T{1})",
                ["ReasonEventParticipant"] = "событие {0} (T{1}), урон нанесён",
                ["ReasonEventTeammate"] = "событие {0} (T{1}), союзник рядом",
                ["ReasonAdmin"] = "админ",
                ["NoRewardSleeperV2"] = "жертва спала или была офлайн ({0}). Очень смело, поздравляем.",
                ["NoRewardCooldown"] = "кулдаун на {0}",
                ["NoRewardNpcDisabled"] = "NPC {0} отключён в конфиге",
                ["NoRewardNpcUnlisted"] = "NPC {0} нет в NpcTiers",
                ["NoRewardTierMissing"] = "у NPC {0} (T{1}) нет значения в TierRewards",
                ["NoRewardNpcDeath"] = "смерть от NPC (отключено в конфиге)",
                ["EventNameBaldHourV3"] = "Час Лысины",
                ["EventNameShampooRainV3"] = "Шампуневый дождь",
                ["EventNameHairiestHuntV3"] = "Охота на волосатого",
                ["EventNameBladeStormV3"] = "Вспышка алопеции",
                ["EventTag"] = " [{0} x{1}]",
                ["EventBaldHourStartV4"] = "<color=#e0a526>ЧАС ЛЫСИНЫ</color>: {0} мин всё даёт x{1} алопеции. Идите убивать, лоб сам себя не расчистит.",
                ["EventBaldHourEndV2"] = "Час Лысины закончился. Лысеем снова по обычному тарифу.",
                ["EventShampooRainStartV2"] = "<color=#e0662f>ШАМПУНЕВЫЙ ДОЖДЬ</color>: {0} мин смерть стоит x{1}. В такую погоду волосы прут как на дрожжах.",
                ["EventShampooRainEndV2"] = "Дождь кончился. Можно спокойно помирать.",
                ["EventHuntStartV4"] = "<color=#e0a526>ОХОТА НА ВОЛОСАТОГО</color>: {0} — самый волосатый на острове ({1}). Кто его убьёт, получит +{2}. Если продержится {3} мин, +{4} получит он сам. Вперёд, эта грива сама себя не подстрижёт.",
                ["EventHuntKilledV4"] = "{0} снимает скальп с самого волосатого, {1}, и получает +{2}. Спасибо за неоценимую услугу обществу.",
                ["EventHuntSurvivedV3"] = "{0} выходит из охоты со всеми волосами и получает +{1}. Мазилы.",
                ["EventHuntDiedV4"] = "{0}, самый волосатый на острове, откинулся сам, без единого выстрела. Умер, как и жил: с волосами на лице и никому на фиг не нужный. Охота окончена.",
                ["EventHuntEscapedV4"] = "{0} смылся с острова вместе со своей гривой, как крыса с нарощенными волосами. Вернётся, когда кончится бальзам. Охота окончена.",
                ["EventBladeStormStartV4"] = "<color=#e0a526>ВСПЫШКА АЛОПЕЦИИ</color>: {0} мин вертолёт, Брэдли и Чинук дают x{1}. Под ноль.",
                ["EventBladeStormEndV2"] = "Вспышка алопеции закончилась. Вертолёт снова платит как обычно, как бюджетник.",
                ["EventStoppedByAdminV2"] = "Админ отменил событие «{0}». Жалобы — к его парикмахеру.",
                ["ReasonHuntKillV2"] = "охота на волосатого ({0})",
                ["ReasonHuntSurvived"] = "пережить охоту",
                ["AdminEventUsageV3"] = "Использование: /calvoadmin evento <hora|champu|peludo|alopecia|parar>",
                ["AdminEventBusy"] = "Уже идёт событие: {0}. Сначала останови его: /calvoadmin evento parar.",
                ["AdminEventCannotStart"] = "Сейчас нельзя запустить «{0}» (мало игроков онлайн или отключено в конфиге?).",
                ["AdminEventNone"] = "Сейчас никакого события нет.",
                ["BatteryTag"] = " [батарейка x{0}]",
                ["HallEmpty"] = "Ни одна карта ещё не закрыта. Первая попадёт сюда со следующим вайпом: полируй лоб.",
                ["HallMapClosed"] = "Карта закрыта {0}",
                ["HallNumber"] = "#{0}",
                ["HallPodiumPlace"] = "{0}.  {1}",
                ["HallTopKiller"] = "Больше всех убийств: <color=#e0a526>{0}</color> ({1})",
                ["HallTopDeaths"] = "Больше всех смертей: <color=#e0662f>{0}</color> ({1}). С такой смертностью можно и косички отрастить.",
                ["HallTopDeathsPlain"] = "Больше всех смертей: <color=#e0662f>{0}</color> ({1})",
                ["HallSnapshot"] = "Снимок карты от {0}",
                ["HallWipeWinner"] = "<color=#e0a526>{0}</color> забирает карту с алопецией <color=#e0a526>{1}</color> (пьедестал в Зале славы, <color=#e0a526>/calvos</color>). Державный лоб.",
                ["AdminHallUsageV2"] = "Использование: /calvoadmin salon guardar | /calvoadmin salon cerrar [forzar] | /calvoadmin salon borrar <n>",
                ["AdminHallCloseUsage"] = "Использование: isla.salon cerrar [forzar]",
                ["AdminHallClosed"] = "Карта закрыта: запись #{0} сохранена в Зале славы. Убийства и смерти карты обнулены.",
                ["AdminHallClosedEmpty"] = "Карта закрыта без записи в Зале славы (на этой карте никто не убивал и не умирал, или зал отключён). Убийства и смерти карты обнулены.",
                ["AdminHallClosedReset"] = "Алопеция всех игроков обнулена (в конфиге включён сброс при вайпе).",
                ["AdminHallCloseRecent"] = "Ничего не закрыто: карта уже закрыта {0} в {1}, меньше {2} ч назад. Чтобы закрыть снова, добавь 'forzar'.",
                ["AdminHallSaved"] = "Запись #{0} сохранена в Зале славы.",
                ["AdminHallNothing"] = "Ничего не сохранено: на этой карте ни у кого нет ни алопеции, ни убийств.",
                ["AdminHallDeleted"] = "Запись #{0} ({1}) удалена из Зала славы.",
                ["AdminHallNotFound"] = "В Зале славы нет записи #{0}.",
                ["BountyUsage"] = "Использование: <color=#e0a526>/cabeza <игрок> <сумма></color> (минимум {0}). Поставленное не возвращается.",
                ["BountyTooLow"] = "Минимум: {0}. За меньшее ему даже бакенбарды не подровняют.",
                ["BountySelf"] = "Нельзя назначить цену за собственный скальп, сколько бы у тебя ни было лишних волос.",
                ["BountyNoBalanceV2"] = "Не хватает: у тебя {0}. Столько злобы при таком балансе.",
                ["BountyNotFound"] = "На острове никого не зовут «{0}». Чтобы ненавидеть, сначала выучи имя.",
                ["BountyAmbiguous"] = "Игроков с «{1}» в имени: {0}. Пиши подробнее, это не лотерея.",
                ["BountyClosed"] = "Награды за головы закрыты.",
                ["BountyFailed"] = "Что-то пошло не так с Очками Черепушки, ничего не поставлено. Попробуй ещё раз.",
                ["BountyPlaced"] = "<color=#e0a526>{0}</color> назначает <color=#e0a526>{1}</color> за скальп <color=#e0662f>{2}</color>. Кто убьёт, тот и заберёт. Разыскивается живым или лысым.",
                ["BountyRaisedV2"] = "<color=#e0a526>{0}</color> добавляет <color=#e0a526>{1}</color> за скальп <color=#e0662f>{2}</color>: в банке уже <color=#e0a526>{3}</color>. Котировки растут.",
                ["BountyClaimed"] = "<color=#e0a526>{0}</color> снимает скальп <color=#e0662f>{1}</color> и забирает <color=#e0a526>{2}</color>. Побрит и оплачен.",
                ["BountyListEmpty"] = "Ни за один скальп ничего не назначено. Либо на острове мир, либо ни у кого нет ни одного Очка Черепушки.",
                ["BountyPriceShort"] = "{0} ОЧ",
                ["BountyMenuHint"] = "Назначай цену: <color=#e0a526>/cabeza <игрок> <сумма></color>. Заберёт тот, кто убьёт его в PvP. Поставленное не возвращается.",
                ["BountyMenuYou"] = "За твой скальп дают <color=#e0662f>{0}</color>. С такой лысиной тебя видно с другого конца острова.",
                ["DebugBountyNoClaim"] = "[debug] {0}: скальп {1} не забран · {2}",
                ["NoBountyTeam"] = "из одной команды",
                ["NoBountySleeper"] = "жертва спала или была офлайн",
                ["AdminBountyUsage"] = "Использование: /calvoadmin cabeza quitar <игрок>",
                ["AdminBountyRemoved"] = "Награда за скальп {0} отменена ({1}).",
                ["AdminBountyNone"] = "За скальп {0} никто ничего не назначал.",
                ["CalvoDelDiaName"] = "Лысый Дня",
                ["CalvoDelDiaChatV3"] = "<color=#e0a526>{0}</color> — <color=#e0a526>ЛЫСЫЙ ДНЯ</color>: +{1} алопеции с прошлых выборов. До завтра обращаться только на «вы».",
                ["CalvoDelDiaBannerV2"] = "{0} — ЛЫСЫЙ ДНЯ! Автографы раздаются на лысине.",
                ["CalvoDelDiaMenu"] = "Лысый Дня: <color=#e0a526>{0}</color> (+{1})",
                ["CalvoDelDiaPrize"] = "<color=#e0a526>Приз Лысому Дня:</color> {0}. Только не зазнавайся, наверху и так ничего не осталось.",
                ["CalvoDelDiaPrizeItems"] = "<color=#e0a526>Приз Лысому Дня:</color> уже в инвентаре (или под ногами, если не влезло).",
                ["AdminCalvoDelDiaUsage"] = "Использование: /calvoadmin calvodeldia ahora",
                ["AdminCalvoDelDiaOff"] = "Лысый Дня отключён в конфиге.",
                ["AdminCalvoDelDiaNone"] = "С прошлых выборов никто не набрал алопеции: Лысого Дня нет.",
                ["HudWallet"] = "<color=#9a9288>ОЧ</color> <color=#e0a526>{0}</color>     <color=#9a9288>ЛЫСИКИ</color> <color=#e0a526>{1}</color>",
                ["HudCounterV3"] = "<size=11><color=#9a9288>АЛОПЕЦИЯ</color></size>  <color=#e0a526>{0}</color>\n<size=10><color=#d8d8d8>{1}</color></size>",
                ["UnitRpFew"] = "{0} Очка Черепушки",
                ["UnitCoinsFew"] = "{0} лысика",
                ["Title_1"] = "Грязные Патлы",
                ["Title_1000"] = "Жалкая Шевелюра",
                ["Title_10000"] = "Наметившиеся Залысины",
                ["Title_100000"] = "Макушка Нараспашку",
                ["Title_1000000"] = "Рыцарь Тонзуры",
                ["Title_10000000"] = "Лорд Бильярдный Шар",
                ["Title_100000000"] = "Его Лысейшее Величество",
                ["RevengeChat"] = "<color=#e0a526>ЛЫСАЯ МЕСТЬ</color>: {0} наносит {1} ответный визит. Алопеция x{2}, с процентами.",
                ["RevengeTag"] = " [месть x{0}]",
                ["ConsolationKit"] = "<color=#e0a526>Лысый собес над тобой сжалился:</color> в инвентаре утешительный набор. Не привыкай.",
                ["DebugConsolationKit"] = "[debug] {0}: утешительный набор",
                ["DurationHoursMinutes"] = "{0} ч {1} мин",
                ["CalvarioTabJobs"] = "ПОРУЧЕНИЯ",
                ["BarberOptJobs"] = "Есть для меня поручения?",
                ["BarberJobsIntro"] = "Поручения на сегодня. Новые через {0}: что не заберёшь до этого, сгорит. И на чаевые не рассчитывай.",
                ["BarberJobsNone"] = "Сегодня для тебя ничего нет. Заходи через {0}, может, к тому времени вспомню, как ты выглядишь.",
                ["BarberJobClaim"] = "ЗАБРАТЬ: {0} (+{1})",
                ["BarberJobClaimed"] = "{0}  [получено]",
                ["BarberJobProgress"] = "{0}  ({1}/{2})  ·  {3}",
                ["BarberJobPaid"] = "Держи, {0}. И никому ни слова.",
                ["JobDone"] = "<color=#e0a526>Поручение Барбера выполнено:</color> {0}. Загляни в парикмахерскую (<color=#e0a526>/peluqueria</color>) за наградой: {1}.",
                ["JobsMenuHint"] = "Награду выдаёт Барбер (<color=#e0a526>/peluqueria</color>). Новые поручения через {0}; что не заберёшь до этого, сгорит.",
                ["JobsMenuEmpty"] = "Сегодня у Барбера нет поручений. Наслаждайся безработицей.",
                ["JobsMenuReward"] = "+{0}",
                ["JobsMenuProgress"] = "{0} / {1}",
                ["JobsMenuReady"] = "ГОТОВО: забери у Барбера",
                ["JobsMenuClaimed"] = "ПОЛУЧЕНО",
                ["JobKillScientists"] = "Убей учёных: {0}",
                ["JobKillScientistsOne"] = "Убей учёного",
                ["JobKillScientistsAt"] = "Убей учёных в локации {1}: {0}",
                ["JobKillScientistsAtOne"] = "Убей учёного в локации {1}",
                ["JobBreakBarrels"] = "Разбей бочки: {0}",
                ["JobBreakBarrelsOne"] = "Разбей бочку",
                ["JobKillAnimals"] = "Убей животных: {0}",
                ["JobKillAnimalsOne"] = "Убей животное",
                ["JobRaidBase"] = "Зачисти Дома Отца Яно: {0}",
                ["JobRaidBaseOne"] = "Зачисти Дом Отца Яно",
                ["JobRaidBaseLevel"] = "Зачисти Дома Отца Яно (сложность «{1}» или выше): {0}",
                ["JobRaidBaseLevelOne"] = "Зачисти Дом Отца Яно (сложность «{1}» или выше)",
                ["RaidDifficulty1"] = "средняя",
                ["RaidDifficulty2"] = "сложная",
                ["RaidDifficulty3"] = "эксперт",
                ["RaidDifficulty4"] = "кошмар",
                ["JobGather"] = "Добудь ресурс «{1}»: {0}",
                ["Resource_wood"] = "дерево",
                ["Resource_stones"] = "камень",
                ["Resource_metal.ore"] = "металлическая руда",
                ["Resource_sulfur.ore"] = "серная руда",
                ["JobSurvive"] = "Продержись в движении и без смертей: {0} мин",
                ["JobSurviveOne"] = "Продержись в движении и без смертей: 1 мин",
                ["DebugJobDone"] = "[debug] {0}: поручение '{1}' выполнено",
                ["DebugJobPaid"] = "[debug] {0}: поручение '{1}' оплачено ({2})",
                ["BarberOptInsurance"] = "Хочу страховку лысины ({0})",
                ["BarberInsuranceOffer"] = "Страховка лысины: {0}, и следующая смерть не будет стоить тебе ни волоска. Мелкий шрифт: отсутствует.",
                ["BarberInsuranceBuy"] = "ПО РУКАМ",
                ["BarberInsuranceDone"] = "Оплачено: {0}. Армированный скотч наклеен: следующая смерть ничего не отнимет. Полное каско, кроме позора.",
                ["BarberInsuranceRepriced"] = "С тех пор как я назвал цену, у тебя прибавилось алопеции, и страховка подорожала. Вот новая цена.",
                ["DebugInsurance"] = "[debug] {0}: страховка лысины ({1})"
            };
        }

        // Text resolved in the language of whoever reads it (1.13.0). A broadcast, a banner or a debug line is built once
        // and Lang resolves each Txt argument with the reader's id, so every player gets it in their own language.
        private sealed class Txt
        {
            private readonly Func<string, string> resolve;

            public Txt(Func<string, string> resolve)
            {
                this.resolve = resolve;
            }

            public string For(string userId) => resolve(userId);

            // No reader (console, logs, JSON): Spanish.
            public override string ToString() => resolve(null);

            public static Txt operator +(Txt a, Txt b) => new Txt(id => a.For(id) + b.For(id));
        }

        private Txt T(string key, params object[] args) => new Txt(id => Lang(key, id, args));

        private static Txt Plain(string text) => new Txt(id => text);

        private Txt Num(long value) => new Txt(id => FormatBaldness(value, id));

        private Txt Compact(long value) => new Txt(id => FormatCompact(value, id));

        private Txt DateTxt(DateTime date) => new Txt(id => FormatDate(date, id));

        private Txt UnitTxt(bool rp, long amount) => new Txt(id => UnitText(rp, amount, id));

        private static Txt JoinTxt(string separator, IEnumerable<Txt> parts)
        {
            List<Txt> list = parts.ToList();
            return new Txt(id => string.Join(separator, list.Select(p => p.For(id)).ToArray()));
        }

        // userId null = nobody reads it in game (console, RCON, JSON, logs): Spanish.
        private string Message(string key, string userId) =>
            userId == null ? lang.GetMessageByLanguage(key, this, ConsoleLanguage) : lang.GetMessage(key, this, userId);

        private string Lang(string key, string userId = null, params object[] args)
        {
            string message = Message(key, userId);
            if (args.Length == 0)
            {
                return message;
            }

            var resolved = new object[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                resolved[i] = args[i] is Txt txt ? txt.For(userId) : args[i];
            }

            try
            {
                return string.Format(message, resolved);
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

        // Messages for everyone go player by player, each in their own language (since 1.13.0).
        private void Broadcast(string key, params object[] args)
        {
            foreach (BasePlayer player in BasePlayer.activePlayerList)
            {
                if (IsRealPlayer(player) && player.IsConnected)
                {
                    Reply(player, key, args);
                }
            }
        }

        // Oxide.Rust's chat helpers pass this SteamID to "chat.add", and the client draws that account's avatar.
        private void SendChat(BasePlayer player, string message) => Player.Message(player, message, config.ChatIconSteamId);

        // The "LanguageCode" text of the lang file the player actually reads: "es", "en" or "ru". Numbers, dates, plurals and
        // the per-language texts of the config follow it, so they always match the language of the text around them.
        private string TextLanguage(string userId)
        {
            string code = Message("LanguageCode", userId);
            return code == "en" || code == "ru" ? code : "es";
        }

        // Thousands separators built by hand, so they do not depend on the server's cultures: 1.000.000 (Spanish),
        // 1,000,000 (English) and 1 000 000 (Russian).
        private static readonly NumberFormatInfo SpanishNumbers = new NumberFormatInfo { NumberGroupSeparator = ".", NumberGroupSizes = new[] { 3 } };
        private static readonly NumberFormatInfo EnglishNumbers = new NumberFormatInfo { NumberGroupSeparator = ",", NumberGroupSizes = new[] { 3 } };
        private static readonly NumberFormatInfo RussianNumbers = new NumberFormatInfo { NumberGroupSeparator = " ", NumberGroupSizes = new[] { 3 } };

        private static NumberFormatInfo NumbersOf(string language) => language == "en" ? EnglishNumbers : language == "ru" ? RussianNumbers : SpanishNumbers;

        private string FormatBaldness(long value, string userId) => value.ToString("#,0", NumbersOf(TextLanguage(userId)));

        // Units of the short forms, biggest first: unit and what goes after the number.
        private static readonly KeyValuePair<ulong, string>[] SpanishCompactUnits =
        {
            new KeyValuePair<ulong, string>(1000000000000000000UL, " T"), new KeyValuePair<ulong, string>(1000000000000UL, " B"),
            new KeyValuePair<ulong, string>(1000000UL, " M")
        };

        private static readonly KeyValuePair<ulong, string>[] EnglishCompactUnits =
        {
            new KeyValuePair<ulong, string>(1000000000000000000UL, "Qi"), new KeyValuePair<ulong, string>(1000000000000000UL, "Qa"),
            new KeyValuePair<ulong, string>(1000000000000UL, "T"), new KeyValuePair<ulong, string>(1000000000UL, "B")
        };

        private static readonly KeyValuePair<ulong, string>[] RussianCompactUnits =
        {
            new KeyValuePair<ulong, string>(1000000000000000000UL, " квинтлн"), new KeyValuePair<ulong, string>(1000000000000000UL, " квадрлн"),
            new KeyValuePair<ulong, string>(1000000000000UL, " трлн"), new KeyValuePair<ulong, string>(1000000000UL, " млрд")
        };

        private static readonly KeyValuePair<ulong, string>[] SpanishWalletUnits =
        {
            new KeyValuePair<ulong, string>(1000000000UL, " mil M"), new KeyValuePair<ulong, string>(1000000UL, " M")
        };

        private static readonly KeyValuePair<ulong, string>[] EnglishWalletUnits =
        {
            new KeyValuePair<ulong, string>(1000000000UL, "B"), new KeyValuePair<ulong, string>(1000000UL, "M")
        };

        private static readonly KeyValuePair<ulong, string>[] RussianWalletUnits =
        {
            new KeyValuePair<ulong, string>(1000000000UL, " млрд"), new KeyValuePair<ulong, string>(1000000UL, " млн")
        };

        // Short form for narrow spots (counter, +X/-X popup, ranking): the full number below a thousand million, then one
        // decimal, truncated so it never shows more than there is. Spanish: M (10^6), B (10^12, billón) and T (10^18);
        // English: B, T, Qa and Qi (short scale); Russian: млрд, трлн, квадрлн and квинтлн.
        private string FormatCompact(long value, string userId)
        {
            if (value > -1000000000L && value < 1000000000L)
            {
                return FormatBaldness(value, userId);
            }

            string language = TextLanguage(userId);
            return Shorten(value, language, language == "en" ? EnglishCompactUnits : language == "ru" ? RussianCompactUnits : SpanishCompactUnits);
        }

        // Wallet figures: the full number below a million, then "12,3 M" / "12.3M" / "12,3 млн" and, from a thousand million,
        // "1,5 mil M" / "1.5B" / "1,5 млрд" (one decimal, truncated like FormatCompact).
        private string FormatWallet(long value, string userId)
        {
            if (value > -1000000L && value < 1000000L)
            {
                return FormatBaldness(value, userId);
            }

            string language = TextLanguage(userId);
            return Shorten(value, language, language == "en" ? EnglishWalletUnits : language == "ru" ? RussianWalletUnits : SpanishWalletUnits);
        }

        private static string Shorten(long value, string language, KeyValuePair<ulong, string>[] units)
        {
            string sign = value < 0 ? "-" : string.Empty;
            ulong magnitude = value < 0 ? (ulong)(-(value + 1)) + 1UL : (ulong)value;
            KeyValuePair<ulong, string> unit = units.FirstOrDefault(u => magnitude >= u.Key);
            if (unit.Key == 0)
            {
                unit = units[units.Length - 1];
            }

            ulong whole = magnitude / unit.Key;
            ulong tenth = magnitude % unit.Key / (unit.Key / 10);
            string decimalSeparator = language == "en" ? "." : ",";
            string number = whole.ToString("#,0", NumbersOf(language)) + (tenth > 0 ? decimalSeparator + tenth.ToString(CultureInfo.InvariantCulture) : string.Empty);
            return sign + number + unit.Value;
        }

        // 04/10/2026 (Spanish), 4 Oct 2026 (English) and 04.10.2026 (Russian).
        private string FormatDate(DateTime date, string userId)
        {
            string language = TextLanguage(userId);
            string format = language == "en" ? "d MMM yyyy" : language == "ru" ? "dd.MM.yyyy" : "dd/MM/yyyy";
            return date.ToString(format, CultureInfo.InvariantCulture);
        }

        // "1 Punto de Chola" / "2 Puntos de Chola"; Russian has three forms (1 очко, 2 очка, 5 очков), hence the "Few" keys.
        private string UnitText(bool rp, long amount, string userId)
        {
            string form = PluralForm(amount, TextLanguage(userId));
            string key = rp
                ? (form == "One" ? "UnitRpOneV2" : form == "Few" ? "UnitRpFew" : "UnitRpV2")
                : (form == "One" ? "UnitCoinsOneV2" : form == "Few" ? "UnitCoinsFew" : "UnitCoinsV2");
            return Lang(key, userId, Num(amount));
        }

        // Russian: 1, 21, 31... "One"; 2-4, 22-24... "Few" (but 12-14 are "Other"); the rest "Other". Elsewhere only 1 is "One".
        private static string PluralForm(long amount, string language)
        {
            if (language != "ru")
            {
                return amount == 1 ? "One" : "Other";
            }

            long lastTwo = Math.Abs(amount % 100);
            long last = lastTwo % 10;
            if (last == 1 && lastTwo != 11) return "One";
            if (last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14)) return "Few";
            return "Other";
        }

        // The config text in the reader's language: the "other languages" entry for their language code, or the Spanish one.
        private string ConfigText(string spanish, Dictionary<string, string> others, string userId)
        {
            string language = TextLanguage(userId);
            return language != "es" && others != null && others.TryGetValue(language, out string text) && !string.IsNullOrEmpty(text) ? text : spanish ?? string.Empty;
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
                nextRandomEventUtc = DateTime.UtcNow.AddMinutes(config.GlobalEvents.IntervalMinutes);
                timer.Every(config.GlobalEvents.IntervalMinutes * 60f, () =>
                {
                    nextRandomEventUtc = DateTime.UtcNow.AddMinutes(config.GlobalEvents.IntervalMinutes);
                    StartRandomEvent();
                });
            }

            ValidateCursedItemNames();
            CheckServerRewardsPatch(ServerRewards);
            LoadMonuments();

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
            surviveStreaks.Remove(victimId);
            dataDirty = true;

            // Suicide (the player is their own killer) does not count for the consolation kit.
            if (killer != victim)
            {
                CountDeathForKit(victimData);
            }

            if (victimData.HasDeathShield)
            {
                victimData.HasDeathShield = false;
                Reply(victim, "ItemShieldUsed");
                DebugNoReward(victimData, T("ItemShieldUsed"));
            }
            else if (!config.NpcDeathsLowerBaldness && IsKilledByNpc(info, killer))
            {
                DebugNoReward(victimData, T("NoRewardNpcDeath"));
            }
            else
            {
                int percent = config.DeathPenaltyPercent;
                Txt eventTag = Plain(string.Empty);
                if (activeEvent == GlobalEvent.ShampooRain)
                {
                    percent *= config.GlobalEvents.ShampooRain.Multiplier;
                    eventTag = EventTag(GlobalEvent.ShampooRain, config.GlobalEvents.ShampooRain.Multiplier);
                }

                percent = Math.Min(100, percent);
                Txt deathReason = T(headshot ? "ReasonHeadshotDeath" : "ReasonDeath", percent) + eventTag;
                long penalty = (long)Math.Ceiling(victimData.Baldness * percent / 100.0);

                ChangeBaldness(victimData, -penalty, true, deathReason);
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

            // Did the victim kill this killer in the last minutes? Checked before the new grudge: the victim now owes one too.
            ulong killerId = (ulong)killer.userID;
            bool revenge = HasGrudge(killerId, victimId);
            RecordGrudge(victimId, killerId);

            if (!IsKillRewardable(killerData, killerId, victim, victimData.Name))
            {
                return;
            }

            long reward = headshot ? config.HeadshotKillReward : config.KillReward;
            Txt reason = T(headshot ? "ReasonPlayerHeadshotKillV2" : "ReasonPlayerKill", victimData.Name);
            if (revenge && reward > 0)
            {
                // One revenge per grudge: it is spent here.
                grudges[killerId].Remove(victimId);
                int multiplier = config.Revenge.Multiplier;
                reward = SaturatingMultiply(reward, multiplier);
                reason += T("RevengeTag", multiplier);
                Broadcast("RevengeChat", killerData.Name, victimData.Name, multiplier);
            }

            GainBaldness(killerData, reward, reason);
        }

        // Hair revenge: true if victimOf was killed by killerOf within the window and has not taken revenge yet.
        private bool HasGrudge(ulong victimOf, ulong killerOf)
        {
            return config.Revenge.Enabled && grudges.TryGetValue(victimOf, out Dictionary<ulong, DateTime> killers)
                && killers.TryGetValue(killerOf, out DateTime when) && (DateTime.UtcNow - when).TotalMinutes <= config.Revenge.WindowMinutes;
        }

        // Only the last kill of each pair counts. Old grudges of that victim are dropped on the way.
        private void RecordGrudge(ulong victimId, ulong killerId)
        {
            if (!config.Revenge.Enabled)
            {
                return;
            }

            if (!grudges.TryGetValue(victimId, out Dictionary<ulong, DateTime> killers))
            {
                killers = new Dictionary<ulong, DateTime>();
                grudges[victimId] = killers;
            }

            DateTime now = DateTime.UtcNow;
            foreach (ulong old in killers.Where(k => (now - k.Value).TotalMinutes > config.Revenge.WindowMinutes).Select(k => k.Key).ToList())
            {
                killers.Remove(old);
            }

            killers[killerId] = now;
        }

        // Consolation kit: the death that makes "Deaths" within the window earns a kit for the next respawn, at most one every
        // "At most one kit every" minutes. The deaths that earned it start over.
        private void CountDeathForKit(PlayerData data)
        {
            ConsolationKitConfig kit = config.ConsolationKit;
            if (!kit.Enabled || kit.Items.Count == 0 || pendingKits.Contains(data.Id))
            {
                return;
            }

            DateTime now = DateTime.UtcNow;
            if (!recentDeaths.TryGetValue(data.Id, out List<DateTime> deaths))
            {
                deaths = new List<DateTime>();
                recentDeaths[data.Id] = deaths;
            }

            deaths.Add(now);
            deaths.RemoveAll(d => (now - d).TotalMinutes > kit.WindowMinutes);
            if (deaths.Count < kit.Deaths || (data.LastConsolationKit != default(DateTime) && (now - data.LastConsolationKit).TotalMinutes < kit.CooldownMinutes))
            {
                return;
            }

            recentDeaths.Remove(data.Id);
            pendingKits.Add(data.Id);
        }

        // On respawn (waking up alive): the items straight to the inventory, with no "SERVER gave you" notice, and our message.
        private void GiveConsolationKit(BasePlayer player)
        {
            ulong id = (ulong)player.userID;
            if (player.IsDead() || !pendingKits.Remove(id))
            {
                return;
            }

            PlayerData data = GetOrCreateData(player);
            data.LastConsolationKit = DateTime.UtcNow;
            dataDirty = true;
            foreach (PrizeItem item in config.ConsolationKit.Items)
            {
                GiveItem(player, item.Shortname, item.Amount, false);
            }

            Reply(player, "ConsolationKit");
            SendDebug("DebugConsolationKit", data.Name);
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
                AddJobProgress(killer, JobType.BreakBarrels, 1);
                return;
            }

            // Barber's jobs count every scientist and animal, whether or not it gives alopecia. Scientists are NPC players
            // ("scientist" in the prefab: scientistnpc_*, scientist2*); the turrets (sentry.scientist.*) are not players.
            if (entityPlayer != null && prefab.IndexOf("scientist", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Vector3 where = entity.transform.position;
                AddJobProgress(killer, JobType.KillScientists, 1, job => string.IsNullOrEmpty(job.Monument) || IsInMonument(where, job.Monument));
            }
            else if (animalPrefabs.Contains(prefab))
            {
                AddJobProgress(killer, JobType.KillAnimals, 1);
            }

            if (!npcTiers.ContainsKey(prefab) && !IsPossibleNpc(entity))
            {
                return;
            }

            PlayerData killerData = GetOrCreateData(killer);
            if (!TryGetNpcReward(prefab, out int tier, out long reward, out Txt whyNot))
            {
                DebugNoReward(killerData, whyNot);
                return;
            }

            GainBaldness(killerData, reward, T("ReasonNpcKill", prefab, tier));
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
            public Txt Label;
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
            if (!TryGetNpcReward(prefab, out int tier, out long reward, out Txt whyNot))
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

            Txt label = Plain(prefab);
            if (activeEvent == GlobalEvent.BladeStorm)
            {
                reward = SaturatingMultiply(reward, config.GlobalEvents.BladeStorm.Multiplier);
                label += EventTag(GlobalEvent.BladeStorm, config.GlobalEvents.BladeStorm.Multiplier);
            }

            PayEventReward(new RewardEvent
            {
                Label = label,
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
                        T("ReasonEventParticipant", rewardEvent.Label, rewardEvent.Tier));
                }
            }

            foreach (BasePlayer mate in GetNearbyOnlineTeammates(rewardEvent.State.Teams, rewardEvent.Position))
            {
                if (paid.Add((ulong)mate.userID))
                {
                    GainBaldness(GetOrCreateData(mate), rewardEvent.Amount,
                        T("ReasonEventTeammate", rewardEvent.Label, rewardEvent.Tier));
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
            if (!storedData.BountyInfo.TryGetValue(target.Id, out BountyRecord info))
            {
                // A bounty from before 1.12.0 has no record: its start is unknown, so it is not made up here.
                info = new BountyRecord { Since = previous > 0 ? default(DateTime) : DateTime.Now };
                storedData.BountyInfo[target.Id] = info;
            }

            if (!info.PlacedBy.Contains(placer.Id))
            {
                info.PlacedBy.Add(placer.Id);
            }

            dataDirty = true;

            if (total > amount)
            {
                Broadcast("BountyRaisedV2", placer.Name, UnitTxt(true, amount), target.Name, UnitTxt(true, total));
            }
            else
            {
                Broadcast("BountyPlaced", placer.Name, UnitTxt(true, amount), target.Name);
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
                    Reply(player, "AdminInvalidValue", Num(MinBaldness));
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
            ChangeBaldness(target, value - target.Baldness, false, T("ReasonAdmin"));
            ExcludeFromCalvoDelDia(target, before);
            Reply(player, action == "set" ? "AdminSetV2" : "AdminResetV2", target.Name, Num(target.Baldness));
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
                    Reply(player, "AdminHallDeleted", number, DateTxt(entry.Date));
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
            storedData.BountyInfo.Remove(target.Id);
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

        // isla.salon: the whole hall of fame as one line of JSON, for the website (1.12.0).
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
            if (args.Length == 0)
            {
                arg.ReplyWith(JsonConvert.SerializeObject(HallOfFameJson(), Formatting.None));
                return;
            }

            bool force = args.Length == 2 && args[1].ToLowerInvariant() == "forzar";
            if (args.Length == 0 || args.Length > 2 || args[0].ToLowerInvariant() != "cerrar" || (args.Length == 2 && !force))
            {
                arg.ReplyWith(Lang("AdminHallCloseUsage", null));
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

        // JSON for the website (1.12.0): isla.salon, isla.calvodeldia, isla.cabezas and isla.evento. Server console and RCON
        // only, like isla.ranking. No SteamIDs: the website shows all of it in public. Times in ISO 8601 UTC ("...Z").
        private static string IsoUtc(DateTime time) =>
            time == default(DateTime) ? null : time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        private string StoredName(ulong id) => storedData.Players.TryGetValue(id, out PlayerData data) ? data.Name ?? string.Empty : string.Empty;

        // Newest first. "wipe" is the server date the entry was saved; "manual" marks a "/calvoadmin salon guardar" snapshot.
        private object HallOfFameJson() =>
            storedData.HallOfFame.AsEnumerable().Reverse().Select(entry => new
            {
                wipe = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                manual = entry.Manual,
                top = entry.Podium.Where(p => p != null).Select(p => new
                {
                    name = p.Name ?? string.Empty,
                    alopecia = p.Value,
                    title = TitleName(GetTitleIndex(p.Value))
                }).ToList(),
                mostKills = entry.TopKiller == null ? null : new { name = entry.TopKiller.Name ?? string.Empty, kills = entry.TopKiller.Value },
                mostDeaths = entry.TopDeaths == null ? null : new { name = entry.TopDeaths.Name ?? string.Empty, deaths = entry.TopDeaths.Value }
            }).ToList();

        [ConsoleCommand("isla.calvodeldia")]
        private void CcmdIslaCalvoDelDia(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                return;
            }

            CalvoDelDiaData day = storedData.CalvoDelDia;
            CalvoDelDiaRecord current = day.CurrentId == 0 ? null : day.History.LastOrDefault(r => r != null && r.Id == day.CurrentId);
            var json = new
            {
                current = current == null ? null : new { name = current.Name ?? string.Empty, gained = current.Gained, since = IsoUtc(current.Date) },
                nextPick = config.CalvoDelDia.Enabled ? IsoUtc(NextCalvoDelDiaPick()) : null,
                history = day.History.Where(r => r != null).Reverse().Take(14)
                    .Select(r => new { date = IsoUtc(r.Date), name = r.Name ?? string.Empty, gained = r.Gained }).ToList()
            };

            arg.ReplyWith(JsonConvert.SerializeObject(json, Formatting.None));
        }

        // Server time of the next scheduled pick: today at the pick time, or tomorrow if today's is done. If today's time has
        // passed without a pick, it happens at the next minute check, so "now".
        private DateTime NextCalvoDelDiaPick()
        {
            DateTime now = DateTime.Now;
            DateTime pick = now.Date.AddMinutes(calvoDelDiaMinutes);
            if (storedData.CalvoDelDia.LastPickDate == TodayKey())
            {
                return pick.AddDays(1);
            }

            return pick < now ? now : pick;
        }

        // Biggest bounty first. "placedBy" and "since" are only known for bounties placed from 1.12.0 on ([] and null before).
        [ConsoleCommand("isla.cabezas")]
        private void CcmdIslaCabezas(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                return;
            }

            var bounties = storedData.Bounties
                .Where(b => b.Value > 0)
                .OrderByDescending(b => b.Value)
                .Select(b =>
                {
                    storedData.BountyInfo.TryGetValue(b.Key, out BountyRecord info);
                    return new
                    {
                        target = StoredName(b.Key),
                        amount = b.Value,
                        placedBy = info == null ? new List<string>() : info.PlacedBy.Select(StoredName).ToList(),
                        since = info == null ? null : IsoUtc(info.Since)
                    };
                })
                .ToList();

            arg.ReplyWith(JsonConvert.SerializeObject(bounties, Formatting.None));
        }

        // The running global event, or {"active": null} plus "nextAt" (the next random event try) when events are on.
        [ConsoleCommand("isla.evento")]
        private void CcmdIslaEvento(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null)
            {
                return;
            }

            var json = new Dictionary<string, object>();
            if (activeEvent == GlobalEvent.None)
            {
                json["active"] = null;
                if (config.GlobalEvents.Enabled && nextRandomEventUtc != default(DateTime))
                {
                    json["nextAt"] = IsoUtc(nextRandomEventUtc);
                }
            }
            else
            {
                json["active"] = activeEvent.ToString();
                json["name"] = EventName(activeEvent).ToString();
                json["endsAt"] = IsoUtc(eventEndsUtc);
                json["target"] = activeEvent == GlobalEvent.HairiestHunt && huntTargetId != 0 ? StoredName(huntTargetId) : null;
            }

            arg.ReplyWith(JsonConvert.SerializeObject(json, Formatting.None));
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

        // For isla.evento only: when the running event ends and when the next random event is tried (UTC; default = unknown).
        private DateTime eventEndsUtc;
        private DateTime nextRandomEventUtc;

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null)
            {
                lastPositions.Remove((ulong)player.userID);
                calvarioNpcInUse.Remove((ulong)player.userID);
                pendingExchanges.Remove((ulong)player.userID);
                walletTexts.Remove((ulong)player.userID);
                insuranceQuotes.Remove((ulong)player.userID);
                surviveStreaks.Remove((ulong)player.userID);
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
                    BroadcastEvent("EventHuntStartV4", target.displayName, Num(lowest), Num(hunt.KillerBonus), minutes, Num(hunt.SurvivorBonus));
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
            eventEndsUtc = DateTime.UtcNow.AddMinutes(minutes);
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
                            BroadcastEvent("EventHuntSurvivedV3", target.Name, Num(bonus));
                            ChangeBaldness(target, bonus, true, T("ReasonHuntSurvived"));
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
                BroadcastEvent("EventHuntKilledV4", killerData.Name, targetData.Name, Num(bonus));
                ChangeBaldness(killerData, bonus, true, T("ReasonHuntKillV2", targetData.Name));
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

        private Txt EventName(GlobalEvent globalEvent) => T("EventName" + globalEvent + "V3");

        private Txt EventTag(GlobalEvent globalEvent, int multiplier) => T("EventTag", EventName(globalEvent), multiplier);

        // Real players who are connected, alive and awake.
        private static List<BasePlayer> GetActivePlayers() =>
            BasePlayer.activePlayerList.Where(p => IsRealPlayer(p) && p.IsConnected && !p.IsDead() && !p.IsSleeping()).ToList();

        // Every baldness gain from gameplay goes through here so the Bald hour can multiply it.
        private void GainBaldness(PlayerData data, long amount, Txt reason)
        {
            if (activeEvent == GlobalEvent.BaldHour)
            {
                amount = SaturatingMultiply(amount, config.GlobalEvents.BaldHour.Multiplier);
                reason += EventTag(GlobalEvent.BaldHour, config.GlobalEvents.BaldHour.Multiplier);
            }

            if (IsBatteryActive(data.Id))
            {
                amount = SaturatingMultiply(amount, config.CursedItems.Battery.Multiplier);
                reason += T("BatteryTag", config.CursedItems.Battery.Multiplier);
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
                GiveConsolationKit(player);
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
                    Text = Lang("HudCounterV3", player.UserIDString, FormatCompact(data.Baldness, player.UserIDString), GetTitle(data.Baldness, player.UserIDString)),
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
                RpAvailable ? FormatWallet(CheckRp(userId), player.UserIDString) : "-", CoinsAvailable ? FormatWallet(CoinBalance(userId), player.UserIDString) : "-");
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
                    Text = (delta > 0 ? "+" : string.Empty) + FormatCompact(delta, player.UserIDString),
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

            ShowBanner(T(key, args), config.Ui.BannerSeconds);
        }

        // Big text in the middle of the screen for everyone connected, each in their own language.
        private void ShowBanner(Txt message, float seconds)
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
                    Text = { Text = message.For(player.UserIDString), FontSize = 22, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1", FadeIn = 0.3f },
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
            ExchangeConfirm,
            Jobs,
            Insurance
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

        private Txt CursedItemName(string key)
        {
            switch (key)
            {
                case "bleach": return T("CalvarioItemBleach");
                case "ducttape": return T("CalvarioItemDuctTape");
                case "battery": return T("CalvarioItemBattery");
                case "dogtag": return T("CalvarioItemDogTag");
                case "bluedogtags": return T("CalvarioItemBlueDogTags");
                case "reddogtags": return T("CalvarioItemRedDogTags");
                case "gems": return T("CalvarioItemGems");
                default: return Plain(key);
            }
        }

        private Txt CursedItemDescription(string key)
        {
            CursedItemsConfig c = config.CursedItems;
            switch (key)
            {
                case "bleach": return T("CalvarioDescBleachV2", Mathf.RoundToInt(c.Bleach.WinChance * 100f), Num(c.Bleach.WinAmount), Num(c.Bleach.LoseAmount));
                case "ducttape": return T("CalvarioDescDuctTape");
                case "battery": return T("CalvarioDescBattery", c.Battery.Multiplier, c.Battery.Minutes);
                case "dogtag": return T("CalvarioDescDogTag", Num(c.DogTag.Reward));
                case "bluedogtags": return T("CalvarioDescBlueDogTags", Num(c.BlueDogTags.Reward));
                case "reddogtags": return T("CalvarioDescRedDogTags", Num(c.RedDogTags.Reward));
                case "gems": return T("CalvarioDescGems", Num(c.Gems.Reward));
                default: return Plain(string.Empty);
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

            foreach (PrizeItem item in config.ConsolationKit.Items)
            {
                FindItemDefinition(item.Shortname);
            }

            foreach (JobDefinition job in jobsById.Values.Where(j => j.ParsedType == JobType.Gather))
            {
                FindItemDefinition(job.Resource);
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

        private void TryDrop(BasePlayer player, ItemDropConfig item, Txt displayName = null)
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
                TryDrop(player, new ItemDropConfig { Shortname = color, DropChance = tags.DropChance }, T("CalvarioItemIdTag"));
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
            Txt name = CursedItemName(key);
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

            Txt reason = T("ReasonItemUseV2", name);
            switch (key)
            {
                case "bleach":
                    BleachConfig bleach = config.CursedItems.Bleach;
                    if (random.NextDouble() < bleach.WinChance)
                    {
                        GainBaldness(data, bleach.WinAmount, reason);
                        return Lang("ItemBleachWinV2", player.UserIDString, Num(bleach.WinAmount));
                    }

                    ChangeBaldness(data, -bleach.LoseAmount, true, reason);
                    return Lang("ItemBleachFail", player.UserIDString, Num(bleach.LoseAmount));
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
                    return Lang("BarberTrophyUsedV2", player.UserIDString, name, Num(reward));
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
            string line = Lang("CarneDeliveredV2", player.UserIDString, delivered, Num(delivered * tags.Reward));
            GainBaldness(data, delivered * tags.Reward, T("ReasonCarne"));

            if (tags.Shortnames.All(c => data.CarneColors.Contains(c)))
            {
                data.CarneColors.Clear();
                data.CarnesCompleted++;
                BroadcastEvent("CarneCompletedV3", data.Name, Num(tags.CollectionBonus));
                ChangeBaldness(data, tags.CollectionBonus, true, T("ReasonCarne"));
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
            Bounties,
            Jobs
        }

        // Gold, silver and bronze bars of the ranking and the hall of fame podium.
        private static readonly string[] PodiumColors = { "0.96 0.8 0.3 1", "0.82 0.82 0.86 1", "0.8 0.55 0.35 1" };

        // Word of each tab in "calvos.tab <tab> <page>".
        private static string TabCommand(MenuTab tab) =>
            tab == MenuTab.Hall ? "salon" : tab == MenuTab.Bounties ? "cabezas" : tab == MenuTab.Jobs ? "encargos" : "ranking";

        private static MenuTab ParseTab(string word) =>
            word == "salon" ? MenuTab.Hall : word == "cabezas" ? MenuTab.Bounties : word == "encargos" ? MenuTab.Jobs : MenuTab.Ranking;

        // /calvos: ranking, hall of fame and bounties. The cursed items live with the barber (OpenBarber).
        private void OpenCalvos(BasePlayer player, MenuTab tab, int page)
        {
            if ((tab == MenuTab.Bounties && !config.Bounties.Enabled) || (tab == MenuTab.Jobs && !JobsActive))
            {
                tab = MenuTab.Ranking;
            }

            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            var ui = new CuiElementContainer();
            string window = DrawCalvarioWindow(ui, data, userId);

            float tabsEnd = DrawTabs(ui, window, tab, userId);
            DrawCalvoDelDia(ui, window, userId, tabsEnd);
            switch (tab)
            {
                case MenuTab.Jobs:
                    DrawJobsTab(ui, window, data, userId);
                    break;
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
            AddText(ui, window, Lang("CalvarioYouV3", userId, FormatCompact(data.Baldness, userId), GetTitle(data.Baldness, userId)), 15, TextAnchor.MiddleRight, "0.45 0.93", "0.935 0.975");
            DrawTitleProgress(ui, window, data.Baldness, userId);
            AddButton(ui, window, Lang("CalvarioClose", userId), "0.956 0.935", "0.99 0.985", ColorPoleRed, null, UiMenu, 18);
            return window;
        }

        // The active tab looks like the old single section label; the others are buttons. Returns where the last tab ends.
        private float DrawTabs(CuiElementContainer ui, string window, MenuTab active, string userId)
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

            if (JobsActive)
            {
                tabs.Add(new KeyValuePair<MenuTab, string>(MenuTab.Jobs, "CalvarioTabJobs"));
            }

            // Narrower with four tabs, so the Calvo del Día still fits on their right.
            float width = tabs.Count > 3 ? 0.15f : 0.18f, gap = tabs.Count > 3 ? 0.008f : 0.01f;
            for (int i = 0; i < tabs.Count; i++)
            {
                float x0 = 0.03f + i * (width + gap);
                bool isActive = tabs[i].Key == active;
                AddButton(ui, window, Lang(tabs[i].Value, userId), Anchor(x0, 0.81f), Anchor(x0 + width, 0.86f), isActive ? ColorScalp : ColorCardDark,
                    isActive ? null : "calvos.tab " + TabCommand(tabs[i].Key) + " 0", null, 13, isActive ? ColorOnScalp : ColorText);
            }

            return 0.03f + tabs.Count * (width + gap) - gap;
        }

        // Right of the tabs, on every tab: the current Calvo del Día, if there is one.
        private void DrawCalvoDelDia(CuiElementContainer ui, string window, string userId, float tabsEnd)
        {
            CalvoDelDiaRecord current = CurrentCalvoDelDia();
            if (current != null)
            {
                AddText(ui, window, Lang("CalvoDelDiaMenu", userId, current.Name, Compact(current.Gained)), 12, TextAnchor.MiddleRight, Anchor(Math.Max(0.61f, tabsEnd + 0.01f), 0.81f), "0.97 0.86", ColorText);
            }
        }

        // Today's barber's jobs: what each one asks for, the barber's remark, how far you are and what it pays. Paid at the barber.
        private void DrawJobsTab(CuiElementContainer ui, string window, PlayerData data, string userId)
        {
            EnsureJobs(data);
            AddText(ui, window, Lang("JobsMenuHint", userId, DurationTxt(TimeToJobReset())), 12, TextAnchor.MiddleLeft, "0.03 0.1", "0.97 0.17", ColorText);
            List<KeyValuePair<JobProgress, JobDefinition>> jobs = data.Jobs.List
                .Where(p => jobsById.ContainsKey(p.Id))
                .Select(p => new KeyValuePair<JobProgress, JobDefinition>(p, jobsById[p.Id]))
                .ToList();
            if (jobs.Count == 0)
            {
                AddText(ui, window, Lang("JobsMenuEmpty", userId), 16, TextAnchor.MiddleCenter, "0.03 0.4", "0.97 0.6", ColorText);
                return;
            }

            const float top = 0.79f, bottom = 0.19f, gap = 0.012f;
            float height = Math.Min(0.18f, (top - bottom - gap * (jobs.Count - 1)) / jobs.Count);
            for (int i = 0; i < jobs.Count; i++)
            {
                JobProgress progress = jobs[i].Key;
                JobDefinition job = jobs[i].Value;
                float y1 = top - i * (height + gap), y0 = y1 - height;
                string block = AddPanel(ui, window, ColorCard, Anchor(0.03f, y0), Anchor(0.97f, y1));
                AddPanel(ui, block, progress.Claimed ? ColorMuted : progress.Done ? ColorGold : ColorPoleRed, "0 0", "0.006 1");
                AddText(ui, block, JobTaskTxt(job).For(userId), 14, TextAnchor.MiddleLeft, "0.02 0.58", "0.74 0.95", progress.Claimed ? ColorMuted : ColorScalp);
                AddText(ui, block, Lang("JobsMenuReward", userId, UnitTxt(true, job.Reward)), 13, TextAnchor.MiddleRight, "0.74 0.58", "0.985 0.95", progress.Claimed ? ColorMuted : ColorGold);
                AddText(ui, block, JobNoteTxt(job).For(userId), 11, TextAnchor.MiddleLeft, "0.02 0.32", "0.985 0.58", ColorMuted);

                float fill = job.Amount > 0 ? Math.Max(0f, Math.Min(1f, progress.Progress / (float)job.Amount)) : 1f;
                AddPanel(ui, block, ColorCardDark, "0.02 0.1", "0.62 0.24");
                if (fill > 0f)
                {
                    AddPanel(ui, block, progress.Done ? ColorGood : ColorScalp, "0.02 0.1", Anchor(0.02f + 0.6f * fill, 0.24f));
                }

                string status = progress.Claimed ? Lang("JobsMenuClaimed", userId)
                    : progress.Done ? Lang("JobsMenuReady", userId)
                    : Lang("JobsMenuProgress", userId, Num(progress.Progress), Num(job.Amount));
                AddText(ui, block, status, 12, TextAnchor.MiddleRight, "0.63 0.04", "0.985 0.3", progress.Done && !progress.Claimed ? ColorGold : ColorText);
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
                AddText(ui, block, Lang(entry.Manual ? "HallSnapshot" : "HallMapClosed", userId, DateTxt(entry.Date)), 13, TextAnchor.MiddleLeft, "0.015 0.8", "0.8 0.98", ColorScalp);
                AddText(ui, block, Lang("HallNumber", userId, entry.Number), 11, TextAnchor.MiddleRight, "0.8 0.8", "0.985 0.98", ColorMuted);

                for (int place = 0; place < entry.Podium.Count && place < PodiumColors.Length; place++)
                {
                    HallPlayer podium = entry.Podium[place];
                    float x0 = 0.015f + place * 0.325f;
                    string card = AddPanel(ui, block, ColorCardDark, Anchor(x0, 0.4f), Anchor(x0 + 0.315f, 0.78f));
                    AddPanel(ui, card, PodiumColors[place], "0 0", "0.02 1");
                    AddText(ui, card, Lang("HallPodiumPlace", userId, place + 1, podium.Name), 13, TextAnchor.MiddleLeft, "0.05 0.5", "0.98 1", PodiumColors[place]);
                    AddText(ui, card, FormatCompact(podium.Value, userId), 12, TextAnchor.MiddleLeft, "0.05 0", "0.98 0.5", ColorGold);
                }

                if (entry.TopKiller != null)
                {
                    AddText(ui, block, Lang("HallTopKiller", userId, entry.TopKiller.Name, Num(entry.TopKiller.Value)), 12, TextAnchor.MiddleLeft, "0.015 0.2", "0.985 0.38", ColorText);
                }

                if (entry.TopDeaths != null)
                {
                    // The joke only on the newest entry, so it is not repeated in every block.
                    AddText(ui, block, Lang(index == 0 ? "HallTopDeaths" : "HallTopDeathsPlain", userId, entry.TopDeaths.Name, Num(entry.TopDeaths.Value)), 12, TextAnchor.MiddleLeft, "0.015 0.02", "0.985 0.2", ColorText);
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
                AddText(ui, row, Lang("BountyPriceShort", userId, Compact(bounties[index].Value)), 14, TextAnchor.MiddleRight, "0.6 0", "0.98 1", ColorGold);
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

            AddText(ui, window, Lang("CalvarioNextV3", userId, TitleText(config.Titles.IndexOf(next), userId), Num(next.MinBaldness - baldness)), 11, TextAnchor.MiddleRight, "0.45 0.898", "0.935 0.925", ColorMuted);
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
                case "jobs":
                    OpenBarber(player, JobsActive ? BarberPage.Jobs : BarberPage.Main, null);
                    break;
                case "insurance":
                    OpenBarber(player, config.Insurance.Enabled && RpAvailable ? BarberPage.Insurance : BarberPage.Main, null);
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
                : mode == ExchangeMode.SellForCoins ? CoinsAvailable && config.Exchange.SellCoinsPer1000 > 0 : CoinsAvailable;

        private bool IsSell(ExchangeMode mode) => mode == ExchangeMode.SellForRp || mode == ExchangeMode.SellForCoins;

        // Whole RP/coins only: selling needs at least the minimum and an exact multiple of the rate (1000 for coins);
        // buying, a multiple of 10 (the rates are per 10 baldness).
        private bool ExchangeAmountValid(ExchangeMode mode, long baldness)
        {
            ExchangeConfig ex = config.Exchange;
            switch (mode)
            {
                case ExchangeMode.SellForRp: return baldness >= ex.MinSell && baldness % ex.SellBaldnessPerRp == 0;
                case ExchangeMode.SellForCoins: return baldness >= ex.MinSell && baldness % 1000 == 0;
                default: return baldness > 0 && baldness % 10 == 0;
            }
        }

        // RP or coins the player gets (selling) or pays (buying) for that much baldness.
        private long ExchangePrice(ExchangeMode mode, long baldness)
        {
            ExchangeConfig ex = config.Exchange;
            switch (mode)
            {
                case ExchangeMode.SellForRp: return baldness / ex.SellBaldnessPerRp;
                case ExchangeMode.SellForCoins: return SaturatingMultiply(baldness / 1000, ex.SellCoinsPer1000);
                case ExchangeMode.BuyWithRp: return SaturatingMultiply(baldness / 10, ex.BuyRpPer10);
                default: return SaturatingMultiply(baldness / 10, ex.BuyCoinsPer10);
            }
        }

        private string ExchangePriceText(ExchangeMode mode, long price, string userId) =>
            UnitText(mode == ExchangeMode.SellForRp || mode == ExchangeMode.BuyWithRp, price, userId);

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
                ChangeBaldness(data, -baldness, true, T("ReasonExchange"));
                return Lang("BarberExDoneSellV2", userId, Num(baldness), priceText);
            }

            // Bought baldness is not multiplied by events or the battery and does not count for the Calvo del Día. It does
            // pay tier prizes, like any other baldness (1.10.0).
            long before = data.Baldness;
            ChangeBaldness(data, baldness, true, T("ReasonExchange"));
            ExcludeFromCalvoDelDia(data, before);
            return Lang("BarberExDoneBuyV2", userId, Num(baldness), priceText);
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
                    carne.Add(Lang("CalvarioCarneHintV2", userId, Num(tags.Reward), Num(tags.CollectionBonus)));
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

                    line += Lang("BarberExIntroV4", userId, Num(data.Baldness),
                        RpAvailable ? Num(CheckRp(data.Id)) : "-", CoinsAvailable ? Num(CoinBalance(data.Id)) : "-");
                    AddExchangeOption(options, ExchangeMode.SellForRp, Lang("BarberExSellRpV4", userId, Num(ex.SellBaldnessPerRp)), userId);
                    AddExchangeOption(options, ExchangeMode.SellForCoins, Lang("BarberExSellCoinsV5", userId, Num(ex.SellCoinsPer1000)), userId);
                    AddExchangeOption(options, ExchangeMode.BuyWithRp, Lang("BarberExBuyRpV6", userId, Num(ex.BuyRpPer10)), userId);
                    AddExchangeOption(options, ExchangeMode.BuyWithCoins, Lang("BarberExBuyCoinsV5", userId, Num(ex.BuyCoinsPer10)), userId);
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
                            Num(amount), ExchangePriceText(amountFor.Mode, ExchangePrice(amountFor.Mode, amount), userId));
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

                    line = Lang(IsSell(toConfirm.Mode) ? "BarberExConfirmSellV2" : "BarberExConfirmBuyV2", userId, Num(toConfirm.Baldness),
                        ExchangePriceText(toConfirm.Mode, ExchangePrice(toConfirm.Mode, toConfirm.Baldness), userId));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberExConfirm", userId), "calvos.exchange confirm"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberExCancel", userId), "calvos.barber exchange"));
                    break;
                case BarberPage.Jobs:
                    EnsureJobs(data);
                    var jobsLine = new List<string>();
                    if (line != null) jobsLine.Add(line);
                    jobsLine.Add(Lang(data.Jobs.List.Count > 0 ? "BarberJobsIntro" : "BarberJobsNone", userId, DurationTxt(TimeToJobReset())));
                    line = string.Join("\n", jobsLine.ToArray());

                    foreach (JobProgress progress in data.Jobs.List)
                    {
                        if (!jobsById.TryGetValue(progress.Id, out JobDefinition job))
                        {
                            continue;
                        }

                        string task = JobTaskTxt(job).For(userId);
                        if (progress.Claimed)
                        {
                            options.Add(new KeyValuePair<string, string>(Lang("BarberJobClaimed", userId, task), null));
                        }
                        else if (progress.Done)
                        {
                            string claim = Lang("BarberJobClaim", userId, task, UnitTxt(true, job.Reward));
                            options.Add(job.Reward <= 0 || RpAvailable
                                ? new KeyValuePair<string, string>(claim, "calvos.jobs claim " + job.Id)
                                : new KeyValuePair<string, string>(Lang("BarberExClosedV3", userId, claim, "Server Rewards"), null));
                        }
                        else
                        {
                            options.Add(new KeyValuePair<string, string>(Lang("BarberJobProgress", userId, task, Num(progress.Progress), Num(job.Amount), UnitTxt(true, job.Reward)), null));
                        }
                    }

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                case BarberPage.Insurance:
                    if (data.HasDeathShield)
                    {
                        line = line ?? Lang("ItemShieldAlready", userId);
                    }
                    else
                    {
                        long price = InsurancePrice(data);
                        insuranceQuotes[data.Id] = price;
                        string offer = Lang("BarberInsuranceOffer", userId, UnitTxt(true, price));
                        line = line == null ? offer : line + "\n" + offer;
                        bool affordable = RpAvailable && CheckRp(data.Id) >= price;
                        string buy = Lang("BarberInsuranceBuy", userId);
                        options.Add(new KeyValuePair<string, string>(affordable ? buy : Lang("BarberExTooMuchV3", userId, buy), affordable ? "calvos.insurance buy" : null));
                    }

                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptBack", userId), "calvos.barber main"));
                    break;
                default:
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptItemsV3", userId), "calvos.barber items"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptCatalog", userId), "calvos.barber catalog"));
                    options.Add(new KeyValuePair<string, string>(Lang("BarberOptCarne", userId), "calvos.barber carne"));
                    if (JobsActive)
                    {
                        options.Add(new KeyValuePair<string, string>(Lang("BarberOptJobs", userId), "calvos.barber jobs"));
                    }

                    if (config.Exchange.Enabled)
                    {
                        options.Add(new KeyValuePair<string, string>(Lang("BarberOptExchangeV2", userId), "calvos.barber exchange"));
                    }

                    if (config.Insurance.Enabled)
                    {
                        string insurance = Lang("BarberOptInsurance", userId, UnitTxt(true, InsurancePrice(data)));
                        options.Add(RpAvailable
                            ? new KeyValuePair<string, string>(insurance, "calvos.barber insurance")
                            : new KeyValuePair<string, string>(Lang("BarberExClosedV3", userId, insurance, "Server Rewards"), null));
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
            AddText(ui, box, Lang("CalvarioYouV3", userId, FormatCompact(data.Baldness, userId), GetTitle(data.Baldness, userId)), 12, TextAnchor.MiddleRight, "0.5 0.88", "0.93 0.98", ColorMuted);
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
                AddText(ui, card, CursedItemName(key).For(userId), 14, TextAnchor.UpperLeft, "0.34 0.74", "0.98 0.95", count > 0 ? ColorScalp : ColorMuted);
                AddText(ui, card, Lang("CatalogHave", userId, count), 12, TextAnchor.UpperLeft, "0.34 0.54", "0.98 0.74", count > 0 ? ColorText : ColorMuted);

                string status = null;
                if (key == "ducttape" && data.HasDeathShield) status = Lang("CalvarioShieldOn", userId);
                if (key == "battery" && IsBatteryActive(data.Id)) status = Lang("CalvarioBatteryOn", userId, BatteryMinutesLeft(data.Id));
                AddText(ui, card, status ?? CursedItemDescription(key).For(userId), 11, TextAnchor.UpperLeft, "0.05 0.22", "0.97 0.5", status != null ? ColorGold : count > 0 ? ColorText : ColorMuted);

                AddButton(ui, card, Lang(count > 0 ? "CatalogUse" : "CatalogNone", userId), "0.05 0.05", "0.95 0.2",
                    count > 0 ? ColorPoleRed : ColorDisabled, count > 0 ? "calvos.use " + key + " catalog" : null, null, 13, count > 0 ? "1 1 1 1" : ColorMuted);
            }

            // Carne de Calvo: one slot per ID tag color, stamped or still missing.
            IdTagsConfig tags = config.CursedItems.IdTags;
            string carne = AddPanel(ui, window, ColorCard, "0.03 0.03", "0.97 0.295");

            int done = tags.Shortnames.Count(c => data.CarneColors.Contains(c));
            AddText(ui, carne, Lang("CatalogCarneTitle", userId, done, tags.Shortnames.Count, data.CarnesCompleted), 14, TextAnchor.MiddleLeft, "0.02 0.78", "0.98 0.97", ColorScalp);
            AddText(ui, carne, Lang("CalvarioCarneHintV2", userId, Num(tags.Reward), Num(tags.CollectionBonus)), 11, TextAnchor.MiddleLeft, "0.02 0.62", "0.98 0.78", ColorText);

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
                AddText(ui, row, FormatCompact(entry.Baldness, userId), 14, TextAnchor.MiddleRight, "0.5 0", "0.68 1", ColorGold);
                AddText(ui, row, GetTitle(entry.Baldness, userId), 13, TextAnchor.MiddleRight, "0.68 0", "0.98 1", ColorText);
            }

            int myIndex = ranking.IndexOf(me);
            string footer = myIndex <= 0
                ? Lang("CalvarioRankingFirstV2", userId)
                : Lang("CalvarioRankingYouV3", userId, myIndex + 1, ranking.Count, Num(ranking[myIndex - 1].Baldness - me.Baldness + 1), ranking[myIndex - 1].Name);
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
                lines.Add(Lang("AdminHallCloseRecent", userId, FormatDate(result.LastClose, userId),
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
                Reply(player, "HallWipeWinner", entry.Podium[0].Name, Num(entry.Podium[0].Value));
            });
        }

        // A PvP kill pays the victim's whole bounty to the killer, unless they are teammates or (by config) the victim was
        // asleep or offline. If Server Rewards does not pay, the bounty stays for the next one.
        private void TryClaimBounty(BasePlayer killer, PlayerData killerData, BasePlayer victim, PlayerData victimData)
        {
            if (!config.Bounties.Enabled || !storedData.Bounties.TryGetValue(victimData.Id, out long total) || total <= 0)
            {
                return;
            }

            Txt whyNot = null;
            if (killer.currentTeam != 0UL && killer.currentTeam == victim.currentTeam)
            {
                whyNot = T("NoBountyTeam");
            }
            else if (config.Bounties.NotOnSleepers && (victim.IsSleeping() || !victim.IsConnected))
            {
                whyNot = T("NoBountySleeper");
            }
            else if (!RpAvailable)
            {
                whyNot = T("NoRpPlugin");
            }
            else if (!AddRp(killerData.Id, total))
            {
                whyNot = T("NoRpRefused");
            }

            if (whyNot != null)
            {
                SendDebug("DebugBountyNoClaim", killerData.Name, victimData.Name, whyNot);
                return;
            }

            storedData.Bounties.Remove(victimData.Id);
            storedData.BountyInfo.Remove(victimData.Id);
            dataDirty = true;
            Broadcast("BountyClaimed", killerData.Name, victimData.Name, UnitTxt(true, total));
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

            Broadcast("CalvoDelDiaChatV3", record.Name, Num(record.Gained));
            ShowBanner(T("CalvoDelDiaBannerV2", record.Name), config.Ui.TitleUpBannerSeconds);
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
            var parts = new List<Txt>();
            var given = new List<string>();
            var spawned = new List<string>();
            int spawnFailed = GivePrize(data, player, prize, parts, given, spawned);
            SendDebug("DebugTierPrize", data.Name, T("CalvoDelDiaName"), PrizeDebugText(parts, given, spawned));
            if (player == null || !player.IsConnected)
            {
                return;
            }

            if (parts.Count > 0)
            {
                Reply(player, "CalvoDelDiaPrize", JoinTxt(", ", parts));
            }
            else if (given.Count > 0)
            {
                Reply(player, "CalvoDelDiaPrizeItems");
            }

            ReplySpawnedPrize(player, spawned, spawnFailed);
            SendPrizeMessage(player, prize);
        }

        #endregion

        #region Barber's jobs, hair insurance and languages (1.13.0)

        private bool JobsActive => config.Jobs.Enabled && config.Jobs.PerDay > 0;

        // A job day starts at the reset time (UTC): it is the UTC date of the last reset that has passed.
        private string JobDayKey() => DateTime.UtcNow.AddMinutes(-jobResetMinutes).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private TimeSpan TimeToJobReset()
        {
            DateTime now = DateTime.UtcNow;
            DateTime reset = now.Date.AddMinutes(jobResetMinutes);
            return (reset > now ? reset : reset.AddDays(1)) - now;
        }

        // "5 h 12 min", each language its way.
        private Txt DurationTxt(TimeSpan time) => T("DurationHoursMinutes", (int)time.TotalHours, time.Minutes);

        // Monuments of this map by their English name. Run at startup (OnServerInitialized also runs after a hot reload).
        private void LoadMonuments()
        {
            monumentsByName.Clear();
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments != null)
            {
                foreach (MonumentInfo monument in monuments)
                {
                    string name = monument?.displayPhrase?.english?.Trim();
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    if (!monumentsByName.TryGetValue(name, out List<MonumentInfo> list))
                    {
                        list = new List<MonumentInfo>();
                        monumentsByName[name] = list;
                    }

                    list.Add(monument);
                }
            }

            foreach (JobDefinition job in jobsById.Values.Where(j => j.ParsedType == JobType.KillScientists && !string.IsNullOrEmpty(j.Monument)))
            {
                if (!monumentsByName.ContainsKey(job.Monument.Trim()))
                {
                    PrintWarning($"Barber's jobs: job '{job.Id}' is not handed out: there is no '{job.Monument}' on this map. Monuments: {string.Join(", ", monumentsByName.Keys.OrderBy(k => k).ToArray())}.");
                }
            }
        }

        private bool IsInMonument(Vector3 position, string name) =>
            monumentsByName.TryGetValue(name.Trim(), out List<MonumentInfo> list) && list.Any(m => m != null && m.IsInBounds(position));

        // Jobs that can be done here and now: the monument is on the map, and raids need Raidable Bases loaded.
        private bool JobAvailable(JobDefinition job)
        {
            switch (job.ParsedType)
            {
                case JobType.KillScientists:
                    return string.IsNullOrEmpty(job.Monument) || monumentsByName.ContainsKey(job.Monument.Trim());
                case JobType.RaidBase:
                    return RaidableBases != null && RaidableBases.IsLoaded;
                default:
                    return true;
            }
        }

        // When the job day changes: "Jobs per day" different jobs at random among those that can be done. What was not
        // claimed the day before is lost.
        private void EnsureJobs(PlayerData data)
        {
            string day = JobDayKey();
            if (!JobsActive || data.Jobs.Day == day)
            {
                return;
            }

            List<JobDefinition> pool = jobsById.Values.Where(JobAvailable).ToList();
            data.Jobs.Day = day;
            data.Jobs.List = new List<JobProgress>();
            while (pool.Count > 0 && data.Jobs.List.Count < config.Jobs.PerDay)
            {
                JobDefinition pick = pool[random.Next(pool.Count)];
                pool.Remove(pick);
                data.Jobs.List.Add(new JobProgress { Id = pick.Id });
            }

            dataDirty = true;
        }

        // Adds to every job of that type the player has today (matches narrows it: a monument, a resource...). absolute: the
        // amount is the current value (the Survive streak), not something to add. Completing one is said quietly in chat.
        private void AddJobProgress(BasePlayer player, JobType type, long amount, Func<JobDefinition, bool> matches = null, bool absolute = false)
        {
            if (!JobsActive || amount <= 0 || !IsRealPlayer(player))
            {
                return;
            }

            PlayerData data = GetOrCreateData(player);
            EnsureJobs(data);
            foreach (JobProgress progress in data.Jobs.List)
            {
                if (progress.Done || !jobsById.TryGetValue(progress.Id, out JobDefinition job) || job.ParsedType != type || (matches != null && !matches(job)))
                {
                    continue;
                }

                long value = Math.Min(job.Amount, absolute ? Math.Max(progress.Progress, amount) : SaturatingAdd(progress.Progress, amount));
                if (value == progress.Progress)
                {
                    continue;
                }

                progress.Progress = value;
                dataDirty = true;
                if (value >= job.Amount)
                {
                    progress.Done = true;
                    if (player.IsConnected)
                    {
                        Reply(player, "JobDone", JobTaskTxt(job), UnitTxt(true, job.Reward));
                    }

                    SendDebug("DebugJobDone", data.Name, job.Id);
                }
            }
        }

        // What the job asks for, from the lang of each player ("Rompe 20 barriles"). The "One" keys are for an amount of 1.
        private Txt JobTaskTxt(JobDefinition job)
        {
            string one = job.Amount == 1 ? "One" : string.Empty;
            switch (job.ParsedType)
            {
                case JobType.KillScientists:
                    return string.IsNullOrEmpty(job.Monument)
                        ? T("JobKillScientists" + one, Num(job.Amount))
                        : T("JobKillScientistsAt" + one, Num(job.Amount), job.Monument.Trim());
                case JobType.BreakBarrels:
                    return T("JobBreakBarrels" + one, Num(job.Amount));
                case JobType.KillAnimals:
                    return T("JobKillAnimals" + one, Num(job.Amount));
                case JobType.RaidBase:
                    int level = Math.Min(4, job.MinDifficulty ?? 0);
                    return level > 0
                        ? T("JobRaidBaseLevel" + one, Num(job.Amount), T("RaidDifficulty" + level))
                        : T("JobRaidBase" + one, Num(job.Amount));
                case JobType.Gather:
                    return T("JobGather", Num(job.Amount), ResourceTxt(job.Resource));
                default:
                    return T("JobSurvive" + one, Num(job.Amount));
            }
        }

        // Lang "Resource_<shortname>" (madera, piedra...), or the shortname if the lang has none.
        private Txt ResourceTxt(string shortname)
        {
            string key = "Resource_" + shortname;
            return new Txt(id =>
            {
                string text = Message(key, id);
                return string.IsNullOrEmpty(text) || text == key ? shortname : text;
            });
        }

        // The barber's remark of the job, in the player's language (config "Text" and "Text in other languages").
        private Txt JobNoteTxt(JobDefinition job) => new Txt(id => ConfigText(job.Text, job.Texts, id));

        // After every other plugin's OnDispenserGather (where the gather rates multiply): the item already has what the player gets.
        private void OnDispenserGathered(ResourceDispenser dispenser, BasePlayer player, global::Item item) => CountGather(player, item);

        private void OnDispenserBonusReceived(ResourceDispenser dispenser, BasePlayer player, global::Item item) => CountGather(player, item);

        private void CountGather(BasePlayer player, global::Item item)
        {
            if (!JobsActive || item?.info == null || item.amount <= 0)
            {
                return;
            }

            string shortname = item.info.shortname;
            AddJobProgress(player, JobType.Gather, item.amount, job => string.Equals(job.Resource, shortname, StringComparison.OrdinalIgnoreCase));
        }

        // Raidable Bases (Casas de Padre Jano). Its 3.1.2 code calls this hook with 17 arguments in this order, and Oxide drops
        // the ones a hook method does not declare. "mode" is the difficulty (0-4) on versions that have them; 3.x has none and
        // always sends 512, and then a base counts for every RaidBase job, whatever its minimum difficulty.
        private void OnRaidableBaseCompleted(Vector3 raidPos, int mode, bool allowPVP, string id, float spawnTime, float despawnTime, float loadTime, ulong ownerId, BasePlayer owner, List<BasePlayer> raiders)
        {
            if (!JobsActive)
            {
                return;
            }

            var players = new HashSet<BasePlayer>();
            if (raiders != null)
            {
                foreach (BasePlayer raider in raiders)
                {
                    if (IsRealPlayer(raider)) players.Add(raider);
                }
            }

            if (IsRealPlayer(owner)) players.Add(owner);
            bool knownDifficulty = mode >= 0 && mode <= 4;
            foreach (BasePlayer player in players)
            {
                AddJobProgress(player, JobType.RaidBase, 1, job => !knownDifficulty || (job.MinDifficulty ?? 0) <= mode);
            }
        }

        [ConsoleCommand("calvos.jobs")]
        private void CcmdJobs(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            string[] parts = MenuArgs(arg);
            if (!IsRealPlayer(player) || !JobsActive || !RequireCalvarioNpc(player))
            {
                return;
            }

            OpenBarber(player, BarberPage.Jobs, parts.Length > 1 && parts[0] == "claim" ? ClaimJob(player, parts[1]) : null);
        }

        // Pays a finished job. Returns the barber's line (null if there is nothing to pay).
        private string ClaimJob(BasePlayer player, string jobId)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            EnsureJobs(data);
            JobProgress progress = data.Jobs.List.FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.OrdinalIgnoreCase));
            if (progress == null || !progress.Done || progress.Claimed || !jobsById.TryGetValue(progress.Id, out JobDefinition job))
            {
                return null;
            }

            if (job.Reward > 0 && !AddRp(data.Id, job.Reward))
            {
                return Lang("BarberExFailed", userId);
            }

            progress.Claimed = true;
            dataDirty = true;
            SendDebug("DebugJobPaid", data.Name, job.Id, UnitTxt(true, job.Reward));
            return Lang("BarberJobPaid", userId, UnitTxt(true, job.Reward));
        }

        // Price of the hair insurance for the player's alopecia now: max(minimum, alopecia / 1000 x surcharge), rounded up.
        private long InsurancePrice(PlayerData data)
        {
            InsuranceConfig insurance = config.Insurance;
            double price = Math.Ceiling(data.Baldness / 1000.0 * insurance.Surcharge);
            long whole = price >= long.MaxValue ? long.MaxValue : (long)price;
            return Math.Max(insurance.MinPrice, whole);
        }

        [ConsoleCommand("calvos.insurance")]
        private void CcmdInsurance(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            if (!IsRealPlayer(player) || !config.Insurance.Enabled || !RequireCalvarioNpc(player))
            {
                return;
            }

            OpenBarber(player, BarberPage.Insurance, BuyInsurance(player));
        }

        // Charges the price the player saw (or less, if their alopecia went down); if it went up, the new price is shown
        // instead. Returns the barber's line.
        private string BuyInsurance(BasePlayer player)
        {
            PlayerData data = GetOrCreateData(player);
            string userId = player.UserIDString;
            if (data.HasDeathShield)
            {
                return Lang("ItemShieldAlready", userId);
            }

            if (!insuranceQuotes.TryGetValue(data.Id, out long quoted))
            {
                return null;
            }

            long price = InsurancePrice(data);
            if (price > quoted)
            {
                return Lang("BarberInsuranceRepriced", userId);
            }

            if (!RpAvailable || CheckRp(data.Id) < price)
            {
                return Lang("BarberExNotEnough", userId);
            }

            if (!TakeRp(data.Id, price))
            {
                return Lang("BarberExFailed", userId);
            }

            insuranceQuotes.Remove(data.Id);
            data.HasDeathShield = true;
            dataDirty = true;
            SendDebug("DebugInsurance", data.Name, UnitTxt(true, price));
            return Lang("BarberInsuranceDone", userId, UnitTxt(true, price));
        }

        // The language flags of Padre Jano's /info menu call lang.SetLanguage and then this hook; Oxide.Rust's own
        // OnPlayerLanguageChanged comes when the game client switches language. The counter and the wallet are redrawn in the
        // new language right away; an open window (barber, /calvos) changes on its next click.
        private void OnIslaLanguageChanged(BasePlayer player, string code) => RedrawInNewLanguage(player);

        private void OnPlayerLanguageChanged(BasePlayer player, string code) => RedrawInNewLanguage(player);

        private void RedrawInNewLanguage(BasePlayer player)
        {
            if (!IsRealPlayer(player) || !player.IsConnected || player.IsSleeping())
            {
                return;
            }

            walletTexts.Remove((ulong)player.userID);
            DrawCounter(player);
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

        private bool TryGetNpcReward(string prefab, out int tier, out long reward, out Txt whyNot)
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

                whyNot = T("NoRewardNpcUnlisted", prefab);
                return false;
            }

            if (disabledNpcs.Contains(prefab))
            {
                whyNot = T("NoRewardNpcDisabled", prefab);
                return false;
            }

            if (!tierRewards.TryGetValue(tier, out reward))
            {
                whyNot = T("NoRewardTierMissing", prefab, tier);
                return false;
            }

            return true;
        }

        private bool IsKillRewardable(PlayerData killerData, ulong killerId, BasePlayer victim, string victimName)
        {
            // Killing sleepers or disconnected players is not glorious.
            if (victim.IsSleeping() || !victim.IsConnected)
            {
                DebugNoReward(killerData, T("NoRewardSleeperV2", victimName));
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
                DebugNoReward(killerData, T("NoRewardCooldown", victimName));
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
                bool movedNow = TrackMovement(player, data);
                data.SurvivalSeconds += SurvivalTickSeconds;
                dataDirty = true;

                if (data.SurvivalSeconds >= interval)
                {
                    data.SurvivalSeconds -= interval;
                    bool moved = data.SurvivalMoved;
                    data.SurvivalMoved = false;
                    if (config.SurvivalRequireMovement && !moved)
                    {
                        DebugNoReward(data, T("NoRpAfk"));
                    }
                    else
                    {
                        GainBaldness(data, config.SurvivalReward, T("ReasonSurvival"));
                    }
                }

                RewardPointsTick(player, data);

                // Survive jobs: a minute alive and moving adds to the streak; a still minute adds nothing and cuts nothing.
                if (movedNow && JobsActive)
                {
                    int streak = (surviveStreaks.TryGetValue(data.Id, out int minutes) ? minutes : 0) + 1;
                    surviveStreaks[data.Id] = streak;
                    AddJobProgress(player, JobType.Survive, streak, null, true);
                }
            }
        }

        // One position check per tick feeds the survival reward, the RP payout (anti-AFK) and the Survive jobs. True if the
        // player moved since the previous tick.
        private bool TrackMovement(BasePlayer player, PlayerData data)
        {
            Vector3 position = player.transform.position;
            bool moved = lastPositions.TryGetValue(data.Id, out Vector3 last) && Vector3.Distance(last, position) >= config.ServerRewards.MinMoveMeters;
            if (moved)
            {
                data.SurvivalMoved = true;
                data.RpMoved = true;
            }

            lastPositions[data.Id] = position;
            return moved;
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
                SendDebug("DebugNoRpV2", data.Name, T("NoRpAfk"));
                return;
            }

            if (ServerRewards == null || !ServerRewards.IsLoaded)
            {
                if (!warnedNoServerRewards)
                {
                    warnedNoServerRewards = true;
                    PrintWarning("Server Rewards is not loaded; no Puntos de Chola are being paid.");
                }

                SendDebug("DebugNoRpV2", data.Name, T("NoRpPlugin"));
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
                    SendDebug("DebugNoRpV2", data.Name, T("NoRpCapV2"));
                    return;
                }
            }

            if (!AddRp(data.Id, amount))
            {
                SendDebug("DebugNoRpV2", data.Name, T("NoRpRefused"));
                return;
            }

            SendDebug("DebugRpV2", data.Name, Num(amount), TitleTxt(GetTierIndex(data.Baldness)));
            if (rp.NotifyPlayer)
            {
                Reply(player, "RpEarnedV5", Num(amount), TitleTxt(GetTierIndex(data.Baldness)));
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

        private void ChangeBaldness(PlayerData data, long delta, bool announce, Txt reason)
        {
            long oldValue = data.Baldness;
            long newValue = Math.Max(MinBaldness, SaturatingAdd(oldValue, delta));

            SendDebug("DebugChange", data.Name, Num(oldValue), Num(newValue),
                delta >= 0 ? "+" : string.Empty, Num(delta), reason);

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
                    Broadcast("TitleUpV3", data.Name, TitleTxt(newTier));
                }

                if (config.Ui.ShowTitleUpBanner)
                {
                    ShowBanner(T("TitleUpBanner", data.Name, TitleUpperTxt(newTier)), config.Ui.TitleUpBannerSeconds);
                }
            }
            else if (newTier < oldTier)
            {
                if (config.AnnounceTitleDrop)
                {
                    Broadcast("TitleDropV2", data.Name, TitleTxt(newTier));
                }

                if (config.Ui.ShowTitleDropBanner)
                {
                    ShowBanner(T("TitleDropBanner", data.Name, TitleUpperTxt(newTier)), config.Ui.TitleUpBannerSeconds);
                }
            }

            if (newTitle > oldTitle)
            {
                // From -1 too, so the first title (Greñas Sucias) has its prize.
                PayTierPrizes(data, oldTitle, newTitle);
            }

            if (newTitle != oldTitle)
            {
                // For other plugins (JanoBridge). Empty title = none.
                Interface.CallHook("OnIslaTitleChanged", data.Id, data.Name ?? string.Empty, TitleName(oldTitle), TitleName(newTitle),
                    newTitle > oldTitle, newValue);
            }
        }

        // Pays each newly reached title's prize once, however the baldness came (bought baldness too, since 1.10.0).
        private void PayTierPrizes(PlayerData data, int oldTier, int newTier)
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

            BasePlayer player = BasePlayer.FindByID(data.Id);
            for (int tier = from; tier <= newTier; tier++)
            {
                if (!tierPrizes.TryGetValue(tier, out TierPrize prize) || prize.IsEmpty)
                {
                    continue;
                }

                var parts = new List<Txt>();
                var given = new List<string>();
                var spawned = new List<string>();
                int spawnFailed = GivePrize(data, player, prize, parts, given, spawned);
                SendDebug("DebugTierPrize", data.Name, TitleTxt(tier), PrizeDebugText(parts, given, spawned));
                if (player == null || !player.IsConnected)
                {
                    continue;
                }

                if (parts.Count > 0)
                {
                    Reply(player, "TierPrizeV2", TitleTxt(tier), JoinTxt(", ", parts));
                }
                else if (given.Count > 0)
                {
                    Reply(player, "TierPrizeItems", TitleTxt(tier));
                }

                ReplySpawnedPrize(player, spawned, spawnFailed);
                SendPrizeMessage(player, prize);
            }
        }

        // Puntos de Chola and pelones go by id (online or not); items and spawned prefabs only to an online player. Fills in
        // what was actually given (parts: Puntos de Chola and pelones; given: items; spawned: prefabs), for the messages, and
        // returns how many prefabs could not be spawned.
        private int GivePrize(PlayerData data, BasePlayer player, TierPrize prize, List<Txt> parts, List<string> given, List<string> spawned)
        {
            if (prize.Rp > 0 && AddRp(data.Id, prize.Rp)) parts.Add(UnitTxt(true, prize.Rp));
            if (prize.Coins > 0 && DepositCoins(data.Id, prize.Coins)) parts.Add(UnitTxt(false, prize.Coins));
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

            int failed = 0;
            if (prize.SpawnPrefabs != null)
            {
                foreach (string prefab in prize.SpawnPrefabs.Where(p => !string.IsNullOrEmpty(p)))
                {
                    if (player == null || !player.IsConnected)
                    {
                        // Prizes are paid when the title goes up, so the player is almost always online; if not, an admin decides.
                        PrintWarning($"Prize prefab '{prefab}' not spawned for {data.Name} ({data.Id}): the player is not connected.");
                        failed++;
                    }
                    else if (SpawnPrizePrefab(player, prefab))
                    {
                        spawned.Add(prefab.Substring(prefab.LastIndexOf('/') + 1));
                    }
                    else
                    {
                        failed++;
                    }
                }
            }

            return failed;
        }

        private static Txt PrizeDebugText(List<Txt> parts, List<string> given, List<string> spawned)
        {
            List<Txt> all = parts.Concat(given.Concat(spawned).Select(Plain)).ToList();
            return all.Count > 0 ? JoinTxt(", ", all) : Plain("-");
        }

        // The prize's own "Message", in the player's language when the config has it ("Message in other languages").
        private void SendPrizeMessage(BasePlayer player, TierPrize prize)
        {
            string message = ConfigText(prize.Message, prize.Messages, player.UserIDString);
            if (!string.IsNullOrEmpty(message))
            {
                SendChat(player, message);
            }
        }

        private void ReplySpawnedPrize(BasePlayer player, List<string> spawned, int failed)
        {
            if (spawned.Count > 0) Reply(player, "PrizeSpawned");
            if (failed > 0) Reply(player, "PrizeSpawnFailed");
        }

        // Ground, buildings, rocks and trees: the mask Rust itself uses to land hackable crates (HackableLockedCrate.LandCheck:
        // Default, Deployed, World, Construction, Terrain, Tree).
        private const int SpawnGroundMask = 1084293377;

        // Rust's own line-of-sight mask (the entity visibility check before OnEntityVisibilityCheck): also has the vehicle layers.
        private const int SpawnSightMask = 1218519041;

        // 3-4 m in front of the player first; further on if that spot is no good.
        private static readonly float[] SpawnDistances = { 4f, 7f, 10f, 14f, 18f };

        // Spawns the prefab in front of the player, facing where they face, on the ground. Owned by the player.
        private bool SpawnPrizePrefab(BasePlayer player, string prefab)
        {
            Vector3 forward = player.eyes.BodyForward();
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
            forward.Normalize();

            if (!TryFindSpawnSpot(player, forward, out Vector3 spot))
            {
                PrintWarning($"Prize prefab '{prefab}' not spawned for {player.displayName} ({(ulong)player.userID}): no free spot in front of them at {player.transform.position}.");
                return false;
            }

            BaseEntity entity = GameManager.server.CreateEntity(prefab, spot, Quaternion.LookRotation(forward));
            if (entity == null)
            {
                PrintWarning($"Prize prefab '{prefab}' does not exist in this Rust version; fix it in the config.");
                return false;
            }

            entity.OwnerID = (ulong)player.userID;
            entity.Spawn();
            return true;
        }

        // A spot on the ground in front of the player with room around it and nothing between them and it.
        private bool TryFindSpawnSpot(BasePlayer player, Vector3 forward, out Vector3 spot)
        {
            Vector3 feet = player.transform.position;
            Vector3 eyes = player.eyes.position;
            foreach (float distance in SpawnDistances)
            {
                // From a bit above the player's head straight down: the ground (or floor) ahead, not the roof over the player.
                Vector3 from = feet + forward * distance + Vector3.up * 3f;
                if (!Physics.Raycast(from, Vector3.down, out RaycastHit hit, 12f, SpawnGroundMask))
                {
                    continue;
                }

                // Below sea level means in the sea.
                if (hit.point.y < 0f)
                {
                    continue;
                }

                // Room for the helicopter: nothing (wall, rock, tree, ceiling, another vehicle) within 3 m of a point 3.5 m up.
                Vector3 centre = hit.point + Vector3.up * 3.5f;
                if (Physics.CheckSphere(centre, 3f, SpawnGroundMask | SpawnSightMask) || !GamePhysics.LineOfSight(eyes, centre, SpawnSightMask))
                {
                    continue;
                }

                spot = hit.point + Vector3.up * 0.3f;
                return true;
            }

            spot = Vector3.zero;
            return false;
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

        private void DebugNoReward(PlayerData data, Txt reason) => SendDebug("DebugNoRewardV2", data.Name, reason);

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

        // The title a player reads (1.13.0): lang key "Title_<minimum alopecia>" in their language. The config name only
        // when the lang has no key for that title (a title added or moved in the config).
        private string TitleText(int index, string userId)
        {
            if (index < 0 || index >= config.Titles.Count)
            {
                return string.Empty;
            }

            string key = "Title_" + config.Titles[index].MinBaldness.ToString(CultureInfo.InvariantCulture);
            string text = Message(key, userId);
            return string.IsNullOrEmpty(text) || text == key ? config.Titles[index].Name : text;
        }

        private Txt TitleTxt(int index) => new Txt(id => TitleText(index, id));

        // Same title in capitals, for the banners.
        private Txt TitleUpperTxt(int index) => new Txt(id => TitleText(index, id).ToUpperInvariant());

        private string GetTitle(long baldness, string userId) => TitleText(GetTierIndex(baldness), userId);

        // Unlike GetTierIndex, -1 below the first title: at 0 baldness a player holds no title.
        private int GetTitleIndex(long baldness) => baldness >= config.Titles[0].MinBaldness ? GetTierIndex(baldness) : -1;

        // The config name, for other plugins and the website (isla.ranking, isla.salon, OnIslaTitleChanged): always Spanish.
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
