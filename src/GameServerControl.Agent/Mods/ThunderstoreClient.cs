using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Read-only client for the Thunderstore /api/v1/package/ endpoint.
///
/// Each game community has its own Thunderstore subdomain (valheim.thunderstore.io,
/// satisfactory.thunderstore.io, etc.). One catalog call returns the full mod list for
/// that community — typically a few MB of JSON. We cache the parsed result per community
/// with a soft TTL so user searches don't hammer Thunderstore.
///
/// Search is in-memory substring matching across name + owner + description, ranked by
/// download count. No pagination — limit is enforced on the result side (default 30).
/// </summary>
public sealed class ThunderstoreClient
{
    private readonly HttpClient _http;
    private readonly ILogger<ThunderstoreClient> _logger;
    private readonly Dictionary<string, CachedCatalog> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public ThunderstoreClient(ILogger<ThunderstoreClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.0 (mod-search)");
        _http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Searches the catalog for a community. <paramref name="community"/> is the
    /// Thunderstore subdomain (e.g. "valheim" for valheim.thunderstore.io).
    /// </summary>
    public async Task<ModSearchResult[]> SearchAsync(string community, string query, int limit, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(community, ct);
        if (catalog is null) return Array.Empty<ModSearchResult>();

        var q = (query ?? "").Trim();
        IEnumerable<ThunderstorePackage> matches = catalog;
        if (q.Length > 0)
        {
            matches = catalog.Where(p =>
                Contains(p.Name, q) ||
                Contains(p.Owner, q) ||
                Contains(p.Versions?.FirstOrDefault()?.Description, q) ||
                (p.Categories ?? Array.Empty<string>()).Any(c => Contains(c, q)));
        }
        // Hide deprecated unless the user explicitly searched for one
        if (q.Length > 0 && !q.Contains("deprecated", StringComparison.OrdinalIgnoreCase))
            matches = matches.Where(p => !p.IsDeprecated);

        return matches
            .OrderByDescending(p => p.Versions?.FirstOrDefault()?.Downloads ?? 0)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(ToResult)
            .ToArray();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static ModSearchResult ToResult(ThunderstorePackage p)
    {
        var v = p.Versions?.FirstOrDefault();
        return new ModSearchResult(
            Name: p.Name ?? "",
            Owner: p.Owner ?? "",
            Version: v?.VersionNumber ?? "?",
            Description: v?.Description,
            IconUrl: v?.Icon,
            DownloadUrl: v?.DownloadUrl ?? "",
            PackageUrl: p.PackageUrl ?? "",
            Downloads: v?.Downloads ?? 0,
            RatingScore: p.RatingScore,
            Categories: p.Categories ?? Array.Empty<string>(),
            Deprecated: p.IsDeprecated);
    }

    private async Task<ThunderstorePackage[]?> GetCatalogAsync(string community, CancellationToken ct)
    {
        // Fast-path: cached + fresh
        if (_cache.TryGetValue(community, out var cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return cached.Packages;

        await _gate.WaitAsync(ct);
        try
        {
            // Double-check inside the lock — another caller may have just refreshed
            if (_cache.TryGetValue(community, out cached) && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
                return cached.Packages;

            var url = $"https://{community}.thunderstore.io/api/v1/package/";
            _logger.LogInformation("Fetching Thunderstore catalog: {Url}", url);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Thunderstore catalog HTTP {Code} for {Url}", (int)resp.StatusCode, url);
                // Return stale cache if available rather than dropping search entirely
                return cached?.Packages;
            }
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var packages = await JsonSerializer.DeserializeAsync<ThunderstorePackage[]>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            packages ??= Array.Empty<ThunderstorePackage>();
            _cache[community] = new CachedCatalog(packages, DateTime.UtcNow);
            _logger.LogInformation("Cached {Count} Thunderstore packages for {Community}", packages.Length, community);
            return packages;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Thunderstore catalog fetch failed for {Community}", community);
            return _cache.TryGetValue(community, out var stale) ? stale.Packages : null;
        }
        finally { _gate.Release(); }
    }

    private sealed record CachedCatalog(ThunderstorePackage[] Packages, DateTime FetchedAt);

    // ---- Thunderstore API DTOs (only the fields we actually use) ----

    public sealed class ThunderstorePackage
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("owner")] public string? Owner { get; set; }
        [JsonPropertyName("package_url")] public string? PackageUrl { get; set; }
        [JsonPropertyName("rating_score")] public int RatingScore { get; set; }
        [JsonPropertyName("is_pinned")] public bool IsPinned { get; set; }
        [JsonPropertyName("is_deprecated")] public bool IsDeprecated { get; set; }
        [JsonPropertyName("categories")] public string[]? Categories { get; set; }
        [JsonPropertyName("versions")] public ThunderstoreVersion[]? Versions { get; set; }
    }

    public sealed class ThunderstoreVersion
    {
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("icon")] public string? Icon { get; set; }
        [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
        [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
    }
}
