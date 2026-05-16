using GameServerControl.Agent.Servers;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Valheim configures via launch args on `valheim_server.exe`. We treat ServerDef.StartArgs
/// as the source of truth and translate to/from a flat dict whose keys match
/// <see cref="ConfigSchemas.Valheim"/>.
///
/// Encoding:
///   key=value pairs    -> ["-key", "value"]   e.g. -name "My Server"
///   modifier:foo=val   -> ["-modifier", "foo", "val"]
///   bool flag (true)   -> ["-flag"]           e.g. -nobuildcost
///   bool flag (false)  -> absent
/// </summary>
public sealed class ValheimConfig : IGameConfig
{
    private static readonly HashSet<string> BoolFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "nobuildcost", "playerevents", "passivemobs", "nomap", "crossplay"
    };

    private static readonly HashSet<string> KeyValueArgs = new(StringComparer.OrdinalIgnoreCase)
    {
        "name", "password", "port", "world", "savedir", "saveinterval", "backups", "preset", "public"
    };

    private readonly ServerStore _store;
    public ValheimConfig(ServerStore store) { _store = store; }

    public Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var args = def.StartArgs ?? Array.Empty<string>();

        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("-")) continue;
            var bare = a.TrimStart('-');

            if (string.Equals(bare, "modifier", StringComparison.OrdinalIgnoreCase) && i + 2 < args.Length)
            {
                var sub = args[i + 1];
                var val = args[i + 2];
                dict[$"modifier:{sub}"] = val;
                i += 2;
                continue;
            }

            if (BoolFlags.Contains(bare))
            {
                dict[bare] = "true";
                continue;
            }

            if (KeyValueArgs.Contains(bare) && i + 1 < args.Length && !args[i + 1].StartsWith("-"))
            {
                dict[bare] = args[i + 1];
                i++;
            }
        }
        return Task.FromResult(dict);
    }

    public Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var args = new List<string>();
        // Re-emit in a stable order so diffs stay readable.
        foreach (var k in new[] { "name", "password", "port", "world", "savedir", "saveinterval", "backups", "public", "preset" })
        {
            if (values.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v))
            {
                args.Add("-" + k);
                args.Add(v);
            }
        }
        foreach (var (k, v) in values.Where(kv => kv.Key.StartsWith("modifier:", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            args.Add("-modifier");
            args.Add(k.Substring("modifier:".Length));
            args.Add(v);
        }
        foreach (var flag in BoolFlags)
        {
            if (values.TryGetValue(flag, out var v) && IsTrue(v))
                args.Add("-" + flag);
        }

        // Preserve any "unknown" args the user might have added by hand (e.g. -nographics, -batchmode)
        if (def.StartArgs is { } existing)
        {
            for (var i = 0; i < existing.Length; i++)
            {
                var a = existing[i];
                if (!a.StartsWith("-")) continue;
                var bare = a.TrimStart('-');
                if (BoolFlags.Contains(bare) || KeyValueArgs.Contains(bare) || bare.Equals("modifier", StringComparison.OrdinalIgnoreCase))
                    continue;
                args.Insert(0, a);
                if (i + 1 < existing.Length && !existing[i + 1].StartsWith("-"))
                {
                    args.Insert(1, existing[i + 1]);
                    i++;
                }
            }
        }

        _store.Update(def.Id, def with { StartArgs = args.ToArray() });
        return Task.FromResult(true);
    }

    private static bool IsTrue(string? s) =>
        !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");
}
