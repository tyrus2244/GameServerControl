namespace GameServerControl.Shared;

public enum ConfigFieldType
{
    Text,
    Multiline,
    Integer,
    Decimal,
    Toggle,
    Choice,
    Password
}

public record ConfigField(
    string Key,
    string Label,
    ConfigFieldType Type,
    string? Description = null,
    string? Default = null,
    double? Min = null,
    double? Max = null,
    double? Step = null,
    string[]? Choices = null);

public record ConfigSection(string Title, ConfigField[] Fields);

public record ConfigSchema(string Key, string DisplayName, ConfigSection[] Sections)
{
    public IEnumerable<ConfigField> AllFields => Sections.SelectMany(s => s.Fields);
}

public static class ConfigSchemas
{
    public static ConfigSchema? For(ServerDef def) =>
        (def.GameType, def.SteamAppId) switch
        {
            (GameType.SteamGeneric, "896660") => Valheim,
            (GameType.SteamGeneric, "2394010") => Palworld,
            (GameType.SteamGeneric, "4129620") => Windrose,
            (GameType.SteamGeneric, "1690800") => Satisfactory,
            (GameType.Windrose, _) => Windrose,
            _ => null
        };

    public static readonly ConfigSchema Satisfactory = new("satisfactory", "Satisfactory", new[]
    {
        new ConfigSection("Server identity (live via Admin API)", new[]
        {
            new ConfigField("ServerName", "Server name", ConfigFieldType.Text,
                Description: "Shown in the server browser. Changes apply immediately, no restart."),
            new ConfigField("ClientPassword", "Join password", ConfigFieldType.Password,
                Description: "Required for players to connect. Empty = no password."),
            new ConfigField("ActiveSessionName", "Session ID (read-only)", ConfigFieldType.Text,
                Description: "Name of the currently loaded save / session. This is what players see in the server browser — the Satisfactory equivalent of Windrose's invite code. Changes when you load a different save in-game; otherwise tracks Server name."),
        }),
        new ConfigSection("Server limits", new[]
        {
            new ConfigField("MaxPlayers", "Max players", ConfigFieldType.Integer,
                Description: "Engine.ini → [/Script/Engine.GameSession]. Restart required.",
                Default: "4", Min: 1, Max: 64),
            new ConfigField("AutoLoadSessionName", "Auto-load save on boot", ConfigFieldType.Text,
                Description: "Name of the save to auto-load. Empty = none (server boots to claim screen)."),
        }),
        new ConfigSection("Behavior", new[]
        {
            new ConfigField("mAutoPauseServerOnEmpty", "Auto-pause when empty", ConfigFieldType.Toggle,
                Description: "Server pauses world simulation when no players are online. Saves CPU.",
                Default: "true"),
            new ConfigField("mAutoSaveOnDisconnect", "Auto-save on disconnect", ConfigFieldType.Toggle,
                Description: "Save automatically when the last player disconnects.",
                Default: "true"),
            new ConfigField("mServerRestartTimeSlot", "Scheduled restart interval (min)", ConfigFieldType.Integer,
                Description: "Minutes between automatic restarts. 0 = disabled.",
                Default: "0", Min: 0, Max: 1440),
        }),
        new ConfigSection("Network quality", new[]
        {
            new ConfigField("ConfiguredInternetSpeed", "Per-client bandwidth cap (B/s)", ConfigFieldType.Integer,
                Description: "Max bytes/sec per remote client. Default 120000 (≈120 KB/s). Bump higher only if you're hitting bandwidth ceilings and your upload supports it.",
                Default: "120000", Min: 10000, Max: 10000000),
        }),
        new ConfigSection("Advanced Game Settings (live via Admin API)", new[]
        {
            new ConfigField("AGS.NoPower", "No power required", ConfigFieldType.Toggle,
                Description: "Buildings work without electricity. Enabling AGS on a save permanently disables Steam achievements for that save."),
            new ConfigField("AGS.NoFuelCost", "No fuel cost", ConfigFieldType.Toggle,
                Description: "Vehicles and generators run without consuming fuel."),
            new ConfigField("AGS.NoBuildCost", "No build cost", ConfigFieldType.Toggle,
                Description: "Buildings cost no materials."),
            new ConfigField("AGS.NoUnlockCost", "Free unlocks", ConfigFieldType.Toggle,
                Description: "Hub upgrades and milestones cost nothing."),
            new ConfigField("AGS.GodMode", "God mode", ConfigFieldType.Toggle,
                Description: "Players take no damage."),
            new ConfigField("AGS.FlightMode", "Flight mode", ConfigFieldType.Toggle,
                Description: "Players can fly (no jetpack needed)."),
            new ConfigField("AGS.StartingTier", "Starting tier", ConfigFieldType.Integer,
                Description: "Tech tier players spawn with already unlocked. 0 = none (start at tier 1 milestones).",
                Default: "0", Min: 0, Max: 9),
            new ConfigField("AGS.SetGamePhase", "Force game phase", ConfigFieldType.Integer,
                Description: "Skip the project-assembly grind. 0 = phase 1, 5 = elevator complete.",
                Default: "0", Min: 0, Max: 5),
            new ConfigField("AGS.GiveAllTiers", "Unlock all schematic tiers", ConfigFieldType.Toggle,
                Description: "Players spawn with every tier unlocked. Skip the entire tech progression."),
            new ConfigField("AGS.UnlockAllResearchSchematics", "Unlock all MAM research", ConfigFieldType.Toggle),
            new ConfigField("AGS.UnlockAllResourceSinkSchematics", "Unlock all AWESOME shop", ConfigFieldType.Toggle),
            new ConfigField("AGS.UnlockInstantAltRecipes", "Instant alt recipes", ConfigFieldType.Toggle,
                Description: "Alt recipes unlock immediately; no Hard Drive scan needed."),
            new ConfigField("AGS.DisableArachnidCreatures", "Disable arachnid creatures", ConfigFieldType.Toggle,
                Description: "Replaces spider-like enemies with non-arachnid variants (server-side arachnophobia mode)."),
        })
    });

    public static readonly ConfigSchema Valheim = new("valheim", "Valheim", new[]
    {
        new ConfigSection("Server identity", new[]
        {
            new ConfigField("name", "Server name", ConfigFieldType.Text, "Shown in the Valheim server browser."),
            new ConfigField("password", "Password", ConfigFieldType.Password, "Anyone joining types this. Required for non-public servers."),
            new ConfigField("port", "Port", ConfigFieldType.Integer, "Default 2456. Valheim uses port and port+1 and port+2.", Default: "2456", Min: 1024, Max: 65535),
            new ConfigField("public", "Listed publicly", ConfigFieldType.Choice, Choices: new[] { "1", "0" }, Default: "0",
                Description: "1 = appears in the server browser. 0 = friends-only via direct join."),
            new ConfigField("crossplay", "Crossplay enabled", ConfigFieldType.Toggle,
                Description: "Allows Xbox/PS5 cross-play. Adds the -crossplay flag."),
        }),
        new ConfigSection("World", new[]
        {
            new ConfigField("world", "World name", ConfigFieldType.Text, "File name (without extension) for the world save."),
            new ConfigField("savedir", "Save directory (in guest)", ConfigFieldType.Text,
                Description: "Override where saves live. Empty = engine default (LocalLow\\IronGate\\Valheim)."),
            new ConfigField("saveinterval", "Save interval (sec)", ConfigFieldType.Integer, Default: "1800", Min: 60, Max: 14400),
            new ConfigField("backups", "Number of backups to retain", ConfigFieldType.Integer, Default: "4", Min: 0, Max: 50),
        }),
        new ConfigSection("World rules", new[]
        {
            new ConfigField("preset", "World preset", ConfigFieldType.Choice,
                Description: "Bundle of default settings. 'Normal' is unchanged; 'Hammer' is creative mode.",
                Choices: new[] { "", "Normal", "Casual", "Easy", "Hard", "Hardcore", "Immersive", "Hammer" }, Default: ""),
            new ConfigField("modifier:combat", "Combat difficulty", ConfigFieldType.Choice,
                Choices: new[] { "", "VeryEasy", "Easy", "Hard", "VeryHard" }, Default: ""),
            new ConfigField("modifier:deathpenalty", "Death penalty", ConfigFieldType.Choice,
                Choices: new[] { "", "CasualMode", "VeryEasy", "Easy", "Hard", "Hardcore" }, Default: ""),
            new ConfigField("modifier:resources", "Resource rate", ConfigFieldType.Choice,
                Choices: new[] { "", "Muchless", "Less", "More", "MuchMore", "Most" }, Default: ""),
            new ConfigField("modifier:raids", "Raid frequency", ConfigFieldType.Choice,
                Choices: new[] { "", "None", "MuchLess", "Less", "More", "MuchMore" }, Default: ""),
            new ConfigField("modifier:portals", "Portal rules", ConfigFieldType.Choice,
                Choices: new[] { "", "CasualPortals", "HardPortals", "VeryHardPortals" }, Default: ""),
            new ConfigField("nobuildcost", "No build cost", ConfigFieldType.Toggle, "Build for free."),
            new ConfigField("playerevents", "Player-triggered events", ConfigFieldType.Toggle),
            new ConfigField("passivemobs", "Passive mobs", ConfigFieldType.Toggle, "Hostile mobs ignore players."),
            new ConfigField("nomap", "No map (hardcore explorer)", ConfigFieldType.Toggle),
        })
    });

    public static readonly ConfigSchema Palworld = new("palworld", "Palworld", new[]
    {
        new ConfigSection("Server identity", new[]
        {
            new ConfigField("ServerName", "Server name", ConfigFieldType.Text, Default: "Default Palworld Server"),
            new ConfigField("ServerDescription", "Description", ConfigFieldType.Multiline),
            new ConfigField("ServerPassword", "Player password", ConfigFieldType.Password),
            new ConfigField("AdminPassword", "Admin password", ConfigFieldType.Password,
                Description: "Required to use admin commands in-game."),
            new ConfigField("PublicPort", "Public port", ConfigFieldType.Integer, Default: "8211", Min: 1024, Max: 65535),
            new ConfigField("PublicIP", "Public IP override", ConfigFieldType.Text, "Leave empty to auto-detect."),
            new ConfigField("ServerPlayerMaxNum", "Max players", ConfigFieldType.Integer, Default: "32", Min: 1, Max: 32),
            new ConfigField("Region", "Region tag", ConfigFieldType.Text, Default: ""),
        }),
        new ConfigSection("RCON (live admin)", new[]
        {
            new ConfigField("RCONEnabled", "Enable RCON", ConfigFieldType.Toggle, "Required for the GUI's live admin pane (Phase 2)."),
            new ConfigField("RCONPort", "RCON port", ConfigFieldType.Integer, Default: "25575", Min: 1024, Max: 65535),
        }),
        new ConfigSection("Game mode", new[]
        {
            new ConfigField("Difficulty", "Difficulty", ConfigFieldType.Choice,
                Choices: new[] { "None", "Casual", "Normal", "Hard" }, Default: "None"),
            new ConfigField("bIsPvP", "PvP enabled", ConfigFieldType.Toggle,
                Description: "Allow player-vs-player damage."),
            new ConfigField("bHardcore", "Hardcore mode", ConfigFieldType.Toggle,
                Description: "Permanent death for players."),
            new ConfigField("bPalLost", "Lose pals on death", ConfigFieldType.Toggle,
                Description: "Death drops pals; otherwise pals stay in inventory."),
            new ConfigField("bEnableFastTravel", "Allow fast travel", ConfigFieldType.Toggle, Default: "true"),
            new ConfigField("bExistPlayerAfterLogout", "Body persists after logout", ConfigFieldType.Toggle,
                Description: "Player body remains in world when offline (raidable)."),
            new ConfigField("bEnableInvaderEnemy", "Enemy raids on bases", ConfigFieldType.Toggle, Default: "true"),
            new ConfigField("bShowPlayerList", "Show player list", ConfigFieldType.Toggle, Default: "true"),
            new ConfigField("bEnableNonLoginPenalty", "Penalty for not logging in", ConfigFieldType.Toggle, Default: "true"),
            new ConfigField("DeathPenalty", "Death penalty", ConfigFieldType.Choice,
                Choices: new[] { "None", "Item", "ItemAndEquipment", "All" }, Default: "All"),
        }),
        new ConfigSection("World rates", new[]
        {
            new ConfigField("ExpRate", "XP rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 20.0, Step: 0.1),
            new ConfigField("DayTimeSpeedRate", "Day length multiplier", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 5.0, Step: 0.1,
                Description: "Lower = longer day."),
            new ConfigField("NightTimeSpeedRate", "Night length multiplier", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 5.0, Step: 0.1),
            new ConfigField("WorkSpeedRate", "Work speed", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 20.0, Step: 0.1),
            new ConfigField("CollectionDropRate", "Gather rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1,
                Description: "Resource drop rate from chopping/mining."),
            new ConfigField("CollectionObjectHpRate", "Resource node HP", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("CollectionObjectRespawnSpeedRate", "Resource respawn speed", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
        }),
        new ConfigSection("Pals", new[]
        {
            new ConfigField("PalCaptureRate", "Pal capture rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PalSpawnNumRate", "Pal spawn rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PalDamageRateAttack", "Pal attack damage", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PalDamageRateDefense", "Pal defense", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("EnemyDropItemRate", "Enemy drop rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PalAutoHPRegeneRate", "Pal HP regen", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1,
                Description: "Pal passive HP regen multiplier (awake)."),
            new ConfigField("PalAutoHpRegeneRateInSleep", "Pal HP regen (sleeping)", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PalEggDefaultHatchingTime", "Egg hatching time (sec)", ConfigFieldType.Decimal, Default: "72.0", Min: 1.0, Max: 10000.0, Step: 1.0),
        }),
        new ConfigSection("Players", new[]
        {
            new ConfigField("PlayerDamageRateAttack", "Player damage", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PlayerDamageRateDefense", "Player defense", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PlayerStomachDecreaceRate", "Hunger decay rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PlayerStaminaDecreaceRate", "Stamina decay rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PlayerAutoHPRegeneRate", "Player HP regen", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
            new ConfigField("PlayerAutoHpRegeneRateInSleep", "Player HP regen (sleeping)", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1),
        }),
        new ConfigSection("Base & building", new[]
        {
            new ConfigField("BuildObjectDamageRate", "Building damage taken", ConfigFieldType.Decimal, Default: "1.0", Min: 0.0, Max: 10.0, Step: 0.1,
                Description: "How much damage buildings take from attacks. 0 = invulnerable."),
            new ConfigField("BuildObjectDeteriorationDamageRate", "Building decay rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.0, Max: 10.0, Step: 0.1,
                Description: "Passive structure decay over time. 0 = no decay."),
            new ConfigField("BaseCampMaxNum", "Max bases per guild", ConfigFieldType.Integer, Default: "128", Min: 1, Max: 10000),
            new ConfigField("BaseCampWorkerMaxNum", "Max workers per base", ConfigFieldType.Integer, Default: "15", Min: 1, Max: 50),
        }),
        new ConfigSection("Items & inventory", new[]
        {
            new ConfigField("DropItemMaxNum", "Max dropped items on ground", ConfigFieldType.Integer, Default: "3000", Min: 0, Max: 10000),
            new ConfigField("DropItemAliveMaxHours", "Dropped item lifetime (hours)", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 168.0, Step: 0.1),
            new ConfigField("bDropItemAtDeath", "Drop items on death", ConfigFieldType.Toggle, Default: "true"),
            new ConfigField("EquipmentDurabilityDamageRate", "Durability loss rate", ConfigFieldType.Decimal, Default: "1.0", Min: 0.0, Max: 10.0, Step: 0.1,
                Description: "How fast gear wears down. 0 = unbreakable."),
            new ConfigField("ItemWeightRate", "Item weight multiplier", ConfigFieldType.Decimal, Default: "1.0", Min: 0.1, Max: 10.0, Step: 0.1,
                Description: "Lower = carry more before encumbered."),
        }),
        new ConfigSection("Guilds", new[]
        {
            new ConfigField("GuildPlayerMaxNum", "Max guild members", ConfigFieldType.Integer, Default: "20", Min: 1, Max: 100),
            new ConfigField("bAutoResetGuildNoOnlinePlayers", "Auto-reset inactive guilds", ConfigFieldType.Toggle),
            new ConfigField("AutoResetGuildTimeNoOnlinePlayers", "Inactive timeout (hours)", ConfigFieldType.Decimal, Default: "72.0", Min: 1.0, Max: 720.0, Step: 1.0),
        }),
    });

    public static readonly ConfigSchema Windrose = new("windrose", "Windrose", new[]
    {
        new ConfigSection("Server identity", new[]
        {
            new ConfigField("ServerName", "Server name", ConfigFieldType.Text, "Shown to friends joining via invite code."),
            new ConfigField("MaxPlayerCount", "Max players", ConfigFieldType.Integer, Default: "4", Min: 1, Max: 32),
        }),
        new ConfigSection("Access", new[]
        {
            new ConfigField("IsPasswordProtected", "Password protected", ConfigFieldType.Toggle,
                Description: "When enabled, players must enter the password below."),
            new ConfigField("Password", "Password", ConfigFieldType.Password,
                Description: "Required when password protection is enabled."),
            new ConfigField("InviteCode", "Invite code (read-only)", ConfigFieldType.Text,
                Description: "Generated by the game. Share this with friends so they can join."),
        }),
        new ConfigSection("World rules", new[]
        {
            new ConfigField("WorldPresetType", "World preset", ConfigFieldType.Choice,
                Description: "Bundle of difficulty settings applied to the loaded world. 'Custom' lets the individual sliders below take effect — otherwise the engine forces preset defaults on next start.",
                Choices: new[] { "Easy", "Medium", "Hard", "Custom" }, Default: "Medium"),
            new ConfigField("MobHealthMultiplier", "Mob health", ConfigFieldType.Decimal,
                Description: "Enemy HP multiplier. Only takes effect when preset = Custom.",
                Default: "1.0", Min: 0.2, Max: 5.0, Step: 0.1),
            new ConfigField("MobDamageMultiplier", "Mob damage", ConfigFieldType.Decimal,
                Description: "Enemy damage multiplier. Custom-only.",
                Default: "1.0", Min: 0.2, Max: 5.0, Step: 0.1),
            new ConfigField("ShipHealthMultiplier", "Enemy ship HP", ConfigFieldType.Decimal,
                Default: "1.0", Min: 0.4, Max: 5.0, Step: 0.1),
            new ConfigField("ShipDamageMultiplier", "Enemy ship damage", ConfigFieldType.Decimal,
                Default: "1.0", Min: 0.2, Max: 2.5, Step: 0.1),
            new ConfigField("BoardingDifficultyMultiplier", "Boarding difficulty", ConfigFieldType.Decimal,
                Description: "How many enemy sailors must be defeated to win a boarding action.",
                Default: "1.0", Min: 0.2, Max: 5.0, Step: 0.1),
            new ConfigField("CombatDifficulty", "Combat difficulty", ConfigFieldType.Choice,
                Description: "Boss aggression and behavior. Custom-only.",
                Choices: new[] { "Easy", "Normal", "Hard" }, Default: "Normal"),
            new ConfigField("CoopQuests", "Shared co-op quests", ConfigFieldType.Toggle,
                Description: "When one player completes a co-op quest, it completes for everyone with the quest active.",
                Default: "true"),
            new ConfigField("EasyExplore", "Immersive exploration", ConfigFieldType.Toggle,
                Description: "Disables points-of-interest markers on the map. Makes exploration harder.",
                Default: "false"),
            new ConfigField("Coop_StatsCorrectionModifier", "Co-op enemy scaling", ConfigFieldType.Decimal,
                Description: "Adjusts enemy HP and Posture-loss rate by player count. 0 = no scaling, 1 = full vanilla scaling.",
                Default: "1.0", Min: 0.0, Max: 2.0, Step: 0.1),
            new ConfigField("Coop_ShipStatsCorrectionModifier", "Co-op enemy-ship scaling", ConfigFieldType.Decimal,
                Description: "Adjusts enemy Ship HP by player count. Default 0 = no scaling.",
                Default: "0.0", Min: 0.0, Max: 2.0, Step: 0.1),
        })
    });
}
