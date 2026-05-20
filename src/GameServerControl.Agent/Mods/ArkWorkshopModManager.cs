using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Mods;

/// <summary>
/// ARK: Survival Ascended / Evolved server-side mod manager.
///
/// ARK distributes mods through Steam Workshop (ASE app 346110) and CurseForge (ASA),
/// not Thunderstore. We implement install-by-ID via SteamCMD:
///
///   steamcmd +login anonymous +workshop_download_item 346110 &lt;workshopId&gt; +quit
///
/// The download lands in steamapps\workshop\content\&lt;appid&gt;\&lt;workshopId&gt;\, and we
/// copy/move it into &lt;install&gt;\ShooterGame\Content\Mods\&lt;workshopId&gt;\ where the game
/// looks for loaded mods. After install we surface a hint that the operator needs to
/// add the workshop ID to the launch args' GameModIds= list (or for ASA, mods=).
///
/// Browse-by-search isn't implemented in this scaffold — Steam's IPublishedFileService
/// requires a Steam Web API key, and we don't ship one. Users paste a Workshop URL or
/// raw ID and we handle the rest. PRs welcome for a search layer once a no-key path
/// exists or operators want to wire their own key.
/// </summary>
public sealed class ArkWorkshopModManager : IModManager
{
    private readonly ILogger<ArkWorkshopModManager> _logger;
    private readonly LocalProcessService _local;
    private readonly IConfiguration _cfg;

    public ArkWorkshopModManager(ILogger<ArkWorkshopModManager> logger, LocalProcessService local, IConfiguration cfg)
    {
        _logger = logger;
        _local = local;
        _cfg = cfg;
    }

    public bool Supports(ServerDef def) =>
        def.GameType == GameType.SteamGeneric &&
        (def.SteamAppId == "376030" /* ARK: SE */ || def.SteamAppId == "2430930" /* ARK: SA */);

    /// <summary>Steam Workshop app ID for the mod content (NOT the same as the game's app ID for ARK: SE).</summary>
    private static string WorkshopAppId(ServerDef def) => def.SteamAppId switch
    {
        "376030"  => "346110",   // ARK: SE — mods are published under app 346110 ("ARK")
        _         => def.SteamAppId ?? "",
    };

    public string ModsFolder(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "ShooterGame", "Content", "Mods");

    public string? MarketplaceSource(ServerDef def) =>
        def.SteamAppId == "2430930"
            ? "CurseForge / Steam Workshop (paste a mod URL or ID in the URL box)"
            : "Steam Workshop (paste a mod URL or ID in the URL box)";

    // No browse support yet — search returns empty with the URL-paste hint.
    public Task<ModSearchResult[]> SearchAsync(ServerDef def, string query, int limit, bool serverSideOnly, CancellationToken ct)
        => Task.FromResult(Array.Empty<ModSearchResult>());

    // ---- list ----

    public Task<ModInfo[]> ListAsync(ServerDef def, CancellationToken ct)
    {
        var dir = ModsFolder(def);
        if (!Directory.Exists(dir)) return Task.FromResult(Array.Empty<ModInfo>());

        var meta = LoadMetadata(def);
        var rows = new List<ModInfo>();
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(sub);
            var sidecar = meta.GetValueOrDefault(id);
            long size = 0;
            try { size = new DirectoryInfo(sub).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
            catch { /* ignore */ }
            rows.Add(new ModInfo(
                ModId: id,
                DisplayName: sidecar?.DisplayName ?? $"Workshop {id}",
                Version: sidecar?.Version,
                Source: sidecar?.Source ?? $"https://steamcommunity.com/sharedfiles/filedetails/?id={id}",
                InstalledAt: sidecar?.InstalledAt ?? Directory.GetCreationTimeUtc(sub),
                SizeBytes: size,
                Files: sidecar?.Files));
        }
        return Task.FromResult(rows.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    // ---- install ----

    public async Task<ModInstallResult> InstallFromUrlAsync(ServerDef def, string url, string? displayName, CancellationToken ct)
    {
        var workshopId = ExtractWorkshopId(url);
        if (string.IsNullOrEmpty(workshopId))
            return new ModInstallResult(false, null,
                "Couldn't extract a Steam Workshop ID from that URL. Paste either the full https://steamcommunity.com/sharedfiles/filedetails/?id=XXXXXX URL or just the numeric ID.");

        var steamCmd = _cfg["Agent:SteamCmdPath"] ?? "steamcmd";
        var workshopAppId = WorkshopAppId(def);
        if (string.IsNullOrEmpty(workshopAppId))
            return new ModInstallResult(false, null, "Unknown Workshop app ID for this game.");

        _logger.LogInformation("ARK workshop install: app {WAppId} item {Id}", workshopAppId, workshopId);

        // 1) Tell SteamCMD to fetch the item. It lands in a known location relative to steamcmd:
        //    steamapps\workshop\content\<workshopAppId>\<workshopId>\
        var cmd = $"\"{steamCmd}\" +login anonymous +workshop_download_item {workshopAppId} {workshopId} +quit";
        var (ok, output) = await _local.RunCommandAsync(cmd, def.GuestWorkingDir, TimeSpan.FromMinutes(20), ct);
        if (!ok)
            return new ModInstallResult(false, null,
                "SteamCMD failed. Make sure 'steamcmd' is on PATH or set Agent:SteamCmdPath in appsettings.json. Last output:\n" +
                Truncate(output, 800));

        // 2) Find where SteamCMD dropped the files. It's the deepest 'content\<workshopAppId>\<workshopId>' on disk.
        var sourceDir = await FindWorkshopContentAsync(steamCmd, workshopAppId, workshopId, ct);
        if (sourceDir is null)
            return new ModInstallResult(false, null,
                $"SteamCMD reported success but couldn't locate the downloaded content for workshop item {workshopId}. " +
                "Look in your steamcmd install for steamapps\\workshop\\content\\");

        // 3) Move into ARK's mods folder
        var destDir = Path.Combine(ModsFolder(def), workshopId);
        if (Directory.Exists(destDir))
        {
            try { Directory.Delete(destDir, recursive: true); } catch { /* ignore */ }
        }
        Directory.CreateDirectory(destDir);
        var installedFiles = new List<string>();
        foreach (var f in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, f);
            var dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, overwrite: true);
            installedFiles.Add(Path.GetRelativePath(def.GuestWorkingDir, dst));
        }

        var info = new ModInfo(
            ModId: workshopId,
            DisplayName: displayName ?? $"Workshop {workshopId}",
            Version: null,   // Workshop doesn't expose a clean version string
            Source: $"https://steamcommunity.com/sharedfiles/filedetails/?id={workshopId}",
            InstalledAt: DateTimeOffset.UtcNow,
            SizeBytes: installedFiles.Select(f => new FileInfo(Path.Combine(def.GuestWorkingDir, f)).Length).Sum(),
            Files: installedFiles.ToArray());
        UpdateMetadata(def, m => m[workshopId] = ToSidecar(info));

        var modIdsHint = string.Equals(def.SteamAppId, "2430930", StringComparison.Ordinal)
            ? $"Add this mod to your launch args: -mods={workshopId} (or append it to your existing -mods=A,B list)."
            : $"Add this mod to your launch args: ?GameModIds={workshopId} (or append it to your existing ?GameModIds=A,B list).";
        _logger.LogInformation(modIdsHint);

        // Wrap the hint into the result so the UI can surface it. We use a fake "version" tag
        // pattern: keep the real version null but stuff the hint into Source as a secondary line.
        // (Cleaner approach: extend ModInstallResult with a Note field — leaving as TODO.)
        return new ModInstallResult(true, info with { Source = info.Source + " | " + modIdsHint }, null);
    }

    // ---- uninstall ----

    public Task<bool> UninstallAsync(ServerDef def, string modId, CancellationToken ct)
    {
        var modsDir = ModsFolder(def);
        var dir = Path.Combine(modsDir, modId);
        var ok = false;
        try
        {
            if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); ok = true; }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "ARK mod uninstall failed for {Id}", modId); }
        UpdateMetadata(def, m => m.Remove(modId));
        return Task.FromResult(ok);
    }

    // ---- helpers ----

    /// <summary>Pull a numeric workshop ID out of either a full Steam URL or a bare ID.</summary>
    private static string ExtractWorkshopId(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var trimmed = input.Trim();
        // Bare ID
        if (Regex.IsMatch(trimmed, "^\\d+$")) return trimmed;
        // ?id=NNNN or &id=NNNN in URL
        var m = Regex.Match(trimmed, "[?&]id=(\\d+)");
        if (m.Success) return m.Groups[1].Value;
        // Last path segment as a number
        m = Regex.Match(trimmed, "/(\\d+)(/|$|\\?)");
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Locate the Workshop content directory SteamCMD just populated.</summary>
    private static Task<string?> FindWorkshopContentAsync(string steamCmdPath, string workshopAppId, string workshopId, CancellationToken ct)
    {
        var candidates = new List<string>();
        // Standard: relative to steamcmd's install dir
        var steamCmdDir = Path.GetDirectoryName(steamCmdPath);
        if (!string.IsNullOrEmpty(steamCmdDir))
            candidates.Add(Path.Combine(steamCmdDir, "steamapps", "workshop", "content", workshopAppId, workshopId));
        // Common fallback locations
        candidates.Add(Path.Combine(@"C:\steamcmd", "steamapps", "workshop", "content", workshopAppId, workshopId));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "steamcmd", "steamapps", "workshop", "content", workshopAppId, workshopId));

        foreach (var c in candidates)
        {
            if (Directory.Exists(c)) return Task.FromResult<string?>(c);
        }
        return Task.FromResult<string?>(null);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "…";

    // ---- metadata sidecar ----

    private static string MetadataPath(ServerDef def) =>
        Path.Combine(def.GuestWorkingDir, "ShooterGame", ".gsc-mods.json");

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
            _logger.LogWarning(ex, "ARK mod sidecar write failed at {Path}", path);
        }
    }
}
