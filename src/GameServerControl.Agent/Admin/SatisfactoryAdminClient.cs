using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Admin;

/// <summary>
/// HTTPS Admin API client for Satisfactory dedicated servers.
///
/// Wire protocol: POST https://&lt;server&gt;:7777/api/v1 with body
///   {"function":"&lt;FunctionName&gt;","data":{...}}
/// Self-signed cert; we don't validate.
///
/// Auth: PasswordLogin with the admin password returns a bearer token that
/// must be sent in the Authorization header for everything else.
/// Token cache is per-server; on 401 we drop it and the next call re-auths.
///
/// We reuse <see cref="ServerDef.RconPassword"/> as the admin password —
/// semantically it's "the server's admin auth password" and avoids adding
/// a new field that only one game uses.
/// </summary>
public sealed class SatisfactoryAdminClient
{
    private readonly ILogger<SatisfactoryAdminClient> _logger;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _tokenByServerId = new();

    public SatisfactoryAdminClient(ILogger<SatisfactoryAdminClient> logger)
    {
        _logger = logger;
        var handler = new HttpClientHandler
        {
            // Server presents a self-signed cert that we explicitly trust.
            // This is safe because the agent and game server share the same host.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    // We co-host the agent and game server on the same box, so loopback is fine
    // and avoids the public-IP hairpin issue the user hit yesterday.
    private static string Endpoint(ServerDef def) => "https://127.0.0.1:7777/api/v1";

    /// <summary>Drop the cached token so the next call re-authenticates.</summary>
    public void InvalidateToken(string serverId) => _tokenByServerId.TryRemove(serverId, out _);

    /// <summary>True if the server has been claimed (and therefore needs a password to admin).</summary>
    public async Task<bool> IsClaimedAsync(ServerDef def, CancellationToken ct)
    {
        var resp = await PostAsync(def, new
        {
            function = "PasswordlessLogin",
            data = new { MinimumPrivilegeLevel = "InitialAdmin" }
        }, null, ct);
        // An unclaimed server returns a valid token here; a claimed server returns
        // errorCode "passwordless_login_not_possible".
        return resp?["data"]?["authenticationToken"] is null;
    }

    private async Task<string?> GetTokenAsync(ServerDef def, CancellationToken ct)
    {
        if (_tokenByServerId.TryGetValue(def.Id, out var cached) && !string.IsNullOrEmpty(cached))
            return cached;
        if (string.IsNullOrEmpty(def.RconPassword))
        {
            _logger.LogWarning("Satisfactory admin password not set for {Id} (servers.json RconPassword is empty)", def.Id);
            return null;
        }
        var resp = await PostAsync(def, new
        {
            function = "PasswordLogin",
            data = new
            {
                MinimumPrivilegeLevel = "Administrator",
                Password = def.RconPassword
            }
        }, null, ct);
        var token = resp?["data"]?["authenticationToken"]?.ToString();
        if (!string.IsNullOrEmpty(token))
        {
            _tokenByServerId[def.Id] = token;
            return token;
        }
        _logger.LogError("PasswordLogin failed for {Id}: {Err}",
            def.Id, resp?["errorCode"]?.ToString() ?? "unknown");
        return null;
    }

    // ---- domain methods ----

    public async Task<JsonObject?> GetAdvancedGameSettingsAsync(ServerDef def, CancellationToken ct)
    {
        var token = await GetTokenAsync(def, ct);
        if (token is null) return null;
        var resp = await PostAsync(def, new { function = "GetAdvancedGameSettings" }, token, ct);
        // Response shape: { "data": { "creativeModeEnabled": bool, "advancedGameSettings": {...} } }
        return resp?["data"]?["advancedGameSettings"] as JsonObject;
    }

    public async Task<bool> ApplyAdvancedGameSettingsAsync(
        ServerDef def, IReadOnlyDictionary<string, object> settings, CancellationToken ct)
    {
        var token = await GetTokenAsync(def, ct);
        if (token is null) return false;
        var resp = await PostAsync(def, new
        {
            function = "ApplyAdvancedGameSettings",
            data = new { AppliedAdvancedGameSettings = settings }
        }, token, ct);
        var err = resp?["errorCode"]?.ToString();
        if (!string.IsNullOrEmpty(err))
        {
            _logger.LogWarning("ApplyAdvancedGameSettings({Keys}) returned {Err}",
                string.Join(",", settings.Keys), err);
            return false;
        }
        return true;
    }

    public async Task<string?> QueryServerNameAsync(ServerDef def, CancellationToken ct)
    {
        var token = await GetTokenAsync(def, ct);
        if (token is null) return null;
        var resp = await PostAsync(def, new { function = "QueryServerState" }, token, ct);
        // Best-effort over a couple of shapes the API has used across versions.
        return resp?["data"]?["serverGameState"]?["activeSessionName"]?.ToString()
            ?? resp?["data"]?["serverGameState"]?["serverName"]?.ToString()
            ?? resp?["data"]?["serverName"]?.ToString();
    }

    public async Task<bool> RenameServerAsync(ServerDef def, string newName, CancellationToken ct)
    {
        var token = await GetTokenAsync(def, ct);
        if (token is null) return false;
        var resp = await PostAsync(def, new
        {
            function = "RenameServer",
            data = new { ServerName = newName }
        }, token, ct);
        return resp?["errorCode"] is null;
    }

    public async Task<bool> SetClientPasswordAsync(ServerDef def, string password, CancellationToken ct)
    {
        var token = await GetTokenAsync(def, ct);
        if (token is null) return false;
        var resp = await PostAsync(def, new
        {
            function = "SetClientPassword",
            data = new { Password = password ?? "" }
        }, token, ct);
        return resp?["errorCode"] is null;
    }

    public async Task<bool> ClaimServerAsync(ServerDef def, string serverName, string adminPassword, CancellationToken ct)
    {
        // Step 1: passwordless login to get an InitialAdmin token (only works on unclaimed servers)
        var resp = await PostAsync(def, new
        {
            function = "PasswordlessLogin",
            data = new { MinimumPrivilegeLevel = "InitialAdmin" }
        }, null, ct);
        var initialToken = resp?["data"]?["authenticationToken"]?.ToString();
        if (string.IsNullOrEmpty(initialToken))
        {
            _logger.LogError("PasswordlessLogin failed: {Err}", resp?["errorCode"]?.ToString() ?? "unknown");
            return false;
        }
        // Step 2: ClaimServer with desired name + new admin password
        var claim = await PostAsync(def, new
        {
            function = "ClaimServer",
            data = new { ServerName = serverName, AdminPassword = adminPassword }
        }, initialToken, ct);
        return claim?["errorCode"] is null;
    }

    // ---- transport ----

    private async Task<JsonObject?> PostAsync(ServerDef def, object body, string? authToken, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(body, new JsonSerializerOptions
            {
                // Satisfactory's API is case-sensitive on field names — match its conventions exactly.
                PropertyNamingPolicy = null,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint(def))
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrEmpty(authToken))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

            using var resp = await _http.SendAsync(req, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                // Token went stale (server restart, password changed, etc.); drop it.
                _tokenByServerId.TryRemove(def.Id, out _);
            }
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Satisfactory API POST failed for {Id}", def.Id);
            return null;
        }
    }
}
