using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Windrose configuration is split across TWO files:
///
///   1. ServerDescription.json (under &lt;GuestWorkingDir&gt;\R5\) — identity & access.
///      Fields: ServerName, MaxPlayerCount, IsPasswordProtected, Password, InviteCode (read-only).
///      Schema-relevant fields live under "ServerDescription_Persistent".
///
///   2. WorldDescription.json — world rules. Path includes a game version dir that
///      changes between releases:
///        &lt;GuestWorkingDir&gt;\R5\Saved\SaveProfiles\Default\RocksDB_v2\&lt;version&gt;\Worlds\&lt;WorldIslandId&gt;\WorldDescription.json
///      WorldIslandId comes from ServerDescription.json.
///      Fields: WorldPresetType (top-level under "WorldDescription"); the per-rule
///      Bool/Float/Tag parameters live in WorldDescription.WorldSettings.* with
///      JSON-stringified TagName keys.
///
/// We mutate only known keys and preserve everything else exactly.
/// </summary>
public sealed class WindroseConfig : IGameConfig
{
    private readonly ILogger<WindroseConfig> _logger;
    public WindroseConfig(ILogger<WindroseConfig> logger) { _logger = logger; }

    // ---------- file paths ----------

    private static string ServerDescPath(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "R5", "ServerDescription.json");

    /// <summary>
    /// Locates WorldDescription.json by reading the WorldIslandId from
    /// ServerDescription.json and then probing the SaveProfiles directory.
    /// Returns null if either is missing.
    /// </summary>
    private static string? FindWorldDescPath(ServerDef def)
    {
        var sdp = ServerDescPath(def);
        if (!File.Exists(sdp)) return null;
        string worldId;
        try
        {
            var content = File.ReadAllText(sdp);
            worldId = (JsonNode.Parse(content) as JsonObject)?[PersistentSection]?["WorldIslandId"]?.ToString() ?? "";
        }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(worldId)) return null;

        var rocksRoot = Path.Combine(def.GuestWorkingDir, "R5", "Saved", "SaveProfiles", "Default", "RocksDB_v2");
        if (!Directory.Exists(rocksRoot)) return null;

        // Game-version dirs like "0.10.0", "0.11.0", ...; pick the newest by string sort.
        foreach (var versionDir in Directory.EnumerateDirectories(rocksRoot).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(versionDir, "Worlds", worldId, "WorldDescription.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    // ---------- key routing ----------

    // ServerDescription_Persistent section name in the server JSON.
    private const string PersistentSection = "ServerDescription_Persistent";

    // Schema keys that target ServerDescription.json.
    private static readonly HashSet<string> ServerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ServerName", "Password", "InviteCode", "MaxPlayerCount", "IsPasswordProtected"
    };

    // Schema keys that target the WorldDescription.json top-level (under "WorldDescription").
    private static readonly HashSet<string> WorldTopLevelKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "WorldPresetType"
    };

    // Schema key → WDS.Parameter.* TagName (inside WorldSettings.FloatParameters).
    private static readonly Dictionary<string, string> FloatTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MobHealthMultiplier"] = "WDS.Parameter.MobHealthMultiplier",
        ["MobDamageMultiplier"] = "WDS.Parameter.MobDamageMultiplier",
        ["ShipHealthMultiplier"] = "WDS.Parameter.ShipsHealthMultiplier",   // engine uses "Ships" (plural)
        ["ShipDamageMultiplier"] = "WDS.Parameter.ShipsDamageMultiplier",
        ["BoardingDifficultyMultiplier"] = "WDS.Parameter.BoardingDifficultyMultiplier",
        ["Coop_StatsCorrectionModifier"] = "WDS.Parameter.Coop.StatsCorrectionModifier",
        ["Coop_ShipStatsCorrectionModifier"] = "WDS.Parameter.Coop.ShipStatsCorrectionModifier",
    };

    // Schema key → WDS.Parameter.* TagName (inside WorldSettings.BoolParameters).
    private static readonly Dictionary<string, string> BoolTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CoopQuests"] = "WDS.Parameter.Coop.SharedQuests",
        ["EasyExplore"] = "WDS.Parameter.EasyExplore",
    };

    // Schema key → WDS.Parameter.* TagName (inside WorldSettings.TagParameters).
    // Values are themselves nested {"TagName":"WDS.Parameter.CombatDifficulty.<level>"}.
    private static readonly Dictionary<string, string> TagTagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CombatDifficulty"] = "WDS.Parameter.CombatDifficulty",
    };

    /// <summary>Build the literal JSON-stringified key the engine uses inside the Parameters objects.</summary>
    private static string TagNameKey(string tagName) => $"{{\"TagName\": \"{tagName}\"}}";

    // ---------- read ----------

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Windrose config in Vm mode not implemented");
            return dict;
        }

        await ReadServerDescAsync(def, dict, ct);
        await ReadWorldDescAsync(def, dict, ct);
        return dict;
    }

    private async Task ReadServerDescAsync(ServerDef def, Dictionary<string, string> dict, CancellationToken ct)
    {
        var path = ServerDescPath(def);
        if (!File.Exists(path)) return;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var root = JsonNode.Parse(text);
            var section = (root as JsonObject)?[PersistentSection] as JsonObject;
            if (section is null) return;
            foreach (var key in new[] { "ServerName", "Password", "InviteCode" })
                if (section.TryGetPropertyValue(key, out var v) && v is not null) dict[key] = v.ToString();
            if (section.TryGetPropertyValue("MaxPlayerCount", out var mp) && mp is not null) dict["MaxPlayerCount"] = mp.ToString();
            if (section.TryGetPropertyValue("IsPasswordProtected", out var pp) && pp is not null)
                dict["IsPasswordProtected"] = bool.TryParse(pp.ToString(), out var b) && b ? "true" : "false";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read {Path}", path);
        }
    }

    private async Task ReadWorldDescAsync(ServerDef def, Dictionary<string, string> dict, CancellationToken ct)
    {
        var path = FindWorldDescPath(def);
        if (path is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var root = JsonNode.Parse(text) as JsonObject;
            var world = root?["WorldDescription"] as JsonObject;
            if (world is null) return;

            // Top-level (e.g. WorldPresetType)
            if (world.TryGetPropertyValue("WorldPresetType", out var preset) && preset is not null)
                dict["WorldPresetType"] = preset.ToString();

            var settings = world["WorldSettings"] as JsonObject;
            if (settings is null) return;

            // Float parameters
            var floats = settings["FloatParameters"] as JsonObject;
            if (floats is not null)
            {
                foreach (var (schemaKey, tag) in FloatTagNames)
                {
                    var k = TagNameKey(tag);
                    if (floats.TryGetPropertyValue(k, out var v) && v is not null)
                        dict[schemaKey] = v.ToString();
                }
            }

            // Bool parameters
            var bools = settings["BoolParameters"] as JsonObject;
            if (bools is not null)
            {
                foreach (var (schemaKey, tag) in BoolTagNames)
                {
                    var k = TagNameKey(tag);
                    if (bools.TryGetPropertyValue(k, out var v) && v is not null)
                        dict[schemaKey] = bool.TryParse(v.ToString(), out var b) && b ? "true" : "false";
                }
            }

            // Tag parameters (nested objects with their own TagName)
            var tags = settings["TagParameters"] as JsonObject;
            if (tags is not null)
            {
                foreach (var (schemaKey, tag) in TagTagNames)
                {
                    var k = TagNameKey(tag);
                    if (tags.TryGetPropertyValue(k, out var v) && v is JsonObject obj &&
                        obj.TryGetPropertyValue("TagName", out var inner) && inner is not null)
                    {
                        // "WDS.Parameter.CombatDifficulty.Normal" → "Normal"
                        var full = inner.ToString();
                        var lastDot = full.LastIndexOf('.');
                        dict[schemaKey] = lastDot >= 0 ? full[(lastDot + 1)..] : full;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read WorldDescription at {Path}", path);
        }
    }

    // ---------- write ----------

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        if (def.HostingMode != HostingMode.BareMetal)
        {
            _logger.LogWarning("Windrose config in Vm mode not implemented");
            return false;
        }

        // Split incoming values by destination file
        var serverOps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var worldOps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values)
        {
            if (ServerKeys.Contains(k)) serverOps[k] = v;
            else worldOps[k] = v;
        }

        var okServer = serverOps.Count == 0 || await WriteServerDescAsync(def, serverOps, ct);
        var okWorld  = worldOps.Count  == 0 || await WriteWorldDescAsync(def, worldOps, ct);
        return okServer && okWorld;
    }

    private async Task<bool> WriteServerDescAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var path = ServerDescPath(def);
        try
        {
            JsonObject root;
            if (File.Exists(path))
            {
                var text = await File.ReadAllTextAsync(path, ct);
                root = (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            }

            if (root[PersistentSection] is not JsonObject section)
            {
                section = new JsonObject();
                root[PersistentSection] = section;
            }
            foreach (var (k, v) in values)
            {
                if (k.Equals("InviteCode", StringComparison.OrdinalIgnoreCase)) continue; // server-managed
                section[k] = ParseTypedServer(k, v);
            }

            return await WriteJsonAtomicAsync(path, root, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write to {Path} failed", path);
            return false;
        }
    }

    private async Task<bool> WriteWorldDescAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var path = FindWorldDescPath(def);
        if (path is null)
        {
            _logger.LogWarning("WorldDescription.json not found; cannot apply world-rule changes. Start the server once so the world is created, then retry.");
            return false;
        }

        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var root = (JsonNode.Parse(text) as JsonObject) ?? new JsonObject();
            if (root["WorldDescription"] is not JsonObject world)
            {
                world = new JsonObject();
                root["WorldDescription"] = world;
            }

            // Detect whether any per-rule custom value is being set; if so, force preset = Custom
            // because the engine wipes WorldSettings on next launch unless preset == Custom.
            var anyCustom = values.Keys.Any(k =>
                FloatTagNames.ContainsKey(k) || BoolTagNames.ContainsKey(k) || TagTagNames.ContainsKey(k));

            if (values.TryGetValue("WorldPresetType", out var preset))
            {
                world["WorldPresetType"] = preset;
                // Switching to a non-custom preset: clear WorldSettings (engine would do this anyway).
                if (!preset.Equals("Custom", StringComparison.OrdinalIgnoreCase))
                {
                    world["WorldSettings"] = new JsonObject
                    {
                        ["BoolParameters"]  = new JsonObject(),
                        ["FloatParameters"] = new JsonObject(),
                        ["TagParameters"]   = new JsonObject(),
                    };
                }
            }
            else if (anyCustom)
            {
                // User is tweaking individual rules without explicitly choosing Custom — set it for them.
                world["WorldPresetType"] = "Custom";
            }

            // Ensure WorldSettings exists with its three child objects
            if (world["WorldSettings"] is not JsonObject settings)
            {
                settings = new JsonObject
                {
                    ["BoolParameters"]  = new JsonObject(),
                    ["FloatParameters"] = new JsonObject(),
                    ["TagParameters"]   = new JsonObject(),
                };
                world["WorldSettings"] = settings;
            }
            var floats = settings["FloatParameters"] as JsonObject ?? new JsonObject();
            var bools  = settings["BoolParameters"]  as JsonObject ?? new JsonObject();
            var tags   = settings["TagParameters"]   as JsonObject ?? new JsonObject();
            settings["FloatParameters"] = floats;
            settings["BoolParameters"]  = bools;
            settings["TagParameters"]   = tags;

            foreach (var (k, v) in values)
            {
                if (FloatTagNames.TryGetValue(k, out var ftag) && double.TryParse(v, out var d))
                    floats[TagNameKey(ftag)] = JsonValue.Create(d);
                else if (BoolTagNames.TryGetValue(k, out var btag))
                    bools[TagNameKey(btag)] = JsonValue.Create(v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");
                else if (TagTagNames.TryGetValue(k, out var ttag))
                    tags[TagNameKey(ttag)] = new JsonObject { ["TagName"] = $"{ttag}.{v}" };
            }

            return await WriteJsonAtomicAsync(path, root, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write to {Path} failed", path);
            return false;
        }
    }

    // ---------- shared helpers ----------

    private static async Task<bool> WriteJsonAtomicAsync(string path, JsonObject root, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
        else File.Move(tmp, path);
        return true;
    }

    private static JsonNode? ParseTypedServer(string key, string raw)
    {
        if (key.Equals("MaxPlayerCount", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(raw, out var n)) return JsonValue.Create(n);
        if (key.Equals("IsPasswordProtected", StringComparison.OrdinalIgnoreCase))
            return JsonValue.Create(raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1");
        return JsonValue.Create(raw);
    }
}
