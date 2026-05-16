using System.Text;
using System.Text.RegularExpressions;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Auto-discovers every OptionSettings key Palworld supports by parsing the
/// vendor-provided <c>DefaultPalWorldSettings.ini</c> at the server install root.
///
/// That file is the canonical "here's every knob this version of Palworld has"
/// document. It's a single commented-out tuple line:
///   ;OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.0,...,ServerName="...")
/// We strip the leading semicolon if present, parse the tuple respecting quoted
/// strings, and emit a ConfigField for every key not already covered by the
/// curated schema.
/// </summary>
public sealed class PalworldDynamicSchema : IDynamicSchemaExtension
{
    private readonly ILogger<PalworldDynamicSchema> _logger;
    private readonly PalworldConfig _curatedConfig;  // we reuse the curated read for current values

    public PalworldDynamicSchema(ILogger<PalworldDynamicSchema> logger, PalworldConfig curated)
    {
        _logger = logger;
        _curatedConfig = curated;
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "2394010";

    public async Task<DynamicSchemaResult?> BuildAsync(ServerDef def, IReadOnlySet<string> curatedKeys, CancellationToken ct)
    {
        var defaultsPath = Path.Combine(def.GuestWorkingDir, "DefaultPalWorldSettings.ini");
        if (!File.Exists(defaultsPath))
        {
            _logger.LogDebug("Palworld defaults file not found at {Path}; skipping dynamic schema.", defaultsPath);
            return null;
        }

        Dictionary<string, string> allDefaults;
        try
        {
            var text = await File.ReadAllTextAsync(defaultsPath, ct);
            var match = Regex.Match(
                text,
                @"^\s*;?\s*OptionSettings\s*=\s*\((.*)\)\s*$",
                RegexOptions.Multiline | RegexOptions.Singleline);
            if (!match.Success)
            {
                _logger.LogWarning("Palworld defaults file present but OptionSettings tuple not found.");
                return null;
            }
            allDefaults = ParseTuple(match.Groups[1].Value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse {Path}", defaultsPath);
            return null;
        }

        // Get current live values so the editor shows what's actually configured, not just defaults.
        var liveValues = await _curatedConfig.ReadAsync(def, ct);

        // Build a field for each key NOT already in the curated schema.
        var fields = new List<ConfigField>();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, defaultVal) in allDefaults.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (curatedKeys.Contains(key)) continue;
            fields.Add(DynamicSchemaUtils.InferField(key, defaultVal, "DefaultPalWorldSettings.ini"));
            // Prefer live value if we have one, else fall back to the defaults-file default.
            values[key] = liveValues.TryGetValue(key, out var live) && !string.IsNullOrEmpty(live) ? live : defaultVal;
        }

        if (fields.Count == 0) return null;
        return new DynamicSchemaResult(
            new ConfigSection($"All settings (auto-discovered, {fields.Count})", fields.ToArray()),
            values);
    }

    // ---- minimal tuple parser (same shape PalworldConfig uses for write) ----
    private static Dictionary<string, string> ParseTuple(string tuple)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = SplitRespectingQuotesAndParens(tuple);
        foreach (var p in parts)
        {
            var eq = p.IndexOf('=');
            if (eq < 0) continue;
            var k = p.Substring(0, eq).Trim();
            var v = p.Substring(eq + 1).Trim();
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v.Substring(1, v.Length - 2);
            dict[k] = v;
        }
        return dict;
    }

    private static List<string> SplitRespectingQuotesAndParens(string s)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; current.Append(ch); continue; }
            if (!inQuote && ch == '(') { depth++; current.Append(ch); continue; }
            if (!inQuote && ch == ')') { depth--; current.Append(ch); continue; }
            if (!inQuote && depth == 0 && ch == ',')
            {
                parts.Add(current.ToString());
                current.Clear();
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts;
    }
}
