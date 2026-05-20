using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Ensures the Valve <c>steamcmd</c> binary is present and runs it for the agent.
///
/// Where it looks (in priority order):
///   1. <c>Agent:SteamCmdPath</c> in appsettings.json — explicit override.
///   2. <c>steamcmd[.exe]</c> on PATH — for users who installed it themselves.
///   3. <c>&lt;agent dir&gt;/SteamCMD/steamcmd[.exe]</c> — our private cached copy.
///
/// If none of those exist, downloads the official Valve archive into our private cache.
/// </summary>
public sealed class SteamCmdManager
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IConfiguration _cfg;
    private readonly ILogger<SteamCmdManager> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SteamCmdManager(IConfiguration cfg, ILogger<SteamCmdManager> logger)
    {
        _cfg = cfg;
        _logger = logger;
    }

    private string ExeName => OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh";

    // Valve's official distribution URLs. These have been stable for years; if Valve ever moves
    // them, surface a clear error rather than silently failing somewhere downstream.
    private string DistributionUrl => OperatingSystem.IsWindows()
        ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
        : "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";

    private string PrivateCacheDir => Path.Combine(AppContext.BaseDirectory, "SteamCMD");

    /// <summary>Find or fetch steamcmd, returning the absolute path to the binary.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            // 1) Operator override
            var configured = _cfg["Agent:SteamCmdPath"];
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            // 2) PATH lookup — covers winget/apt installs
            var onPath = WhichSteamCmd();
            if (onPath is not null) return onPath;

            // 3) Private cache
            var cached = Path.Combine(PrivateCacheDir, ExeName);
            if (File.Exists(cached)) return cached;

            // 4) Download fresh
            _logger.LogInformation("steamcmd not found — downloading from {Url}", DistributionUrl);
            Directory.CreateDirectory(PrivateCacheDir);
            var archivePath = Path.Combine(PrivateCacheDir, OperatingSystem.IsWindows() ? "steamcmd.zip" : "steamcmd.tar.gz");
            using (var resp = await _http.GetAsync(DistributionUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(archivePath);
                await resp.Content.CopyToAsync(fs, ct);
            }

            if (OperatingSystem.IsWindows())
            {
                ZipFile.ExtractToDirectory(archivePath, PrivateCacheDir, overwriteFiles: true);
            }
            else
            {
                // Use tar via shell — extracts steamcmd.sh + linux32/steamcmd into place.
                await RunShellAsync($"tar -xzf \"{archivePath}\" -C \"{PrivateCacheDir}\"", ct);
                await RunShellAsync($"chmod +x \"{cached}\"", ct);
            }
            try { File.Delete(archivePath); } catch { /* not critical */ }

            if (!File.Exists(cached))
                throw new FileNotFoundException("Extracted steamcmd archive but binary missing. Check " + PrivateCacheDir);
            _logger.LogInformation("steamcmd installed at {Path}", cached);
            return cached;
        }
        finally { _gate.Release(); }
    }

    private static string? WhichSteamCmd()
    {
        // Plain "where"/"which" style PATH lookup with both candidate exe names.
        var candidates = OperatingSystem.IsWindows() ? new[] { "steamcmd.exe", "steamcmd" } : new[] { "steamcmd.sh", "steamcmd" };
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var sep = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in candidates)
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    private static async Task RunShellAsync(string cmd, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"-c \"{cmd.Replace("\"", "\\\"")}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync(ct);
    }

    /// <summary>
    /// Run <c>steamcmd +login anonymous +force_install_dir … +app_update … validate +quit</c> and
    /// stream every line of stdout/stderr through <paramref name="onLine"/>. Returns success/exit code.
    /// </summary>
    public async Task<(bool Ok, int ExitCode)> RunAppUpdateAsync(
        string installDir,
        string appId,
        Action<string, int?> onLine,
        CancellationToken ct)
    {
        var exe = await EnsureAsync(ct);
        Directory.CreateDirectory(installDir);

        var args = $"+force_install_dir \"{installDir}\" +login anonymous +app_update {appId} validate +quit";
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
        };

        using var p = new Process { StartInfo = psi };
        var progressRegex = new Regex(@"progress:\s*([\d.]+)", RegexOptions.IgnoreCase);

        p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            int? pct = null;
            var m = progressRegex.Match(e.Data);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v))
                pct = (int)Math.Clamp(v, 0, 100);
            onLine(e.Data, pct);
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrEmpty(e.Data)) return;
            onLine(e.Data, null);
        };

        if (!p.Start()) return (false, -1);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { /* swallow */ }
            return (false, -2);
        }
        return (p.ExitCode == 0, p.ExitCode);
    }
}
