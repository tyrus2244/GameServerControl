using System.Net.Http;
using System.Text;
using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Notifications;

/// <summary>
/// Posts game-server lifecycle events to a Discord channel webhook URL stored per-server.
///
/// Webhook URL format: https://discord.com/api/webhooks/&lt;id&gt;/&lt;token&gt;
/// Discord accepts a JSON body with `username`, `content`, and an `embeds` array; we send a
/// single embed per event so the message is colored (green=up, red=down, yellow=warn, blue=info).
///
/// Failures are swallowed — Discord being down should never block a server start.
/// </summary>
public sealed class DiscordNotifier
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly ILogger<DiscordNotifier> _logger;

    public DiscordNotifier(ILogger<DiscordNotifier> logger)
    {
        _logger = logger;
    }

    public enum Kind { Up, Down, Warn, Info }

    public bool IsConfigured(ServerDef def) => !string.IsNullOrWhiteSpace(def.DiscordWebhookUrl);

    public async Task SendAsync(ServerDef def, Kind kind, string title, string description, CancellationToken ct = default)
    {
        if (!IsConfigured(def)) return;
        var color = kind switch
        {
            Kind.Up   => 0x3FFF8E,  // green
            Kind.Down => 0xFF5C6A,  // red
            Kind.Warn => 0xFFC857,  // yellow
            _         => 0x66D9FF   // blue
        };
        var payload = new
        {
            username = "Game Server Control",
            embeds = new[]
            {
                new
                {
                    title,
                    description,
                    color,
                    footer = new { text = def.Name + " · " + def.GameType },
                    timestamp = DateTime.UtcNow.ToString("o")
                }
            }
        };
        var body = JsonSerializer.Serialize(payload);
        try
        {
            using var resp = await _http.PostAsync(def.DiscordWebhookUrl,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            // Discord returns 204 No Content on success
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Discord webhook for {Id} returned {Code}", def.Id, (int)resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Discord webhook for {Id} failed", def.Id);
        }
    }

    /// <summary>Direct send to a URL — used by the "test webhook" endpoint before the URL is saved.</summary>
    public async Task SendRawAsync(string webhookUrl, string title, string description, CancellationToken ct = default)
    {
        var payload = new
        {
            username = "Game Server Control",
            embeds = new[]
            {
                new { title, description, color = 0x66D9FF, timestamp = DateTime.UtcNow.ToString("o") }
            }
        };
        var body = JsonSerializer.Serialize(payload);
        using var resp = await _http.PostAsync(webhookUrl,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Discord HTTP {(int)resp.StatusCode}: " +
                await resp.Content.ReadAsStringAsync(ct));
    }
}
