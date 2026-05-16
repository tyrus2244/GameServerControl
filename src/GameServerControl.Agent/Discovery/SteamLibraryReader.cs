using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameServerControl.Agent.Discovery;

// Registry access is Windows-only by design. The whole agent targets Windows
// (Hyper-V cmdlets + Windows Task Scheduler + Steam-on-Windows paths).

/// <summary>
/// Reads Steam library state from disk WITHOUT requiring the Steam client to be running.
///
/// Steam stores its library list in &lt;SteamInstall&gt;\steamapps\libraryfolders.vdf — a Valve
/// custom key-value format. Each library has a &lt;LibraryPath&gt;\steamapps\common\&lt;game&gt;
/// directory plus an appmanifest_&lt;appid&gt;.acf for every installed app.
///
/// We extract just the bits we need (library paths + installed app IDs and their installdir)
/// with a tiny tokenizer instead of pulling in a full VDF library.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SteamLibraryReader
{
    private readonly ILogger<SteamLibraryReader> _logger;
    public SteamLibraryReader(ILogger<SteamLibraryReader> logger) { _logger = logger; }

    /// <summary>
    /// Find the Steam client install root. Tries registry first, then common Windows paths.
    /// Returns null if no Steam install is detected.
    /// </summary>
    public string? FindSteamInstallPath()
    {
        try
        {
            // HKLM (64-bit Steam on Windows installs here under WOW6432Node)
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            var hklmPath = hklm?.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(hklmPath) && Directory.Exists(hklmPath)) return hklmPath;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "HKLM Steam lookup failed"); }

        try
        {
            using var hkcu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
            var hkcuPath = hkcu?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(hkcuPath) && Directory.Exists(hkcuPath)) return hkcuPath;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "HKCU Steam lookup failed"); }

        // Fallback to common paths
        foreach (var p in new[] {
            @"C:\Program Files (x86)\Steam",
            @"C:\Program Files\Steam",
            @"D:\Steam",
        })
        {
            if (Directory.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>
    /// Returns the list of Steam library directories from libraryfolders.vdf.
    /// Always includes the Steam install root itself (Steam treats that as library "0").
    /// </summary>
    public List<string> EnumerateLibraryFolders(string steamInstallPath)
    {
        var result = new List<string>();
        var vdfPath = Path.Combine(steamInstallPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            // Older Steam versions kept it in config/libraryfolders.vdf
            vdfPath = Path.Combine(steamInstallPath, "config", "libraryfolders.vdf");
        }
        if (!File.Exists(vdfPath))
        {
            _logger.LogWarning("libraryfolders.vdf not found at expected paths under {Steam}", steamInstallPath);
            result.Add(steamInstallPath); // assume only the default library
            return result;
        }
        try
        {
            var text = File.ReadAllText(vdfPath);
            // Match every "path" "<value>" line — these are the library roots.
            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.Compiled))
            {
                var raw = m.Groups[1].Value;
                // Steam writes paths with doubled backslashes in VDF; normalize.
                var path = raw.Replace(@"\\", @"\");
                if (Directory.Exists(path)) result.Add(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse {Path}", vdfPath);
        }
        if (result.Count == 0) result.Add(steamInstallPath);
        return result;
    }

    /// <summary>
    /// For a given Steam library folder, returns a map of installed appId → installdir name
    /// (the folder name under \steamapps\common\). Reads appmanifest_*.acf files.
    /// </summary>
    public Dictionary<string, string> EnumerateInstalledApps(string libraryFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var manifestDir = Path.Combine(libraryFolder, "steamapps");
        if (!Directory.Exists(manifestDir)) return result;

        foreach (var file in Directory.EnumerateFiles(manifestDir, "appmanifest_*.acf"))
        {
            try
            {
                var text = File.ReadAllText(file);
                var appIdMatch = Regex.Match(text, "\"appid\"\\s*\"(\\d+)\"", RegexOptions.IgnoreCase);
                var installDirMatch = Regex.Match(text, "\"installdir\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                if (!appIdMatch.Success || !installDirMatch.Success) continue;
                result[appIdMatch.Groups[1].Value] = installDirMatch.Groups[1].Value;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Skipping unreadable manifest {File}", file);
            }
        }
        return result;
    }
}
