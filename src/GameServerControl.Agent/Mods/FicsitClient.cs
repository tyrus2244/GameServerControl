using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// GraphQL client for ficsit.app — the Satisfactory community's mod marketplace.
/// Far larger catalog than satisfactory.thunderstore.io (which is essentially empty).
///
/// API: POST https://api.ficsit.app/v2/query with a JSON body { "query": "..." }.
/// We send a single GraphQL query for browsing/searching, cache the parsed mods for
/// 30 minutes to avoid hammering the endpoint.
///
/// "Server-side-only" detection: each mod's latest version has a `targets` array
/// (build targets it ships binaries for). Values include "Windows", "WindowsServer",
/// "LinuxServer", "WindowsNoEditor", etc. A mod that ships *only* server targets and
/// no plain-client target is server-side-only. That's our filter.
/// </summary>
public sealed class FicsitClient
{
    private const string Endpoint = "https://api.ficsit.app/v2/query";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private readonly HttpClient _http;
    private readonly ILogger<FicsitClient> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedCatalog? _cache;

    public FicsitClient(ILogger<FicsitClient> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl/1.0 (mod-search)");
    }

    public async Task<ModSearchResult[]> SearchAsync(string query, int limit, bool serverSideOnly, CancellationToken ct)
    {
        var catalog = await GetCatalogAsync(ct);
        if (catalog is null) return Array.Empty<ModSearchResult>();

        var q = (query ?? "").Trim();
        IEnumerable<FicsitMod> matches = catalog;
        if (q.Length > 0)
        {
            matches = catalog.Where(m =>
                Contains(m.Name, q) ||
                Contains(m.ShortDescription, q) ||
                Contains(m.ModReference, q) ||
                (m.Tags ?? Array.Empty<FicsitTag>()).Any(t => Contains(t.Name, q)));
        }
        if (serverSideOnly) matches = matches.Where(IsServerSideOnly);
        matches = matches.Where(m => !m.Hidden);

        return matches
            .OrderByDescending(m => m.Downloads)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(ToResult)
            .ToArray();
    }

    private static bool Contains(string? haystack, string needle) =>
        haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static bool IsServerSideOnly(FicsitMod m)
    {
        var targets = m.LastVersion?.Targets ?? Array.Empty<FicsitTarget>();
        if (targets.Length == 0) return false;
        var hasServer = targets.Any(t => Contains(t.TargetName, "Server"));
        // Plain client targets: "Windows", "Linux", "Mac" — anything that lacks "Server" is a client build.
        var hasClient = targets.Any(t =>
            !string.IsNullOrEmpty(t.TargetName) &&
            !t.TargetName.Contains("Server", StringComparison.OrdinalIgnoreCase));
        return hasServer && !hasClient;
    }

    private static ModSearchResult ToResult(FicsitMod m)
    {
        var v = m.LastVersion;
        // ficsit returns "link" as a relative URL like "/v1/version/<id>/download"
        var dl = string.IsNullOrEmpty(v?.Link) ? "" : "https://api.ficsit.app" + v.Link;
        return new ModSearchResult(
            Name: m.Name ?? m.ModReference ?? "(unknown)",
            Owner: m.Creator?.Username ?? "",
            Version: v?.Version ?? "?",
            Description: m.ShortDescription,
            IconUrl: m.Logo,
            DownloadUrl: dl,
            PackageUrl: $"https://ficsit.app/mod/{m.ModReference}",
            Downloads: m.Downloads,
            RatingScore: 0,
            Categories: (m.Tags ?? Array.Empty<FicsitTag>()).Select(t => t.Name ?? "").Where(s => s.Length > 0).ToArray(),
            Deprecated: m.Hidden,
            ServerSideOnly: IsServerSideOnly(m));
    }

    private async Task<FicsitMod[]?> GetCatalogAsync(CancellationToken ct)
    {
        if (_cache is { } cached && DateTime.UtcNow - cached.FetchedAt < CacheTtl)
            return cached.Mods;

        await _gate.WaitAsync(ct);
        try
        {
            if (_cache is { } again && DateTime.UtcNow - again.FetchedAt < CacheTtl)
                return again.Mods;

            // Fetch up to 200 popular mods. ficsit.app's getMods endpoint paginates;
            // we make one bigger call rather than chasing offsets — search is in-memory
            // so a few hundred mods is plenty for substring filtering.
            const string graphql = @"
            {
              getMods(filter: { limit: 200, offset: 0, order_by: popularity, order: desc, hidden: false }) {
                mods {
                  id
                  mod_reference
                  name
                  short_description
                  logo
                  downloads
                  hidden
                  creator { username }
                  tags { name }
                  last_version {
                    version
                    link
                    targets { targetName }
                  }
                }
              }
            }";

            var body = JsonSerializer.Serialize(new { query = graphql });
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ficsit.app catalog HTTP {Code}", (int)resp.StatusCode);
                return _cache?.Mods;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            var parsed = await JsonSerializer.DeserializeAsync<FicsitResponse>(stream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            var mods = parsed?.Data?.GetMods?.Mods ?? Array.Empty<FicsitMod>();
            _cache = new CachedCatalog(mods, DateTime.UtcNow);
            _logger.LogInformation("Cached {Count} ficsit.app mods", mods.Length);
            return mods;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ficsit.app catalog fetch failed");
            return _cache?.Mods;
        }
        finally { _gate.Release(); }
    }

    private sealed record CachedCatalog(FicsitMod[] Mods, DateTime FetchedAt);

    // ---- ficsit.app GraphQL response DTOs (only the fields we use) ----

    public sealed class FicsitResponse { [JsonPropertyName("data")] public FicsitData? Data { get; set; } }
    public sealed class FicsitData { [JsonPropertyName("getMods")] public FicsitGetMods? GetMods { get; set; } }
    public sealed class FicsitGetMods { [JsonPropertyName("mods")] public FicsitMod[]? Mods { get; set; } }

    public sealed class FicsitMod
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("mod_reference")] public string? ModReference { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("short_description")] public string? ShortDescription { get; set; }
        [JsonPropertyName("logo")] public string? Logo { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("hidden")] public bool Hidden { get; set; }
        [JsonPropertyName("creator")] public FicsitCreator? Creator { get; set; }
        [JsonPropertyName("tags")] public FicsitTag[]? Tags { get; set; }
        [JsonPropertyName("last_version")] public FicsitVersion? LastVersion { get; set; }
    }

    public sealed class FicsitCreator { [JsonPropertyName("username")] public string? Username { get; set; } }
    public sealed class FicsitTag      { [JsonPropertyName("name")] public string? Name { get; set; } }

    public sealed class FicsitVersion
    {
        [JsonPropertyName("version")] public string? Version { get; set; }
        [JsonPropertyName("link")] public string? Link { get; set; }
        [JsonPropertyName("targets")] public FicsitTarget[]? Targets { get; set; }
    }

    public sealed class FicsitTarget { [JsonPropertyName("targetName")] public string? TargetName { get; set; } }
}
