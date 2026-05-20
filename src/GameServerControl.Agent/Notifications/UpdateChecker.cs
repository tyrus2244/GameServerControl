using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Notifications;

/// <summary>
/// Polls the GitHub Releases API once a day and exposes the latest known version. The
/// dashboards check /api/version on connect and show an "update available" banner if
/// the cached latest is newer than the running InformationalVersion.
/// </summary>
public sealed class UpdateChecker : IHostedService, IDisposable
{
    private static readonly HttpClient _http = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);

    private readonly ILogger<UpdateChecker> _logger;
    private readonly string _repo;
    private readonly string _runningVersion;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public UpdateStatus Latest { get; private set; }

    public UpdateChecker(IConfiguration cfg, ILogger<UpdateChecker> logger)
    {
        _logger = logger;
        _repo = cfg["Agent:Repo"] ?? "tyrus2244/GameServerControl";
        _runningVersion = typeof(UpdateChecker).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        Latest = new UpdateStatus(
            CurrentVersion: _runningVersion,
            LatestVersion: null,
            LatestUrl: null,
            UpdateAvailable: false,
            CheckedAt: null);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
            _http.DefaultRequestHeaders.UserAgent.ParseAdd($"gsc-update-checker/{_runningVersion}");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loop = Task.Run(() => PollLoop(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        if (_loop is not null) try { await _loop.WaitAsync(ct); } catch { /* swallow */ }
    }

    private async Task PollLoop(CancellationToken ct)
    {
        // Initial delay so we don't hammer GitHub on every restart loop.
        try { await Task.Delay(InitialDelay, ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await CheckAsync(ct);
            try { await Task.Delay(PollInterval, ct); } catch { return; }
        }
    }

    public async Task<UpdateStatus> CheckAsync(CancellationToken ct)
    {
        try
        {
            var url = $"https://api.github.com/repos/{_repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("GitHub Releases API returned {Code} (rate limited? unauthenticated cap is 60/hr).", (int)resp.StatusCode);
                return Latest;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            if (release?.TagName is null) return Latest;

            var latestVersion = NormalizeTag(release.TagName);
            var newer = CompareVersions(latestVersion, _runningVersion) > 0;
            Latest = new UpdateStatus(
                CurrentVersion: _runningVersion,
                LatestVersion: latestVersion,
                LatestUrl: release.HtmlUrl,
                UpdateAvailable: newer,
                CheckedAt: DateTimeOffset.UtcNow);
            _logger.LogInformation("Update check: running {Cur}, latest {Lat} (update {Av})",
                _runningVersion, latestVersion, newer ? "AVAILABLE" : "not needed");
            return Latest;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed (non-fatal).");
            return Latest;
        }
    }

    private static string NormalizeTag(string tag) =>
        tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;

    // Numeric-segment compare. Falls back to ordinal string compare for prerelease tags.
    private static int CompareVersions(string a, string b)
    {
        var aParts = a.Split('.');
        var bParts = b.Split('.');
        var n = Math.Max(aParts.Length, bParts.Length);
        for (int i = 0; i < n; i++)
        {
            var aHasNum = i < aParts.Length && int.TryParse(aParts[i], out var ai);
            var bHasNum = i < bParts.Length && int.TryParse(bParts[i], out var bi);
            if (!aHasNum || !bHasNum) return string.Compare(a, b, StringComparison.Ordinal);
            ai = aHasNum ? int.Parse(aParts[i]) : 0;
            bi = bHasNum ? int.Parse(bParts[i]) : 0;
            if (ai != bi) return ai.CompareTo(bi);
        }
        return 0;
    }

    public void Dispose() { _cts?.Dispose(); }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
    }
}
