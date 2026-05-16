using System.Text;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Config;

/// <summary>
/// Reads / writes PalWorldSettings.ini inside the guest.
///
/// The relevant line is:
///   OptionSettings=(Difficulty=None,DayTimeSpeedRate=1.000000,ExpRate=2.5,ServerName="My Server",...)
///
/// We parse the tuple between the outer parens, edit fields, then re-emit a fully-quoted line.
/// </summary>
public sealed class PalworldConfig : IGameConfig
{
    private readonly GuestProcessService _guest;
    private readonly IConfiguration _cfg;
    private readonly ILogger<PalworldConfig> _logger;

    public PalworldConfig(GuestProcessService guest, IConfiguration cfg, ILogger<PalworldConfig> logger)
    {
        _guest = guest;
        _cfg = cfg;
        _logger = logger;
    }

    private GuestCredential GetCred(string? id)
    {
        var key = string.IsNullOrEmpty(id) ? "default" : id;
        var s = _cfg.GetSection($"Agent:GuestCredentials:{key}");
        return new GuestCredential { Username = s["Username"] ?? "", Password = s["Password"] ?? "" };
    }

    private static string IniPath(ServerDef def) =>
        System.IO.Path.Combine(def.GuestWorkingDir, "Pal", "Saved", "Config", "WindowsServer", "PalWorldSettings.ini");

    public async Task<Dictionary<string, string>> ReadAsync(ServerDef def, CancellationToken ct)
    {
        var path = IniPath(def);
        string? content = def.HostingMode == HostingMode.BareMetal
            ? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "")
            : await ReadViaPsDirectAsync(def, path, ct);

        if (string.IsNullOrWhiteSpace(content)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var (_, optionTuple) = FindOptionLine(content);
        return optionTuple is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ParseTuple(optionTuple);
    }

    public async Task<bool> WriteAsync(ServerDef def, Dictionary<string, string> values, CancellationToken ct)
    {
        var path = IniPath(def);
        var current = def.HostingMode == HostingMode.BareMetal
            ? (File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : "")
            : await ReadViaPsDirectAsync(def, path, ct) ?? "";

        var (lineText, optionTuple) = FindOptionLine(current);
        var existing = optionTuple is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : ParseTuple(optionTuple);
        foreach (var (k, v) in values) existing[k] = v ?? "";

        var newTuple = EmitTuple(existing);
        var newLine = $"OptionSettings=({newTuple})";
        var newContent = lineText is null
            ? "[/Script/Pal.PalGameWorldSettings]\r\n" + newLine + "\r\n"
            : current.Replace(lineText, newLine);

        if (def.HostingMode == HostingMode.BareMetal)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, newContent, ct);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Local write to {Path} failed", path);
                return false;
            }
        }

        return await WriteViaPsDirectAsync(def, path, newContent, ct);
    }

    private async Task<string?> ReadViaPsDirectAsync(ServerDef def, string path, CancellationToken ct)
    {
        var cred = GetCred(def.GuestCredentialId);
        var cmd = $"powershell -NoProfile -Command \"if (Test-Path -LiteralPath '{path}') {{ Get-Content -LiteralPath '{path}' -Raw }} else {{ '' }}\"";
        var (ok, output) = await _guest.RunCommandInGuestAsync(def.VmName, cred, cmd, def.GuestWorkingDir, TimeSpan.FromSeconds(20), ct);
        return ok ? output : null;
    }

    private async Task<bool> WriteViaPsDirectAsync(ServerDef def, string path, string content, CancellationToken ct)
    {
        var cred = GetCred(def.GuestCredentialId);
        var bytes = Encoding.UTF8.GetBytes(content);
        var b64 = Convert.ToBase64String(bytes);
        var dir = Path.GetDirectoryName(path)!.Replace("'", "''");
        var safePath = path.Replace("'", "''");
        var script = "powershell -NoProfile -Command \"" +
            $"New-Item -ItemType Directory -Force -Path '{dir}' | Out-Null; " +
            $"[IO.File]::WriteAllBytes('{safePath}', [Convert]::FromBase64String('{b64}'))" +
            "\"";
        var (writeOk, writeOut) = await _guest.RunCommandInGuestAsync(def.VmName, cred, script, def.GuestWorkingDir, TimeSpan.FromSeconds(30), ct);
        if (!writeOk) _logger.LogError("PS-Direct write to {Path} failed: {Out}", path, writeOut);
        return writeOk;
    }

    // ---- INI parsing ----

    private static (string? lineText, string? tupleContent) FindOptionLine(string content)
    {
        var match = Regex.Match(content, @"OptionSettings\s*=\s*\((?<tup>.*)\)", RegexOptions.Singleline);
        if (!match.Success) return (null, null);
        return (match.Value, match.Groups["tup"].Value);
    }

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
            if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
                v = v.Substring(1, v.Length - 2);
            dict[k] = v;
        }
        return dict;
    }

    private static List<string> SplitRespectingQuotesAndParens(string s)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depthParen = 0;
        var inQuote = false;
        foreach (var ch in s)
        {
            if (ch == '"') { inQuote = !inQuote; current.Append(ch); continue; }
            if (!inQuote && ch == '(') { depthParen++; current.Append(ch); continue; }
            if (!inQuote && ch == ')') { depthParen--; current.Append(ch); continue; }
            if (!inQuote && depthParen == 0 && ch == ',')
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

    private static string EmitTuple(Dictionary<string, string> values)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var (k, v) in values)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(k);
            sb.Append('=');
            sb.Append(QuoteIfNeeded(k, v));
        }
        return sb.ToString();
    }

    // Palworld INI: strings are quoted; numbers/bools/enums are bare.
    private static readonly HashSet<string> AlwaysQuoteKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ServerName", "ServerDescription", "AdminPassword", "ServerPassword", "PublicIP", "Region", "BanListURL"
    };

    private static string QuoteIfNeeded(string key, string value)
    {
        if (AlwaysQuoteKeys.Contains(key))
            return "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";
        if (string.IsNullOrEmpty(value)) return "\"\"";
        return value;
    }
}
