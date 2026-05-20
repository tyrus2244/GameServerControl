using System.Runtime.InteropServices;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Discovery;

/// <summary>
/// Scans the local host for installed dedicated servers and returns ones that match
/// a known preset. Cross-platform:
///
///   1. Steam libraries — Windows or Linux, via libraryfolders.vdf + appmanifest_*.acf.
///   2. Common bare-metal install roots — C:\GameServers\* on Windows;
///      ~/gameservers, /srv/gameservers, /opt/gameservers on Linux.
///
/// Per-game exe paths differ between platforms (e.g. FactoryServer.sh vs FactoryServer.exe),
/// so the KnownGames table carries both. Whichever exists wins.
/// </summary>
public sealed class ServerDiscoveryService
{
    private readonly ILogger<ServerDiscoveryService> _logger;
    private readonly SteamLibraryReader _steam;
    private readonly ServerRegistry _registry;

    public ServerDiscoveryService(
        ILogger<ServerDiscoveryService> logger,
        SteamLibraryReader steam,
        ServerRegistry registry)
    {
        _logger = logger;
        _steam = steam;
        _registry = registry;
    }

    private sealed record KnownGame(
        string PresetKey,
        string DisplayName,
        string AppId,
        string ExeRelativeWindows,
        string ExeRelativeLinux);

    // Linux-side exes for games whose dedicated server ships on Linux. For Windows-only
    // games we still set the Linux field — it just won't ever match an install on disk.
    private static readonly KnownGame[] KnownGames =
    {
        new("windrose",      "Windrose",                 "4129620",  @"WindroseServer.exe",                                       "WindroseServer.sh"),
        new("valheim",       "Valheim",                  "896660",   "valheim_server.exe",                                        "start_server_xterm.sh"),
        new("satisfactory",  "Satisfactory",             "1690800",  "FactoryServer.exe",                                         "FactoryServer.sh"),
        new("palworld",      "Palworld",                 "2394010",  @"PalServer.exe",                                            "PalServer.sh"),
        new("ark-asa",       "ARK: Survival Ascended",   "2430930",  @"ShooterGame\Binaries\Win64\ArkAscendedServer.exe",         "ShooterGame/Binaries/Linux/ArkAscendedServer"),
        new("ark-se",        "ARK: Survival Evolved",    "376030",   @"ShooterGame\Binaries\Win64\ShooterGameServer.exe",         "ShooterGame/Binaries/Linux/ShooterGameServer"),
        new("rust",          "Rust",                     "258550",   "RustDedicated.exe",                                         "RustDedicated"),
        new("zomboid",       "Project Zomboid",          "380870",   "StartServer64.bat",                                         "start-server.sh"),
        new("7dtd",          "7 Days to Die",            "294420",   "7DaysToDieServer.exe",                                      "startserver.sh"),
        new("terraria",      "Terraria",                 "105600",   "TerrariaServer.exe",                                        "TerrariaServer.bin.x86_64"),
        new("dst",           "Don't Starve Together",    "343050",   @"bin\dontstarve_dedicated_server_nullrenderer.exe",         "bin64/dontstarve_dedicated_server_nullrenderer"),
    };

    private static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    private static string ExeRel(KnownGame g) => IsWindows() ? g.ExeRelativeWindows : g.ExeRelativeLinux;

    /// <summary>Cheap, pure-filesystem scan. Safe to call as often as needed.</summary>
    public DiscoverResponse Discover()
    {
        var byAppId = KnownGames.ToDictionary(g => g.AppId, g => g);
        var existingInstalls = new HashSet<string>(
            _registry.All
                .Select(s => NormalizePath(s.GuestWorkingDir))
                .Where(p => !string.IsNullOrEmpty(p)),
            StringComparer.OrdinalIgnoreCase);

        var found = new List<DiscoveredServer>();
        var librariesScanned = new List<string>();

        // 1) Steam library scan
        var steamPath = _steam.FindSteamInstallPath();
        if (steamPath is not null)
        {
            foreach (var lib in _steam.EnumerateLibraryFolders(steamPath))
            {
                librariesScanned.Add(lib);
                var installed = _steam.EnumerateInstalledApps(lib);
                foreach (var (appId, installDir) in installed)
                {
                    if (!byAppId.TryGetValue(appId, out var game)) continue;
                    var installPath = Path.Combine(lib, "steamapps", "common", installDir);
                    var exePath = Path.Combine(installPath, ExeRel(game));
                    if (!File.Exists(exePath))
                    {
                        _logger.LogDebug("{App} listed in Steam but exe missing at {Exe}", game.DisplayName, exePath);
                        continue;
                    }
                    found.Add(new DiscoveredServer(
                        PresetKey: game.PresetKey,
                        DisplayName: game.DisplayName,
                        SteamAppId: game.AppId,
                        InstallPath: installPath,
                        ExePath: exePath,
                        Source: "steam-library",
                        AlreadyConfigured: existingInstalls.Contains(NormalizePath(installPath))));
                }
            }
        }
        else
        {
            _logger.LogDebug("No Steam install detected; skipping Steam library scan.");
        }

        // 2) Common bare-metal / SteamCMD install roots
        foreach (var root in CommonInstallRoots())
        {
            if (!Directory.Exists(root)) continue;
            librariesScanned.Add(root);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                foreach (var g in KnownGames)
                {
                    var exePath = Path.Combine(dir, ExeRel(g));
                    if (!File.Exists(exePath)) continue;
                    if (found.Any(f => string.Equals(NormalizePath(f.InstallPath), NormalizePath(dir), StringComparison.OrdinalIgnoreCase)))
                        break;
                    found.Add(new DiscoveredServer(
                        PresetKey: g.PresetKey,
                        DisplayName: g.DisplayName,
                        SteamAppId: g.AppId,
                        InstallPath: dir,
                        ExePath: exePath,
                        Source: "gameservers-folder",
                        AlreadyConfigured: existingInstalls.Contains(NormalizePath(dir))));
                    break;
                }
            }
        }

        return new DiscoverResponse(found.ToArray(), librariesScanned.ToArray());
    }

    private static IEnumerable<string> CommonInstallRoots()
    {
        if (IsWindows())
        {
            return new[]
            {
                @"C:\GameServers",
                @"D:\GameServers",
                @"C:\Servers",
                @"D:\Servers",
            };
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var paths = new List<string>
        {
            Path.Combine(home, "gameservers"),
            Path.Combine(home, "GameServers"),
            "/srv/gameservers",
            "/opt/gameservers",
            "/var/lib/gameservers",
        };
        // macOS conventions — /usr/local/var is the Homebrew-friendly spot for variable data,
        // and ~/Applications/GameServers covers users who keep dedicated servers in their home tree.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            paths.Add(Path.Combine(home, "Applications", "GameServers"));
            paths.Add("/usr/local/var/gameservers");
            paths.Add("/opt/homebrew/var/gameservers");
        }
        return paths;
    }

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p.TrimEnd('\\', '/'); }
    }
}
