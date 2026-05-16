using GameServerControl.Agent.Admin;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Auto-discovers Satisfactory Advanced Game Settings by querying the live admin API.
///
/// The <c>GetAdvancedGameSettings</c> endpoint returns the COMPLETE set of AGS keys
/// the running server actually supports — no guessing needed. We surface anything
/// the curated schema doesn't already cover (e.g. <c>FG.GameRules.GiveItems</c>,
/// which is a JSON tuple we don't have a UI control for, but still worth showing).
/// </summary>
public sealed class SatisfactoryDynamicSchema : IDynamicSchemaExtension
{
    private readonly ILogger<SatisfactoryDynamicSchema> _logger;
    private readonly SatisfactoryAdminClient _api;

    public SatisfactoryDynamicSchema(ILogger<SatisfactoryDynamicSchema> logger, SatisfactoryAdminClient api)
    {
        _logger = logger;
        _api = api;
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "1690800";

    public async Task<DynamicSchemaResult?> BuildAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(def.RconPassword))
        {
            _logger.LogDebug("Satisfactory admin password not set; cannot enumerate AGS keys.");
            return null;
        }

        var ags = await _api.GetAdvancedGameSettingsAsync(def, ct);
        if (ags is null)
        {
            _logger.LogDebug("Satisfactory AGS fetch returned null (auth failed or server unreachable).");
            return null;
        }

        // Curated schema uses keys like "AGS.NoPower"; map back to API form for de-dup.
        // Build a comparison set that matches both forms.
        var curatedApiForms = new HashSet<string>(curatedKeys
            .Where(k => k.StartsWith("AGS.", StringComparison.OrdinalIgnoreCase))
            .Select(k => k[4..]), StringComparer.OrdinalIgnoreCase);

        var fields = new List<ConfigField>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in ags.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var apiKey = kvp.Key;
            // API keys look like "FG.GameRules.NoPower" or "FG.PlayerRules.GodMode".
            // Extract the leaf (after the last dot).
            var lastDot = apiKey.LastIndexOf('.');
            var leaf = lastDot >= 0 ? apiKey[(lastDot + 1)..] : apiKey;
            if (curatedApiForms.Contains(leaf)) continue;

            var raw = kvp.Value?.ToString() ?? "";
            // Use the "AGS.<leaf>" form as the schema key so writes route through the existing AGS pathway.
            var schemaKey = "AGS." + leaf;
            fields.Add(DynamicSchemaUtils.InferField(schemaKey, raw, "Satisfactory Admin API"));
            // Normalize bool case for the editor
            values[schemaKey] = raw.Equals("True", StringComparison.OrdinalIgnoreCase) ? "true"
                              : raw.Equals("False", StringComparison.OrdinalIgnoreCase) ? "false"
                              : raw;
        }

        if (fields.Count == 0) return null;
        return new DynamicSchemaResult(
            new ConfigSection($"All AGS keys (auto-discovered, {fields.Count})", fields.ToArray()),
            values);
    }
}
