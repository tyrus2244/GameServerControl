using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Client.Services;

public sealed class AgentClient
{
    private readonly HttpClient _http;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public AgentClient(string baseUrl, string token)
    {
        var handler = new HttpClientHandler
        {
            // Tailnet is already E2E-encrypted by WireGuard; accept the agent's self-signed cert.
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(35) };
        if (!string.IsNullOrEmpty(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync("/api/health", ct);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public async Task<List<ServerDef>> ListServersAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync("/api/servers", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ServerDef>>(s, JsonOpts) ?? new();
    }

    public async Task<DiscoverResponse> DiscoverServersAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync("/api/discover", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<DiscoverResponse>(s, JsonOpts) ?? new(Array.Empty<DiscoveredServer>(), Array.Empty<string>());
    }

    public async Task<ModListResponse> ListModsAsync(string serverId, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{serverId}/mods", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModListResponse>(s, JsonOpts)
            ?? new(Array.Empty<ModInfo>(), false, "no response", null);
    }

    public async Task<ModInstallResult> InstallModAsync(string serverId, string url, string? displayName, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new ModInstallRequest(url, displayName), JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{serverId}/mods/install",
            new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModInstallResult>(body, JsonOpts)
            ?? new ModInstallResult(false, null, "no response");
    }

    public async Task<bool> UninstallModAsync(string serverId, string modId, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"/api/servers/{serverId}/mods/{Uri.EscapeDataString(modId)}", ct);
        return r.IsSuccessStatusCode;
    }

    public async Task<ModUpdatesResponse> CheckModUpdatesAsync(string serverId, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{serverId}/mods/updates", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModUpdatesResponse>(s, JsonOpts)
            ?? new ModUpdatesResponse(Array.Empty<ModUpdateInfo>(), false, "no response");
    }

    public async Task<ModInstallResult> UpdateModAsync(string serverId, string modId, string url, string? displayName, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(new ModInstallRequest(url, displayName), JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{serverId}/mods/{Uri.EscapeDataString(modId)}/update",
            new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModInstallResult>(body, JsonOpts) ?? new ModInstallResult(false, null, "no response");
    }

    public async Task<ModSearchResponse> SearchModsAsync(string serverId, string query, int limit = 30, bool serverSideOnly = true, CancellationToken ct = default)
    {
        var u = $"/api/servers/{serverId}/mods/search?q={Uri.EscapeDataString(query)}&limit={limit}&serverSideOnly={(serverSideOnly ? "true" : "false")}";
        var r = await _http.GetAsync(u, ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ModSearchResponse>(s, JsonOpts)
            ?? new(Array.Empty<ModSearchResult>(), false, null, "no response");
    }

    public async Task<ServerStatus?> GetStatusAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/status", ct);
        if (!r.IsSuccessStatusCode) return null;
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ServerStatus>(s, JsonOpts);
    }

    public async Task<List<ServerStatus>> GetAllStatusAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync("/api/status", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<ServerStatus>>(s, JsonOpts) ?? new();
    }

    public async Task<ServerDef> CreateServerAsync(ServerDef def, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(def, JsonOpts);
        var r = await _http.PostAsync("/api/servers", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ServerDef>(body, JsonOpts) ?? throw new InvalidOperationException("Empty body");
    }

    public async Task<ServerDef> UpdateServerAsync(string id, ServerDef def, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(def, JsonOpts);
        var r = await _http.PutAsync($"/api/servers/{id}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {body}");
        return JsonSerializer.Deserialize<ServerDef>(body, JsonOpts) ?? throw new InvalidOperationException("Empty body");
    }

    public async Task DeleteServerAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"/api/servers/{id}", ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
    }

    public sealed record ConfigPayload(ConfigSchema? Schema, Dictionary<string, string> Values);

    public async Task<ConfigPayload> GetServerConfigAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/config", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<ConfigPayload>(s, JsonOpts) ?? new ConfigPayload(null, new());
    }

    public async Task PutServerConfigAsync(string id, Dictionary<string, string> values, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(values, JsonOpts);
        var r = await _http.PutAsync($"/api/servers/{id}/config", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<RconPlayer[]> RconListPlayersAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/rcon/players", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<RconPlayer[]>(s, JsonOpts) ?? Array.Empty<RconPlayer>();
    }

    public async Task<RconResponse> RconRunAsync(string id, RconStandardCommand cmd, string? payload, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { Command = cmd.ToString(), Payload = payload }, JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{id}/rcon/command",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
            return new RconResponse(false, "", $"HTTP {(int)r.StatusCode}: {s}");
        return JsonSerializer.Deserialize<RconResponse>(s, JsonOpts) ?? new RconResponse(false, "", "Empty response");
    }

    public async Task<List<BackupInfo>> ListBackupsAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/backups", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<BackupInfo>>(s, JsonOpts) ?? new();
    }

    public async Task<ActionResult> RestoreBackupAsync(string id, string backupName, CancellationToken ct = default)
    {
        var r = await _http.PostAsync($"/api/servers/{id}/backups/{Uri.EscapeDataString(backupName)}/restore",
            new StringContent(""), ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
            return new ActionResult(false, $"HTTP {(int)r.StatusCode}: {s}", null);
        return JsonSerializer.Deserialize<ActionResult>(s, JsonOpts) ?? new ActionResult(false, "Empty response", null);
    }

    public async Task<bool> DeleteBackupAsync(string id, string backupName, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"/api/servers/{id}/backups/{Uri.EscapeDataString(backupName)}", ct);
        return r.IsSuccessStatusCode;
    }

    public async Task<MaintenanceSchedule?> GetScheduleAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/schedule", ct);
        if (!r.IsSuccessStatusCode) return null;
        var s = await r.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(s) || s == "null") return null;
        return JsonSerializer.Deserialize<MaintenanceSchedule>(s, JsonOpts);
    }

    public async Task SetScheduleAsync(string id, MaintenanceSchedule schedule, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(schedule, JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{id}/schedule",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<InstallJobAck> InstallServerAsync(InstallServerRequest req, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(req, JsonOpts);
        var r = await _http.PostAsync("/api/servers/install",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {s}");
        return JsonSerializer.Deserialize<InstallJobAck>(s, JsonOpts) ?? throw new InvalidOperationException("Empty install ack");
    }

    public async Task<InstallProgress?> GetInstallProgressAsync(string jobId, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/install/{jobId}", ct);
        if (!r.IsSuccessStatusCode) return null;
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<InstallProgress>(s, JsonOpts);
    }

    public async Task<UpdateStatus?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var r = await _http.GetAsync("/api/version", ct);
            if (!r.IsSuccessStatusCode) return null;
            var s = await r.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<UpdateStatus>(s, JsonOpts);
        }
        catch { return null; }
    }

    public async Task<List<TokenMetadata>> ListTokensAsync(CancellationToken ct = default)
    {
        var r = await _http.GetAsync("/api/tokens", ct);
        r.EnsureSuccessStatusCode();
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<List<TokenMetadata>>(s, JsonOpts) ?? new();
    }

    public async Task<TokenMetadata> CreateTokenAsync(string id, string name, TokenRole role, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new CreateTokenRequest(id, name, role), JsonOpts);
        var r = await _http.PostAsync("/api/tokens",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {s}");
        return JsonSerializer.Deserialize<TokenMetadata>(s, JsonOpts) ?? throw new InvalidOperationException("Empty body");
    }

    public async Task<bool> DeleteTokenAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"/api/tokens/{Uri.EscapeDataString(id)}", ct);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> TestDiscordWebhookAsync(string webhookUrl, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new DiscordWebhookTestRequest(webhookUrl), JsonOpts);
        var r = await _http.PostAsync("/api/discord/test",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        return r.IsSuccessStatusCode;
    }

    public async Task ClearScheduleAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.DeleteAsync($"/api/servers/{id}/schedule", ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
    }

    public sealed record AutostartStatus(bool Supported, bool? Enabled);

    public async Task<AutostartStatus> GetAutostartAsync(string id, CancellationToken ct = default)
    {
        var r = await _http.GetAsync($"/api/servers/{id}/autostart", ct);
        if (!r.IsSuccessStatusCode) return new AutostartStatus(false, null);
        var s = await r.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<AutostartStatus>(s, JsonOpts) ?? new AutostartStatus(false, null);
    }

    public async Task SetAutostartAsync(string id, bool enabled, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { Enabled = enabled }, JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{id}/autostart",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        if (!r.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)r.StatusCode}: {await r.Content.ReadAsStringAsync(ct)}");
    }

    public async Task<bool> StartLogTailAsync(string id, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { Action = "start" }, JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{id}/logs/tail",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> StopLogTailAsync(string id, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { Action = "stop" }, JsonOpts);
        var r = await _http.PostAsync($"/api/servers/{id}/logs/tail",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), ct);
        return r.IsSuccessStatusCode;
    }

    public async Task<ActionResult> ActionAsync(string id, ServerActionKind kind, bool stopVm = true, bool force = false, CancellationToken ct = default)
    {
        var path = kind switch
        {
            ServerActionKind.Start => $"/api/servers/{id}/start",
            ServerActionKind.Stop => $"/api/servers/{id}/stop?stopVm={stopVm}&force={force}",
            ServerActionKind.Restart => $"/api/servers/{id}/restart",
            ServerActionKind.Backup => $"/api/servers/{id}/backup",
            ServerActionKind.Update => $"/api/servers/{id}/update",
            ServerActionKind.ApplyConfig => $"/api/servers/{id}/apply",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var r = await _http.PostAsync(path, new StringContent(""), ct);
        var s = await r.Content.ReadAsStringAsync(ct);
        if (!r.IsSuccessStatusCode)
            return new ActionResult(false, $"HTTP {(int)r.StatusCode}: {s}", null);
        return JsonSerializer.Deserialize<ActionResult>(s, JsonOpts) ?? new ActionResult(false, "Empty response", null);
    }
}
