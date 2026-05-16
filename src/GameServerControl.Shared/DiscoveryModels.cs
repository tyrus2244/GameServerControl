namespace GameServerControl.Shared;

/// <summary>
/// One installed dedicated server found by the discovery scan.
/// The client uses <see cref="PresetKey"/> to look up matching defaults
/// (start args, save dirs) when the user clicks "Add".
/// </summary>
public sealed record DiscoveredServer(
    string PresetKey,           // Matches GamePresets key. "windrose" / "valheim" / "satisfactory" / etc.
    string DisplayName,         // Human-readable game name. "Valheim"
    string SteamAppId,
    string InstallPath,         // Absolute path to install root.
    string ExePath,             // Absolute exe path inside install.
    string Source,              // Where we found it. "steam-library" / "gameservers-folder"
    bool AlreadyConfigured      // True if a servers.json entry already points at this install.
);

public sealed record DiscoverResponse(DiscoveredServer[] Servers, string[] LibrariesScanned);
