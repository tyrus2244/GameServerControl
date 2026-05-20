using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Minimal client for the GitHub Releases REST API.
///
/// Used by curated catalogs (Palworld) where each entry is identified by a GitHub repo;
/// we resolve the latest release at install time so download URLs don't go stale in code.
///
/// Cached per (owner, repo) for 30 minutes. Unauthenticated — GitHub's anonymous rate
/// limit (60/hr/IP) is plenty for our usage pattern.
/// </summary>
public sealed class GitHubReleasesClient
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private readonly HttpClient _http;
    private readonly ILogger<GitHubReleasesClient> _logger;
    private readonly Dictionary<string, CachedRelease> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GitHubReleasesClient(ILogger<GitHubReleasesClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.0");
        _http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    /// <summary>
    /// Returns the latest non-prerelease release. If <paramref name="assetNameContains"/>
    /// is supplied, only assets whose filename contains that string (case-insensitive) are
    /// considered for the download URL — handy when a release has both server and client
    /// bundles and we only want the server one.
    /// </summary>
    public async Task<GitHubRelease?> GetLatestAsync(string owner, string repo, string? assetNameContains, CancellationToken ct)
    {
        var key = $"{owner}/{repo}";
        if (_cache.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return cached.Release;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
                return cached.Release;

            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("GitHub releases HTTP {Code} for {Owner}/{Repo}", (int)resp.StatusCode, owner, repo);
                _cache[key] = new CachedRelease(null, DateTime.UtcNow);
                return null;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var raw = await JsonSerializer.DeserializeAsync<RawRelease>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            if (raw is null) return null;

            var asset = (raw.Assets ?? Array.Empty<RawAsset>())
                .Where(a => !string.IsNullOrEmpty(a.BrowserDownloadUrl))
                .Where(a => string.IsNullOrEmpty(assetNameContains) ||
                            (a.Name?.Contains(assetNameContains, StringComparison.OrdinalIgnoreCase) ?? false))
                .OrderByDescending(a => a.Size)
                .FirstOrDefault();

            var rel = new GitHubRelease(
                Tag: raw.TagName ?? "?",
                PublishedAt: raw.PublishedAt,
                AssetUrl: asset?.BrowserDownloadUrl,
                AssetName: asset?.Name);
            _cache[key] = new CachedRelease(rel, DateTime.UtcNow);
            return rel;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GitHub releases lookup failed for {Owner}/{Repo}", owner, repo);
            return null;
        }
        finally { _gate.Release(); }
    }

    public sealed record GitHubRelease(string Tag, DateTime? PublishedAt, string? AssetUrl, string? AssetName);
    private sealed record CachedRelease(GitHubRelease? Release, DateTime FetchedAt);

    private sealed class RawRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("published_at")] public DateTime? PublishedAt { get; set; }
        [JsonPropertyName("assets")] public RawAsset[]? Assets { get; set; }
    }
    private sealed class RawAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }
}
