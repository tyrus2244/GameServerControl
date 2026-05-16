using System.Runtime.Versioning;
using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Discovery;

/// <summary>
/// Scans the local host for installed dedicated servers and returns ones that match
/// a known preset. Two sources:
///
///   1. Steam libraries — for users who install via the Steam client. We read
///      libraryfolders.vdf + appmanifest_*.acf.
///   2. SteamCMD common paths (C:\GameServers\*) — for users who installed via
///      steamcmd anonymously, the way GAMINGSERVER does Windrose and Satisfactory.
///
/// Matching is done against a hard-coded table keyed by Steam app ID. The set of
/// known games matches <c>GamePresets.cs</c> on the client; the agent only needs
/// (preset key, display name, relative exe path) to identify a real install.
/// </summary>
[SupportedOSPlatform("windows")]
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

    // Keep this in sync with GamePresets.cs on the client side.
    // Entries here are the SUBSET that ship a public dedicated server we can detect.
    private sealed record KnownGame(
        string PresetKey,
        string DisplayName,
        string AppId,
        string ExeRelative);

    private static readonly KnownGame[] KnownGames =
    {
        new("windrose",      "Windrose",                 "4129620",  @"WindroseServer.exe"),
        new("valheim",       "Valheim",                  "896660",   "valheim_server.exe"),
        new("satisfactory",  "Satisfactory",             "1690800",  "FactoryServer.exe"),
        new("palworld",      "Palworld",                 "2394010",  @"PalServer.exe"),
        new("ark-asa",       "ARK: Survival Ascended",   "2430930",  @"ShooterGame\Binaries\Win64\ArkAscendedServer.exe"),
        new("ark-se",        "ARK: Survival Evolved",    "376030",   @"ShooterGame\Binaries\Win64\ShooterGameServer.exe"),
        new("rust",          "Rust",                     "258550",   "RustDedicated.exe"),
        new("zomboid",       "Project Zomboid",          "380870",   "StartServer64.bat"),
        new("7dtd",          "7 Days to Die",            "294420",   "7DaysToDieServer.exe"),
        new("terraria",      "Terraria",                 "105600",   "TerrariaServer.exe"),
        new("dst",           "Don't Starve Together",    "343050",   @"bin\dontstarve_dedicated_server_nullrenderer.exe"),
    };

    /// <summary>
    /// Runs the discovery scan and returns matches plus the list of libraries scanned (for UI display).
    /// Cheap to call repeatedly — pure filesystem reads.
    /// </summary>
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

        // ---- 1) Steam library scan ----
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
                    var exePath = Path.Combine(installPath, game.ExeRelative);
                    if (!File.Exists(exePath))
                    {
                        _logger.LogDebug("Found {App} in Steam library but exe missing at {Exe}", game.DisplayName, exePath);
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

        // ---- 2) SteamCMD common paths (C:\GameServers\*) ----
        foreach (var root in CommonSteamCmdRoots())
        {
            if (!Directory.Exists(root)) continue;
            librariesScanned.Add(root);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                // Look for any known game's exe inside this folder. We don't have an appmanifest
                // here so we match by exe path. First hit wins.
                foreach (var g in KnownGames)
                {
                    var exePath = Path.Combine(dir, g.ExeRelative);
                    if (!File.Exists(exePath)) continue;
                    // Skip if we already added this exact install path from Steam scan
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

    private static IEnumerable<string> CommonSteamCmdRoots() => new[]
    {
        @"C:\GameServers",
        @"D:\GameServers",
        @"C:\Servers",
        @"D:\Servers",
    };

    private static string NormalizePath(string p)
    {
        if (string.IsNullOrEmpty(p)) return "";
        try { return Path.GetFullPath(p).TrimEnd('\\', '/'); }
        catch { return p.TrimEnd('\\', '/'); }
    }
}
