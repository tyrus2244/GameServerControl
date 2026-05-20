namespace GameServerControl.Shared;

/// <summary>One installed (or installable) server-side mod.</summary>
public sealed record ModInfo(
    string ModId,              // Unique within this server. Usually the filename or top-level folder name under the mods dir.
    string DisplayName,
    string? Version,           // Parsed from the mod manifest if available, else null.
    string? Source,            // URL the mod was downloaded from, or null if pre-existing/manual.
    DateTimeOffset? InstalledAt,
    long SizeBytes,
    string[]? Files);          // Paths (relative to install root) this mod owns. Used at uninstall.

public sealed record ModInstallRequest(string Url, string? DisplayName);

public sealed record ModInstallResult(bool Ok, ModInfo? Mod, string? Error);

/// <summary>
/// Server-mod-listing response. <see cref="Supported"/> = false when the agent has no
/// IModManager for this server's game (e.g. Satisfactory has no built-in mod loader the
/// agent supports yet). UI uses this to show "mod management not available" gracefully.
/// </summary>
public sealed record ModListResponse(
    ModInfo[] Mods,
    bool Supported,
    string? UnsupportedReason,
    string? ModsFolder);       // Where on disk mods live for this server, for the user's reference.
