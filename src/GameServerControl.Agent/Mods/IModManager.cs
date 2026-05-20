using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Per-game server-side mod manager. Each game with a mod ecosystem (Valheim/BepInEx,
/// Satisfactory/SML, ARK/Workshop, …) implements this. The shipped MVP only does Valheim.
///
/// All operations are "best effort" — mod loaders are messy and zip layouts vary. We
/// favor predictability over completeness: install a Thunderstore-style zip, list what
/// shows up on disk, uninstall what we put there. Anything more elaborate (dependency
/// resolution, version pinning) is out of scope for v1.
/// </summary>
public interface IModManager
{
    bool Supports(ServerDef def);
    string ModsFolder(ServerDef def);

    Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct);
    Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct);
    Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct);

    /// <summary>
    /// Returns a search-result page from this game's mod marketplace.
    /// <see cref="MarketplaceSource"/> identifies what we're searching (e.g. "valheim.thunderstore.io")
    /// so the UI can surface "Searching valheim.thunderstore.io…".
    /// Implementations may return an empty array + <see cref="MarketplaceSource"/>=null when search isn't
    /// available for this game.
    /// </summary>
    Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct);
    string? MarketplaceSource(ServerDef def);
}

/// <summary>
/// Routes a ServerDef to the right IModManager. Multiple managers can be registered
/// (one per game family) and the first that Supports() the server wins.
/// </summary>
public sealed class ModManagerRegistry
{
    private readonly IEnumerable<IModManager> _managers;
    public ModManagerRegistry(IEnumerable<IModManager> managers) { _managers = managers; }
    public IModManager? For(ServerDef def) => _managers.FirstOrDefault(m => m.Supports(def));
}
