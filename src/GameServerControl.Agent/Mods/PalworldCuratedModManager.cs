using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Palworld has no centralized marketplace (no Thunderstore community, no ficsit-equivalent),
/// so we curate a small hand-picked list of well-known server-side server tools and resolve
/// their latest release at install time via the GitHub Releases API.
///
/// Each entry declares where its file goes on disk (DLL → Pal\Binaries\Win64\,
/// .pak → Pal\Content\Paks\~mods\, etc.). The Install action downloads the asset, places it,
/// and records what was placed in BinPath\.gsc-mods.json so uninstall is precise.
///
/// ⚠ Entries here are best-effort: I picked the canonical GitHub repos at time of writing,
/// but Palworld modding moves fast and forks/abandonments happen. If a curated entry's
/// repo 404s or its releases have a different asset shape, install fails with a clear
/// error and we can fix the entry without breaking the rest of the catalog.
///
/// To add more: extend <see cref="CuratedList"/> below with a new <see cref="CuratedEntry"/>.
/// </summary>
public sealed class PalworldCuratedModManager : IModManager
{
    private readonly ILogger<PalworldCuratedModManager> _logger;
    private readonly HttpClient _http;
    private readonly GitHubReleasesClient _gh;

    public PalworldCuratedModManager(ILogger<PalworldCuratedModManager> logger, GitHubReleasesClient gh)
    {
        _logger = logger;
        _gh = gh;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl-ModInstaller/1.0");
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric && def.SteamAppId == "2394010";

    public string ModsFolder(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "Pal", "Binaries", "Win64");

    public string? MarketplaceSource(ServerDef def) => "Curated (Palguard, etc. — via GitHub Releases)";

    // ---- the curated list ----

    private enum InstallKind
    {
        Win64Dll,         // Drop the asset into Pal\Binaries\Win64\<filename>
        Win64ZipExtract,  // Unzip into Pal\Binaries\Win64\
        PaksExtract,      // Unzip into Pal\Content\Paks\~mods\
    }

    private sealed record CuratedEntry(
        string ModId,                 // stable internal ID + folder/file name we track
        string DisplayName,
        string Description,
        string Owner,
        string Repo,                  // GitHub owner/repo
        string? AssetNameContains,    // substring filter to pick the right release asset
        InstallKind Kind,
        bool ServerSideOnly,
        string[] Categories);

    private static readonly CuratedEntry[] CuratedList =
    {
        new(ModId: "Palguard",
            DisplayName: "Palguard",
            Description: "Server admin tools — kick, ban, broadcast, save management, anti-cheat helpers. Drop-in DLL hook; clients don't need to install anything.",
            Owner: "magicbots",
            Repo: "PalGuard",
            AssetNameContains: ".zip",
            Kind: InstallKind.Win64ZipExtract,
            ServerSideOnly: true,
            Categories: new[] { "Admin", "Anti-cheat", "Server-side" }),

        new(ModId: "PalSchema",
            DisplayName: "PalSchema",
            Description: "Data-mod framework for Palworld dedicated servers. Lets other mods modify pals/items via JSON without binary patches. Server-only.",
            Owner: "Okaetsu",
            Repo: "PalSchema",
            AssetNameContains: ".zip",
            Kind: InstallKind.Win64ZipExtract,
            ServerSideOnly: true,
            Categories: new[] { "Framework", "Server-side" }),

        // Add more here as you verify them: { ModId, DisplayName, Description, Owner, Repo, AssetNameContains, Kind, ServerSideOnly, Categories }
    };

    // ---- search ----

    public async Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct)
    {
        var q = (query ?? "").Trim();
        var rows = CuratedList.AsEnumerable();
        if (q.Length > 0)
        {
            rows = rows.Where(e =>
                e.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                e.Owner.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        if (serverSideOnly) rows = rows.Where(e => e.ServerSideOnly);

        // Resolve each entry's latest release in parallel so the search returns fast.
        // GitHub gets cached in GitHubReleasesClient so the second call is instant.
        var resolved = await Task.WhenAll(rows.Select(async e =>
        {
            var rel = await _gh.GetLatestAsync(e.Owner, e.Repo, e.AssetNameContains, ct);
            return new ModSearchResult(
                Name: e.DisplayName,
                Owner: e.Owner,
                Version: rel?.Tag ?? "(latest)",
                Description: e.Description,
                IconUrl: $"https://github.com/{e.Owner}.png",   // fallback — author avatar
                DownloadUrl: rel?.AssetUrl ?? "",
                PackageUrl: $"https://github.com/{e.Owner}/{e.Repo}",
                Downloads: 0,
                RatingScore: 0,
                Categories: e.Categories,
                Deprecated: false,
                ServerSideOnly: e.ServerSideOnly);
        }));
        return resolved.Where(r => !string.IsNullOrEmpty(r.DownloadUrl)).Take(limit).ToArray();
    }

    // ---- list ----

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var meta = LoadMetadata(def);
        var rows = new List<ModInfo>();
        foreach (var (id, sidecar) in meta)
        {
            long size = 0;
            try
            {
                foreach (var rel in sidecar.Files ?? Array.Empty<string>())
                {
                    var abs = Path.Combine(def.GuestWorkingDir, rel);
                    if (File.Exists(abs)) size += new FileInfo(abs).Length;
                }
            }
            catch { /* tolerate */ }
            rows.Add(new ModInfo(
                ModId: id,
                DisplayName: sidecar.DisplayName ?? id,
                Version: sidecar.Version,
                Source: sidecar.Source,
                InstalledAt: sidecar.InstalledAt,
                SizeBytes: size,
                Files: sidecar.Files));
        }
        return Task.FromResult(rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // ---- install ----

    public async Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new ModInstallResult(false, null, "URL must be an http(s) link.");

        // Match the URL back to a curated entry so we know where to put the files. If the
        // user installs from a URL not in the catalog, we fall back to "drop into Win64".
        var entry = CuratedList.FirstOrDefault(e =>
            url.Contains($"{e.Owner}/{e.Repo}", StringComparison.OrdinalIgnoreCase));
        var kind = entry?.Kind ?? InstallKind.Win64ZipExtract;
        var modId = entry?.ModId ?? displayName ?? Path.GetFileNameWithoutExtension(uri.Segments.LastOrDefault() ?? "mod");

        var tmpRoot = Path.Combine(Path.GetTempPath(), "gsc-mod-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmpRoot);
        var downloadPath = Path.Combine(tmpRoot, "download.bin");

        try
        {
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return new ModInstallResult(false, null, $"Download returned HTTP {(int)resp.StatusCode}.");
                await using var fs = File.Create(downloadPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            var installRoot = kind switch
            {
                InstallKind.PaksExtract => Path.Combine(def.GuestWorkingDir, "Pal", "Content", "Paks", "~mods"),
                _                       => Path.Combine(def.GuestWorkingDir, "Pal", "Binaries", "Win64"),
            };
            Directory.CreateDirectory(installRoot);

            var installedFiles = new List<string>();
            if (kind == InstallKind.Win64Dll)
            {
                // Single DLL: copy verbatim
                var dst = Path.Combine(installRoot, Path.GetFileName(uri.LocalPath));
                File.Copy(downloadPath, dst, overwrite: true);
                installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, dst));
            }
            else
            {
                // Zip extract — preserve directory layout
                var extract = Path.Combine(tmpRoot, "extract");
                try { ZipFile.ExtractToDirectory(downloadPath, extract, overwriteFiles: true); }
                catch (Exception ex) { return new ModInstallResult(false, null, "Asset isn't a valid zip: " + ex.Message); }
                foreach (var f in Directory.EnumerateFiles(extract, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(extract, f);
                    var dst = Path.Combine(installRoot, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(f, dst, overwrite: true);
                    installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, dst));
                }
            }

            var version = entry is not null
                ? (await _gh.GetLatestAsync(entry.Owner, entry.Repo, entry.AssetNameContains, ct))?.Tag
                : null;
            var info = new ModInfo(
                ModId: modId,
                DisplayName: entry?.DisplayName ?? modId,
                Version: version,
                Source: url,
                InstalledAt: DateTimeOffset.UtcNow,
                SizeBytes: installedFiles.Select(f => new FileInfo(Path.Combine(def.GuestWorkingDir, f)).Length).Sum(),
                Files: installedFiles.ToArray());
            UpdateMetadata(def, m => m[modId] = ToSidecar(info));
            return new ModInstallResult(true, info, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Palworld mod install failed for {Url}", url);
            return new ModInstallResult(false, null, ex.Message);
        }
        finally
        {
            try { Directory.Delete(tmpRoot, recursive: true); } catch { /* ignore */ }
        }
    }

    // ---- uninstall ----

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        var meta = LoadMetadata(def);
        if (!meta.TryGetValue(modId, out var sidecar)) return Task.FromResult(false);
        var ok = false;
        foreach (var rel in sidecar.Files ?? Array.Empty<string>())
        {
            var abs = Path.Combine(def.GuestWorkingDir, rel);
            try { if (File.Exists(abs)) { File.Delete(abs); ok = true; } } catch { /* ignore */ }
        }
        UpdateMetadata(def, m => m.Remove(modId));
        return Task.FromResult(ok);
    }

    // ---- metadata sidecar ----

    private static string MetadataPath(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "Pal", ".gsc-mods.json");

    private sealed record SidecarEntry(string? DisplayName, string? Version, string? Source, DateTimeOffset? InstalledAt, string[]? Files);
    private static SidecarEntry ToSidecar(ModInfo info) =>
        new(info.DisplayName, info.Version, info.Source, info.InstalledAt, info.Files);

    private Dictionary<string, SidecarEntry> LoadMetadata(ServerDef def)
    {
        var path = MetadataPath(def);
        if (!File.Exists(path)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, SidecarEntry>>(File.ReadAllText(path));
            return dict is null ? new(StringComparer.OrdinalIgnoreCase) : new(dict, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private void UpdateMetadata(ServerDef def, Action<Dictionary<string, SidecarEntry>> mutate)
    {
        var path = MetadataPath(def);
        var dict = LoadMetadata(def);
        mutate(dict);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Palworld mod sidecar write failed at {Path}", path);
        }
    }
}
