using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameServerControl.Agent.Discovery;

/// <summary>
/// Reads Steam library state from disk WITHOUT requiring the Steam client to be running.
///
/// Cross-platform:
///   - On Windows: probes registry (HKLM\WOW6432Node\Valve\Steam, HKCU\Valve\Steam) + common paths.
///   - On Linux:   probes ~/.steam/steam, ~/.local/share/Steam, /usr/local/games/steam, etc.
///   - On macOS:   probes ~/Library/Application Support/Steam.
///
/// The VDF (libraryfolders.vdf) and ACF (appmanifest_*.acf) formats are identical on every
/// platform, so once we have a Steam install path the rest is shared code.
/// </summary>
public sealed class SteamLibraryReader
{
    private readonly ILogger<SteamLibraryReader> _logger;
    public SteamLibraryReader(ILogger<SteamLibraryReader> logger) { _logger = logger; }

    /// <summary>
    /// Find the Steam client install root. Returns null if no Steam install is detected.
    /// </summary>
    public string? FindSteamInstallPath()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return FindSteamOnWindows();
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return FindSteamOnMacOS();
        return FindSteamOnLinux();
    }

    private string? FindSteamOnMacOS()
    {
        // The Mac Steam client installs under ~/Library/Application Support/Steam.
        // Game-server-only Macs are rare, but auto-discovery still helps for the developer
        // running a personal dedicated server on their iMac.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, "Library", "Application Support", "Steam"),
            "/Applications/Steam.app/Contents/Resources",
        };
        foreach (var p in candidates)
            if (Directory.Exists(p)) return p;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private string? FindSteamOnWindows()
    {
        try
        {
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

        foreach (var p in new[] { @"C:\Program Files (x86)\Steam", @"C:\Program Files\Steam", @"D:\Steam" })
            if (Directory.Exists(p)) return p;
        return null;
    }

    private string? FindSteamOnLinux()
    {
        // Order matters: ~/.steam/steam is the canonical symlink most distros use,
        // ~/.local/share/Steam is the actual install on Flatpak/newer Steam,
        // /usr/local paths are for distro-packaged installs.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            Path.Combine(home, ".steam", "steam"),
            Path.Combine(home, ".local", "share", "Steam"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam"), // Flatpak
            "/usr/local/games/Steam",
            "/usr/local/share/Steam",
            "/usr/games/steam",
            "/opt/steam",
        };
        foreach (var p in candidates)
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
            vdfPath = Path.Combine(steamInstallPath, "config", "libraryfolders.vdf");
        if (!File.Exists(vdfPath))
        {
            _logger.LogWarning("libraryfolders.vdf not found under {Steam}", steamInstallPath);
            result.Add(steamInstallPath);
            return result;
        }
        try
        {
            var text = File.ReadAllText(vdfPath);
            foreach (Match m in Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.Compiled))
            {
                // VDF escapes backslashes; normalize for Windows. Linux paths are unaffected.
                var raw = m.Groups[1].Value.Replace(@"\\", @"\");
                if (Directory.Exists(raw)) result.Add(raw);
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
