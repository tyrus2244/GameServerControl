using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Auth;

public sealed record TokenRecord
{
    public string Id { get; init; } = "";          // Human label slug, e.g. "ops-tyrus"
    public string Name { get; init; } = "";        // Display name shown in UI
    public string Token { get; init; } = "";       // The actual bearer token
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public TokenRole Role { get; init; } = TokenRole.Admin;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Multi-token store backed by tokens.json. The legacy single Agent:ApiToken setting still
/// works and is treated as an Admin token for backward compatibility.
/// </summary>
public sealed class TokenRegistry
{
    private readonly string _path;
    private readonly string _legacyToken;
    private readonly ILogger<TokenRegistry> _logger;
    private readonly object _gate = new();
    private List<TokenRecord> _tokens = new();

    public TokenRegistry(IConfiguration cfg, ILogger<TokenRegistry> logger)
    {
        _logger = logger;
        _legacyToken = cfg["Agent:ApiToken"] ?? "";
        var dataDir = cfg["Agent:DataDir"] ?? Path.GetDirectoryName(typeof(TokenRegistry).Assembly.Location) ?? Environment.CurrentDirectory;
        _path = Path.Combine(dataDir, "tokens.json");
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) { _tokens = new(); return; }
                _tokens = JsonSerializer.Deserialize<List<TokenRecord>>(File.ReadAllText(_path)) ?? new();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "tokens.json parse failed; starting with empty registry");
                _tokens = new();
            }
        }
    }

    private void Save()
    {
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_tokens, new JsonSerializerOptions { WriteIndented = true }));
                if (File.Exists(_path)) File.Replace(tmp, _path, _path + ".bak");
                else File.Move(tmp, _path);
            }
            catch (Exception ex) { _logger.LogError(ex, "Save tokens.json failed"); throw; }
        }
    }

    /// <summary>
    /// Look up a presented bearer token. Returns (id, name, role) on success or null on miss.
    /// Falls back to the legacy single token in appsettings → mapped to ("legacy", "Legacy", Admin).
    /// </summary>
    public (string Id, string Name, TokenRole Role)? Lookup(string presented)
    {
        if (string.IsNullOrEmpty(presented)) return null;
        if (!string.IsNullOrEmpty(_legacyToken) && CryptoEquals(presented, _legacyToken))
            return ("legacy", "Legacy (appsettings)", TokenRole.Admin);
        lock (_gate)
        {
            foreach (var t in _tokens)
                if (CryptoEquals(presented, t.Token)) return (t.Id, t.Name, t.Role);
        }
        return null;
    }

    public IReadOnlyList<TokenRecord> ListMetadata()
    {
        lock (_gate)
        {
            // Strip the actual token value when listing — only the prefix is exposed for identification.
            return _tokens.Select(t => t with { Token = TokenPrefix(t.Token) }).ToArray();
        }
    }

    public TokenRecord Create(string id, string name, TokenRole role)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("id required");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name required");
        var rec = new TokenRecord
        {
            Id = id.Trim(),
            Name = name.Trim(),
            Token = GenerateToken(),
            Role = role,
            CreatedAt = DateTimeOffset.UtcNow
        };
        lock (_gate)
        {
            if (_tokens.Any(t => string.Equals(t.Id, rec.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Token id already exists: " + rec.Id);
            _tokens.Add(rec);
        }
        Save();
        return rec;   // Returned exactly once — caller MUST capture Token; later listings only show the prefix.
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var i = _tokens.FindIndex(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));
            if (i < 0) return false;
            _tokens.RemoveAt(i);
        }
        Save();
        return true;
    }

    private static string TokenPrefix(string token) =>
        token.Length <= 8 ? "********" : token.Substring(0, 8) + "…";

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static bool CryptoEquals(string a, string b)
    {
        if (a.Length != b.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
