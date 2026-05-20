using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// Manages server-side BepInEx plugin DLLs for Valheim (and any other BepInEx-based
/// dedicated server that ships with a BepInEx folder under the install root).
///
/// Layout on disk:
///   &lt;install&gt;/BepInEx/plugins/         ← .dll files + per-mod folders go here
///   &lt;install&gt;/BepInEx/.gsc-mods.json   ← our metadata sidecar: who, when, where from
///
/// Install flow: HTTP GET the URL → save zip to temp → look for "plugins/" subdir inside
/// the zip (Thunderstore convention) and copy its contents into &lt;install&gt;/BepInEx/plugins.
/// If the zip has no plugins/ subdir but does have top-level .dll files, those get copied
/// instead — handles raw GitHub-release zips of single-DLL mods.
///
/// Uninstall: removes the file or folder named by ModId and updates the sidecar.
/// </summary>
public sealed class ValheimBepInExModManager : IModManager
{
    private readonly ILogger<ValheimBepInExModManager> _logger;
    private readonly HttpClient _http;
    private readonly ThunderstoreClient _thunderstore;

    public ValheimBepInExModManager(ILogger<ValheimBepInExModManager> logger, ThunderstoreClient thunderstore)
    {
        _logger = logger;
        _thunderstore = thunderstore;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("GameServerControl-ModInstaller/1.0");
    }

    public string? MarketplaceSource(ServerDef def) => "valheim.thunderstore.io";

    public Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct)
        => _thunderstore.SearchAsync("valheim", query, limit, serverSideOnly, ct);

    public bool Supports(ServerDef def)
    {
        // Any server whose install dir has a BepInEx folder is fair game. Catches Valheim
        // out-of-the-box and any other BepInEx-using game the user adds.
        if (string.IsNullOrEmpty(def.GuestWorkingDir)) return false;
        return Directory.Exists(Path.Combine(def.GuestWorkingDir, "BepInEx"))
            || def.SteamAppId == "896660"; // Valheim — match even if BepInEx not yet installed (let install work)
    }

    public string ModsFolder(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "BepInEx", "plugins");

    private static string MetadataPath(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "BepInEx", ".gsc-mods.json");

    // ---- list ----

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var dir = ModsFolder(def);
        if (!Directory.Exists(dir)) return Task.FromResult(Array.Empty<ModInfo>());

        var meta = LoadMetadata(def);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ModInfo>();

        // Top-level DLLs (single-file mods)
        foreach (var dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileName(dll);
            seen.Add(id);
            var info = new FileInfo(dll);
            var sidecar = meta.GetValueOrDefault(id);
            result.Add(new ModInfo(
                ModId: id,
                DisplayName: sidecar?.DisplayName ?? Path.GetFileNameWithoutExtension(dll),
                Version: sidecar?.Version,
                Source: sidecar?.Source,
                InstalledAt: sidecar?.InstalledAt ?? info.CreationTimeUtc,
                SizeBytes: info.Length,
                Files: new[] { Path.GetRelativePath(def.GuestWorkingDir, dll) }));
        }

        // Subfolders (multi-file mods)
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            if (seen.Contains(id)) continue;
            var sidecar = meta.GetValueOrDefault(id);
            long size = 0;
            try { size = new DirectoryInfo(sub).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { /* ignore */ }
            result.Add(new ModInfo(
                ModId: id,
                DisplayName: sidecar?.DisplayName ?? id,
                Version: sidecar?.Version,
                Source: sidecar?.Source,
                InstalledAt: sidecar?.InstalledAt ?? Directory.GetCreationTimeUtc(sub),
                SizeBytes: size,
                Files: sidecar?.Files));
        }

        return Task.FromResult(result.OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // ---- install ----

    public async Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            return new ModInstallResult(false, null, "URL must be an http(s) link to a zip.");

        var modsDir = ModsFolder(def);
        Directory.CreateDirectory(modsDir);

        var tmpRoot = Path.Combine(Path.GetTempPath(), "gsc-mod-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(tmpRoot);
        var zipPath = Path.Combine(tmpRoot, "download.zip");
        var extractPath = Path.Combine(tmpRoot, "extract");

        try
        {
            // 1) Download
            _logger.LogInformation("Downloading mod from {Url}", url);
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return new ModInstallResult(false, null, $"Download returned HTTP {(int)resp.StatusCode}.");
                await using var fs = File.Create(zipPath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            // 2) Validate it's a zip
            try { ZipFile.ExtractToDirectory(zipPath, extractPath, overwriteFiles: true); }
            catch (Exception ex) { return new ModInstallResult(false, null, "Not a valid zip: " + ex.Message); }

            // 3) Decide what to copy. Thunderstore convention: top-level "plugins/" folder with .dll files.
            //    Fallbacks: top-level .dll files, or a single top-level folder containing the mod.
            var (sourceDir, copyDlls) = LocateModPayload(extractPath);
            if (sourceDir is null && copyDlls is null)
                return new ModInstallResult(false, null,
                    "Could not find any plugin DLLs in the zip. Expected a top-level 'plugins/' folder, or top-level .dll files.");

            // 4) Pick the ModId (display name → folder name, or DLL name)
            string modId;
            var installedFiles = new List<string>();
            if (sourceDir is not null)
            {
                // Whole folder → make a per-mod subfolder under plugins/ so uninstall is trivial.
                modId = SanitizeFolderName(displayName ?? Path.GetFileName(sourceDir.TrimEnd('\\', '/')) ?? "mod");
                var destDir = Path.Combine(modsDir, modId);
                if (Directory.Exists(destDir))
                {
                    // Allow upgrade in place
                    try { Directory.Delete(destDir, recursive: true); } catch { /* ignore */ }
                }
                CopyDirectory(sourceDir, destDir);
                foreach (var f in Directory.EnumerateFiles(destDir, "*", SearchOption.AllDirectories))
                    installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, f));
            }
            else
            {
                // Single-file mod: copy .dll(s) into plugins/ at the top level.
                foreach (var dll in copyDlls!)
                {
                    var dst = Path.Combine(modsDir, Path.GetFileName(dll));
                    File.Copy(dll, dst, overwrite: true);
                    installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, dst));
                }
                modId = Path.GetFileName(copyDlls[0]);
            }

            // 5) Try to extract a version from a Thunderstore manifest.json if present
            string? version = null;
            string? finalName = displayName;
            var manifest = Path.Combine(extractPath, "manifest.json");
            if (File.Exists(manifest))
            {
                try
                {
                    var json = JsonNode.Parse(await File.ReadAllTextAsync(manifest, ct));
                    version = json?["version_number"]?.ToString();
                    finalName ??= json?["name"]?.ToString();
                }
                catch { /* tolerate broken manifests */ }
            }
            finalName ??= modId;

            // 6) Persist metadata
            var info = new ModInfo(
                ModId: modId,
                DisplayName: finalName,
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
            _logger.LogError(ex, "Mod install failed for {Url}", url);
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
        var modsDir = ModsFolder(def);
        if (!Directory.Exists(modsDir)) return Task.FromResult(false);

        // First try the metadata: if we recorded specific files, delete only those.
        var meta = LoadMetadata(def);
        var ok = false;
        if (meta.TryGetValue(modId, out var sidecar) && sidecar.Files is { Length: > 0 } files)
        {
            foreach (var rel in files)
            {
                var abs = Path.Combine(def.GuestWorkingDir, rel);
                try { if (File.Exists(abs)) { File.Delete(abs); ok = true; } } catch { /* ignore */ }
            }
            // Clean up the per-mod folder if it ended up empty
            var modFolder = Path.Combine(modsDir, modId);
            if (Directory.Exists(modFolder) && !Directory.EnumerateFileSystemEntries(modFolder).Any())
            {
                try { Directory.Delete(modFolder); } catch { /* ignore */ }
            }
            UpdateMetadata(def, m => m.Remove(modId));
            return Task.FromResult(ok);
        }

        // No metadata (manually-installed mod): delete by name match.
        var dllPath = Path.Combine(modsDir, modId);
        if (File.Exists(dllPath))
        {
            try { File.Delete(dllPath); ok = true; } catch { /* ignore */ }
        }
        var folderPath = Path.Combine(modsDir, modId);
        if (Directory.Exists(folderPath))
        {
            try { Directory.Delete(folderPath, recursive: true); ok = true; } catch { /* ignore */ }
        }
        return Task.FromResult(ok);
    }

    // ---- helpers ----

    /// <summary>Decide what part of an extracted zip to copy into plugins/.</summary>
    private static (string? folder, string[]? loose) LocateModPayload(string extractRoot)
    {
        // Preference 1: top-level "plugins/" folder (Thunderstore standard)
        var plugins = Path.Combine(extractRoot, "plugins");
        if (Directory.Exists(plugins) && Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories).Any())
            return (plugins, null);

        // Preference 2: top-level .dll files (simple GitHub-release zips)
        var topDlls = Directory.GetFiles(extractRoot, "*.dll", SearchOption.TopDirectoryOnly);
        if (topDlls.Length > 0) return (null, topDlls);

        // Preference 3: single top-level subfolder containing .dlls (some uploaders structure it this way)
        var subfolders = Directory.GetDirectories(extractRoot);
        if (subfolders.Length == 1 && Directory.EnumerateFiles(subfolders[0], "*.dll", SearchOption.AllDirectories).Any())
            return (subfolders[0], null);

        return (null, null);
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, f);
            var dst = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
        }
    }

    private static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c));
        return string.IsNullOrWhiteSpace(clean) ? "mod" : clean;
    }

    // ---- metadata sidecar ----

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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mod sidecar read failed at {Path}", path);
            return new(StringComparer.OrdinalIgnoreCase);
        }
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
            _logger.LogWarning(ex, "Mod sidecar write failed at {Path}", path);
        }
    }
}
