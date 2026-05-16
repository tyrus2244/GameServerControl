using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Per-game source that auto-discovers config keys we didn't hand-curate.
///
/// Curated schemas (in <c>ConfigSchema.cs</c>) deliver polished labels, descriptions,
/// units, and min/max bounds for the most-used settings. But every game has more
/// knobs than we can reasonably hand-write, and games ship new settings between
/// versions. A dynamic extension reads the *canonical* source of truth for that
/// game (a defaults file, the admin API, etc.), surfaces everything as a single
/// "All settings (auto-discovered)" section, and skips keys already in curated.
///
/// <para>
/// The /api/servers/{id}/config endpoint composes curated + dynamic into one
/// response so the existing editor UI renders both with no client changes.
/// </para>
/// </summary>
public interface IDynamicSchemaExtension
{
    bool Supports(ServerDef def);

    /// <summary>
    /// Returns the auto-discovered section (one section, possibly many fields)
    /// and current values, or null if nothing was found / supported.
    /// </summary>
    Task<DynamicSchemaResult?> BuildAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct);
}

public sealed record DynamicSchemaResult(ConfigSection Section, Dictionary<string, string> Values);
