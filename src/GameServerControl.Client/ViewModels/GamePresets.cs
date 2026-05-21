using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed record GamePreset(
    string Key,
    string Label,
    GameType GameType,
    string? SteamAppId,
    string DefaultExeRelative,
    string DefaultWorkingDir,
    string[] DefaultStartArgs,
    string[] DefaultSaveDirs);

public static class GamePresets
{
    public static readonly GamePreset[] All =
    {
        new("custom",   "Custom / blank",        GameType.Custom,        null,       "",                                 "",                            Array.Empty<string>(), Array.Empty<string>()),

        new("windrose", "Windrose",              GameType.Windrose,      "4129620",  "WindroseServer.exe",               @"C:\WindroseServer",          Array.Empty<string>(),
            new[]{ @"C:\WindroseServer\Saves" }),

        new("valheim",  "Valheim",               GameType.SteamGeneric,  "896660",   "valheim_server.exe",               @"C:\ValheimServer",
            new[]{ "-nographics", "-batchmode", "-name", "MyServer", "-port", "2456", "-world", "MyWorld", "-password", "secret" },
            new[]{ @"C:\Users\Administrator\AppData\LocalLow\IronGate\Valheim" }),

        new("ark-asa",  "ARK: Survival Ascended",GameType.SteamGeneric,  "2430930",  @"ShooterGame\Binaries\Win64\ArkAscendedServer.exe", @"C:\ArkAscendedServer",
            new[]{ "TheIsland_WP?listen?MaxPlayers=20?ServerPassword=?ServerAdminPassword=changeme" },
            new[]{ @"C:\ArkAscendedServer\ShooterGame\Saved" }),

        new("ark-se",   "ARK: Survival Evolved", GameType.SteamGeneric,  "376030",   @"ShooterGame\Binaries\Win64\ShooterGameServer.exe", @"C:\ArkServer",
            new[]{ "TheIsland?listen?MaxPlayers=20?ServerAdminPassword=changeme", "-server", "-log" },
            new[]{ @"C:\ArkServer\ShooterGame\Saved" }),

        new("palworld", "Palworld",              GameType.SteamGeneric,  "2394010",  @"PalServer\PalServer.exe",         @"C:\PalServer",
            new[]{ "-useperfthreads", "-NoAsyncLoadingThread", "-UseMultithreadForDS", "EpicApp=PalServer" },
            new[]{ @"C:\PalServer\Pal\Saved" }),

        new("satisfactory","Satisfactory",       GameType.SteamGeneric,  "1690800",  @"FactoryServer.exe",               @"C:\SatisfactoryServer",
            new[]{ "-log", "-unattended" },
            new[]{ @"C:\Users\Administrator\AppData\Local\FactoryGame\Saved\SaveGames" }),

        new("zomboid",  "Project Zomboid",       GameType.SteamGeneric,  "380870",   @"StartServer64.bat",               @"C:\ZomboidServer",
            Array.Empty<string>(),
            new[]{ @"C:\Users\Administrator\Zomboid" }),

        new("vrising", "V Rising",               GameType.SteamGeneric,  "1829350",  @"VRisingServer.exe",               @"C:\VRisingServer",
            new[]{ "-persistentDataPath", @".\save-data", "-serverName", "My V Rising Server", "-saveName", "world1", "-logFile", @".\logs\VRisingServer.log" },
            new[]{ @"C:\VRisingServer\save-data" }),

        new("rust",     "Rust",                  GameType.SteamGeneric,  "258550",   @"RustDedicated.exe",               @"C:\RustServer",
            new[]{ "-batchmode", "+server.port", "28015", "+rcon.port", "28016", "+rcon.password", "changeme", "+server.hostname", "My Rust Server" },
            new[]{ @"C:\RustServer\server" }),

        new("7dtd",     "7 Days to Die",         GameType.SteamGeneric,  "294420",   @"7DaysToDieServer.exe",            @"C:\7DaysServer",
            new[]{ "-quit", "-batchmode", "-nographics", "-configfile=serverconfig.xml" },
            new[]{ @"C:\Users\Administrator\AppData\Roaming\7DaysToDie\Saves" }),

        new("terraria", "Terraria",              GameType.SteamGeneric,  "105600",   @"TerrariaServer.exe",              @"C:\TerrariaServer",
            new[]{ "-config", "serverconfig.txt" },
            new[]{ @"C:\Users\Administrator\Documents\My Games\Terraria\Worlds" }),

        new("dst",      "Don't Starve Together", GameType.SteamGeneric,  "343050",   @"bin\dontstarve_dedicated_server_nullrenderer.exe", @"C:\DSTServer",
            new[]{ "-console", "-cluster", "MyCluster", "-shard", "Master" },
            new[]{ @"C:\Users\Administrator\Documents\Klei\DoNotStarveTogether" }),

        new("minecraft","Minecraft (Java)",      GameType.Minecraft,     null,       @"server.jar",                      @"C:\MinecraftServer",
            new[]{ "java", "-Xmx4G", "-Xms4G", "-jar", "server.jar", "nogui" },
            new[]{ @"C:\MinecraftServer\world", @"C:\MinecraftServer\world_nether", @"C:\MinecraftServer\world_the_end" }),
    };

    public static GamePreset? FindByKey(string key)
        => All.FirstOrDefault(p => p.Key == key);
}
