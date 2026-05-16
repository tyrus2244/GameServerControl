using System.Text;
using GameServerControl.Agent.Admin;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Satisfactory config has THREE backends:
///
///   1. Engine.ini — server-limits stuff that needs a restart:
///      MaxPlayers, ConfiguredInternetSpeed.
///   2. Game.ini — auto-pause / auto-save / restart slot / auto-load name.
///   3. HTTPS Admin API — server identity (name, client password) and
///      Advanced Game Settings (NoPower, GodMode, FlightMode, etc.).
///
/// Keys starting with "AGS." in the schema are AGS toggles; everything else
/// is routed by the static <see cref="IniKeyMap"/> + <see cref="ApiKeys"/>.
/// AGS API keys live under "FG.PlayerRules.&lt;Name&gt;" — schema strips the "AGS." prefix.
/// </summary>
public sealed class SatisfactoryConfig : IGameConfig
{
    private readonly ILogger<SatisfactoryConfig> _logger;
    private readonly SatisfactoryAdminClient _api;

    public SatisfactoryConfig(ILogger<SatisfactoryConfig> logger, SatisfactoryAdminClient api)
    {
        _logger = logger;
        _api = api;
    }

    // ---- INI routing (schema-key → (relative path, section, ini-key)) ----

    private static readonly Dictionary<string, (string Rel, string Section, string IniKey)> IniKeyMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MaxPlayers"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Engine.ini", "/Script/Engine.GameSession", "MaxPlayers"),
            ["AutoLoadSessionName"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Game.ini", "/Script/FactoryGame.FGServerSubsystem", "mAutoLoadSessionName"),
            ["mAutoPauseServerOnEmpty"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Game.ini", "/Script/FactoryGame.FGServerSubsystem", "mAutoPauseServerOnEmpty"),
            ["mAutoSaveOnDisconnect"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Game.ini", "/Script/FactoryGame.FGServerSubsystem", "mAutoSaveOnDisconnect"),
            ["mServerRestartTimeSlot"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Game.ini", "/Script/FactoryGame.FGServerSubsystem", "mServerRestartTimeSlot"),
            ["ConfiguredInternetSpeed"] =
                (@"FactoryGame\Saved\Config\WindowsServer\Engine.ini", "/Script/Engine.Player", "ConfiguredInternetSpeed"),
        };

    // Schema keys that are settable via dedicated API methods (not AGS).
    private static readonly HashSet<string> ApiKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ServerName", "ClientPassword"
    };

    private const string AgsPrefix = "AGS.";

    // Schema key (without "AGS." prefix) → exact API key.
    // Empirically derived from a live GetAdvancedGameSettings response — the prefix
    // is NOT consistent: most are FG.GameRules.* but a few are FG.PlayerRules.*.
    private static readonly Dictionary<string, string> AgsApiKeyByLeaf =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["NoPower"]                          = "FG.GameRules.NoPower",
            ["NoFuelCost"]                       = "FG.GameRules.NoFuelCost",
            ["NoUnlockCost"]                     = "FG.GameRules.NoUnlockCost",
            ["UnlockInstantAltRecipes"]          = "FG.GameRules.UnlockInstantAltRecipes",
            ["SetGamePhase"]                     = "FG.GameRules.SetGamePhase",
            ["StartingTier"]                     = "FG.GameRules.StartingTier",
            ["GiveAllTiers"]                     = "FG.GameRules.GiveAllTiers",
            ["UnlockAllResearchSchematics"]      = "FG.GameRules.UnlockAllResearchSchematics",
            ["UnlockAllResourceSinkSchematics"]  = "FG.GameRules.UnlockAllResourceSinkSchematics",
            ["DisableArachnidCreatures"]         = "FG.GameRules.DisableArachnidCreatures",
            ["NoBuildCost"]                      = "FG.PlayerRules.NoBuildCost",
            ["GodMode"]                          = "FG.PlayerRules.GodMode",
            ["FlightMode"]                       = "FG.PlayerRules.FlightMode",
        };
    // Reverse: API key → schema leaf for the Read path.
    private static readonly Dictionary<string, string> AgsLeafByApiKey =
        AgsApiKeyByLeaf.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    // ---- read ----

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Satisfactory config in Vm mode not implemented");
            return result;
        }

        // 1) INI reads (always safe — file-only, no auth needed)
        var iniCache = new Dictionary<string, IniDoc>(StringComparer.OrdinalIgnoreCase);
        foreach (var (schemaKey, m) in IniKeyMap)
        {
            var path = Path.Combine(def.GuestWorkingDir, m.Rel);
            if (!iniCache.TryGetValue(path, out var doc))
            {
                doc = File.Exists(path) ? IniDoc.Parse(await File.ReadAllTextAsync(path, ct)) : new IniDoc();
                iniCache[path] = doc;
            }
            var v = doc.Get(m.Section, m.IniKey);
            if (v is not null) result[schemaKey] = NormalizeIniRead(schemaKey, v);
        }

        // 2) API reads — only if admin password is configured.
        if (!string.IsNullOrEmpty(def.RconPassword))
        {
            try
            {
                var serverName = await _api.QueryServerNameAsync(def, ct);
                if (serverName is not null) result["ServerName"] = serverName;

                var ags = await _api.GetAdvancedGameSettingsAsync(def, ct);
                if (ags is not null)
                {
                    foreach (var kvp in ags)
                    {
                        // Only surface keys we have a schema field for. The server returns more
                        // (e.g. FG.GameRules.GiveItems) that we don't expose yet.
                        if (!AgsLeafByApiKey.TryGetValue(kvp.Key, out var leaf)) continue;
                        var raw = kvp.Value?.ToString() ?? "";
                        result[AgsPrefix + leaf] = raw.Equals("True", StringComparison.OrdinalIgnoreCase) ? "true"
                                                  : raw.Equals("False", StringComparison.OrdinalIgnoreCase) ? "false"
                                                  : raw;
                    }
                }
                // We never read ClientPassword back — Satisfactory doesn't expose it for read; it's write-only.
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Satisfactory API read failed; falling back to INI-only view");
            }
        }

        return result;
    }

    // ---- write ----

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Satisfactory config in Vm mode not implemented");
            return false;
        }

        // Partition incoming values:
        //   iniOps[file] = list of (section, key, value) writes
        //   apiAgs       = AGS toggles to apply in one call
        //   serverNameNew / clientPasswordNew = single API setter calls
        var iniOps = new Dictionary<string, List<(string Section, string IniKey, string Value)>>(StringComparer.OrdinalIgnoreCase);
        var apiAgs = new Dictionary<string, object>();
        string? serverNameNew = null;
        string? clientPasswordNew = null;

        foreach (var (schemaKey, rawVal) in values)
        {
            if (schemaKey.StartsWith(AgsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var leaf = schemaKey.Substring(AgsPrefix.Length);
                if (!AgsApiKeyByLeaf.TryGetValue(leaf, out var apiKey))
                {
                    _logger.LogDebug("Unknown AGS key {Leaf} — no API mapping; ignoring", leaf);
                    continue;
                }
                apiAgs[apiKey] = CoerceAgsValue(schemaKey, rawVal);
                continue;
            }
            if (schemaKey.Equals("ServerName", StringComparison.OrdinalIgnoreCase)) { serverNameNew = rawVal; continue; }
            if (schemaKey.Equals("ClientPassword", StringComparison.OrdinalIgnoreCase)) { clientPasswordNew = rawVal; continue; }
            if (IniKeyMap.TryGetValue(schemaKey, out var m))
            {
                var path = Path.Combine(def.GuestWorkingDir, m.Rel);
                if (!iniOps.TryGetValue(path, out var list))
                {
                    list = new List<(string, string, string)>();
                    iniOps[path] = list;
                }
                list.Add((m.Section, m.IniKey, NormalizeIniWrite(schemaKey, rawVal)));
                continue;
            }
            _logger.LogDebug("Ignoring unknown Satisfactory schema key {Key}", schemaKey);
        }

        var allOk = true;

        // ---- INI writes ----
        foreach (var (path, ops) in iniOps)
        {
            try
            {
                var doc = File.Exists(path) ? IniDoc.Parse(await File.ReadAllTextAsync(path, ct)) : new IniDoc();
                foreach (var (sec, k, v) in ops) doc.Set(sec, k, v);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                await File.WriteAllTextAsync(tmp, doc.Emit(), ct);
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
                else File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SatisfactoryConfig INI write to {Path} failed", path);
                allOk = false;
            }
        }

        // ---- API writes ----
        // Skip silently if no admin password is configured; INI ops still apply.
        if (string.IsNullOrEmpty(def.RconPassword) &&
            (serverNameNew is not null || clientPasswordNew is not null || apiAgs.Count > 0))
        {
            _logger.LogWarning("Satisfactory API write skipped — no admin password configured on {Id}. " +
                "Set the admin password (servers.json RconPassword) to enable live identity + AGS edits.", def.Id);
            return allOk;
        }

        if (serverNameNew is not null)
        {
            if (!await _api.RenameServerAsync(def, serverNameNew, ct)) { allOk = false; _logger.LogWarning("RenameServer failed for {Id}", def.Id); }
        }
        if (clientPasswordNew is not null)
        {
            if (!await _api.SetClientPasswordAsync(def, clientPasswordNew, ct)) { allOk = false; _logger.LogWarning("SetClientPassword failed for {Id}", def.Id); }
        }
        if (apiAgs.Count > 0)
        {
            if (!await _api.ApplyAdvancedGameSettingsAsync(def, apiAgs, ct)) { allOk = false; _logger.LogWarning("ApplyAdvancedGameSettings failed for {Id}", def.Id); }
        }

        return allOk;
    }

    private static object CoerceAgsValue(string schemaKey, string raw)
    {
        // Integer AGS keys: SetGamePhase, StartingTier. Everything else is bool.
        if ((schemaKey.EndsWith(".SetGamePhase", StringComparison.OrdinalIgnoreCase) ||
             schemaKey.EndsWith(".StartingTier", StringComparison.OrdinalIgnoreCase)) &&
            int.TryParse(raw, out var n)) return n;
        return raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1";
    }

    private static string NormalizeIniRead(string schemaKey, string raw)
    {
        if (IsIniBoolKey(schemaKey))
            return raw.Trim().Equals("True", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        var v = raw.Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v.Substring(1, v.Length - 2);
        return v;
    }

    private static string NormalizeIniWrite(string schemaKey, string raw)
    {
        if (IsIniBoolKey(schemaKey))
            return raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || raw.Trim() == "1" ? "True" : "False";
        return raw ?? "";
    }

    private static bool IsIniBoolKey(string schemaKey) =>
        schemaKey.Equals("mAutoPauseServerOnEmpty", StringComparison.OrdinalIgnoreCase) ||
        schemaKey.Equals("mAutoSaveOnDisconnect", StringComparison.OrdinalIgnoreCase);

    // -------- minimal INI doc that preserves comments/blank lines/order --------

    private sealed class IniDoc
    {
        private readonly List<string> _lines = new();

        public static IniDoc Parse(string text)
        {
            var doc = new IniDoc();
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n')) doc._lines.Add(raw);
            return doc;
        }

        public string? Get(string section, string key)
        {
            var (s, e) = FindSectionRange(section);
            if (s < 0) return null;
            for (int i = s + 1; i < e; i++)
                if (TryParseKv(_lines[i], out var k, out var v) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return v;
            return null;
        }

        public void Set(string section, string key, string value)
        {
            var (s, e) = FindSectionRange(section);
            if (s < 0)
            {
                if (_lines.Count > 0 && !string.IsNullOrWhiteSpace(_lines[^1])) _lines.Add("");
                _lines.Add("[" + section + "]");
                _lines.Add($"{key}={value}");
                return;
            }
            for (int i = s + 1; i < e; i++)
            {
                if (TryParseKv(_lines[i], out var k, out _) && k.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    _lines[i] = $"{key}={value}";
                    return;
                }
            }
            var insertIdx = e;
            while (insertIdx > s + 1 && string.IsNullOrWhiteSpace(_lines[insertIdx - 1])) insertIdx--;
            _lines.Insert(insertIdx, $"{key}={value}");
        }

        public string Emit()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                sb.Append(_lines[i]);
                if (i < _lines.Count - 1) sb.Append("\r\n");
            }
            if (sb.Length == 0 || sb[^1] != '\n') sb.Append("\r\n");
            return sb.ToString();
        }

        private (int Start, int End) FindSectionRange(string section)
        {
            var target = "[" + section + "]";
            int start = -1;
            for (int i = 0; i < _lines.Count; i++)
            {
                var trimmed = _lines[i].Trim();
                if (trimmed.Equals(target, StringComparison.OrdinalIgnoreCase)) { start = i; break; }
            }
            if (start < 0) return (-1, -1);
            int end = _lines.Count;
            for (int i = start + 1; i < _lines.Count; i++)
            {
                var t = _lines[i].Trim();
                if (t.StartsWith("[") && t.EndsWith("]")) { end = i; break; }
            }
            return (start, end);
        }

        private static bool TryParseKv(string line, out string key, out string value)
        {
            key = ""; value = "";
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0) return false;
            if (trimmed.StartsWith(";") || trimmed.StartsWith("#") || trimmed.StartsWith("[")) return false;
            var eq = trimmed.IndexOf('=');
            if (eq <= 0) return false;
            key = trimmed.Substring(0, eq).Trim();
            value = trimmed.Substring(eq + 1).Trim();
            return true;
        }
    }
}
