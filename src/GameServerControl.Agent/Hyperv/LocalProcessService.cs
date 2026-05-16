using System.Diagnostics;

namespace GameServerControl.Agent.Hyperv;

/// <summary>
/// Bare-metal counterpart to <see cref="GuestProcessService"/>. Runs everything directly
/// on the agent host instead of dispatching through PowerShell Direct into a VM guest.
///
/// Process detached pattern: we shell out via cmd /c start "" "exe" args so the launched
/// process keeps running after this method returns and is parented to no one.
/// </summary>
public sealed class LocalProcessService
{
    private readonly ILogger<LocalProcessService> _logger;
    public LocalProcessService(ILogger<LocalProcessService> logger) { _logger = logger; }

    /// <summary>
    /// True on Windows (we have <c>schtasks</c>) — false on Linux/macOS.
    /// Orchestrator uses this to decide whether to drive autostart via the Task Scheduler
    /// or fall through to direct <see cref="Process.Start"/>. Linux operators get
    /// auto-restart by running the agent under a systemd unit with <c>Restart=on-failure</c>.
    /// </summary>
    public bool HasScheduledTaskSupport => OperatingSystem.IsWindows();

    public Task<(bool Ok, int? Pid, string Error)> StartProcessAsync(string exePath, string[] args, string workingDir, CancellationToken ct = default)
    {
        try
        {
            if (!File.Exists(exePath))
                return Task.FromResult<(bool, int?, string)>((false, null, $"EXE not found: {exePath}"));

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Path.GetDirectoryName(exePath) ?? "" : workingDir,
                UseShellExecute = true,       // launches detached so the agent service can exit without killing the server
                CreateNoWindow = false,
                WindowStyle = ProcessWindowStyle.Minimized
            };
            foreach (var a in args)
                psi.ArgumentList.Add(a);

            var p = Process.Start(psi);
            if (p is null) return Task.FromResult<(bool, int?, string)>((false, null, "Process.Start returned null"));
            return Task.FromResult<(bool, int?, string)>((true, p.Id, ""));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StartProcess failed for {Exe}", exePath);
            return Task.FromResult<(bool, int?, string)>((false, null, ex.Message));
        }
    }

    public Task<(bool Ok, int? Pid)> FindProcessAsync(string exePath, CancellationToken ct = default)
    {
        try
        {
            var name = Path.GetFileNameWithoutExtension(exePath);
            // Match by name first (cheap); if duplicates exist, prefer one whose MainModule path matches the target.
            var candidates = Process.GetProcessesByName(name);
            Process? best = null;
            foreach (var p in candidates)
            {
                try
                {
                    if (string.Equals(p.MainModule?.FileName, exePath, StringComparison.OrdinalIgnoreCase))
                        return Task.FromResult<(bool, int?)>((true, p.Id));
                    best ??= p;
                }
                catch
                {
                    best ??= p;  // MainModule access requires same architecture / privileges; tolerate
                }
            }
            return Task.FromResult<(bool, int?)>((true, best?.Id));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "FindProcess failed for {Exe}", exePath);
            return Task.FromResult<(bool, int?)>((false, null));
        }
    }

    public async Task<bool> StopProcessAsync(string exePath, int? pid, bool force, CancellationToken ct = default)
    {
        try
        {
            Process? p = null;
            if (pid is int id)
            {
                try { p = Process.GetProcessById(id); } catch { /* may not exist */ }
            }
            if (p is null)
            {
                var name = Path.GetFileNameWithoutExtension(exePath);
                p = Process.GetProcessesByName(name).FirstOrDefault();
            }
            if (p is null) return true;  // already gone

            if (!force)
            {
                try { p.CloseMainWindow(); } catch { /* no UI */ }
                var deadline = DateTime.UtcNow.AddSeconds(45);
                while (DateTime.UtcNow < deadline && !p.HasExited)
                {
                    await Task.Delay(500, ct);
                }
                if (p.HasExited) return true;
            }

            try { p.Kill(entireProcessTree: true); } catch (Exception ex) { _logger.LogWarning(ex, "Kill failed for pid {Pid}", p.Id); return false; }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "StopProcess failed for {Exe}", exePath);
            return false;
        }
    }

    public async Task<bool> StartScheduledTaskAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return false;
        var (ok, output) = await RunCommandAsync($"schtasks /run /tn \"{taskName}\"", "", TimeSpan.FromSeconds(15), ct);
        if (!ok) _logger.LogWarning("schtasks /run for {Task} failed: {Out}", taskName, output);
        return ok;
    }

    public async Task<bool> EndScheduledTaskAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return true; // nothing to end
        var (ok, _) = await RunCommandAsync($"schtasks /end /tn \"{taskName}\"", "", TimeSpan.FromSeconds(15), ct);
        return ok;
    }

    public async Task<bool?> GetAutostartAsync(string taskName, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return null; // unsupported here — return "unknown"
        // /fo LIST /v outputs plain text. Look for the "Scheduled Task State:" row whose value is "Enabled" or "Disabled".
        // (schtasks /xml is UTF-16 with BOM which mangles when read through cmd's default codepage.)
        var (ok, output) = await RunCommandAsync($"schtasks /query /tn \"{taskName}\" /fo LIST /v", "", TimeSpan.FromSeconds(10), ct);
        if (!ok || string.IsNullOrWhiteSpace(output)) return null;
        foreach (var rawLine in output.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("Scheduled Task State", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':');
                if (colon < 0) continue;
                var val = line.Substring(colon + 1).Trim();
                if (val.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) return true;
                if (val.Equals("Disabled", StringComparison.OrdinalIgnoreCase)) return false;
            }
        }
        return null;
    }

    public async Task<bool> SetAutostartAsync(string taskName, bool enabled, CancellationToken ct = default)
    {
        if (!HasScheduledTaskSupport) return true; // nothing to set — orchestrator treats as a no-op
        var flag = enabled ? "/enable" : "/disable";
        var (ok, _) = await RunCommandAsync($"schtasks /change /tn \"{taskName}\" {flag}", "", TimeSpan.FromSeconds(15), ct);
        return ok;
    }

    /// <summary>
    /// Cross-platform kill-by-process-name. Strips an .exe suffix if present
    /// (the leaf name with or without extension matches <see cref="Process.GetProcessesByName"/>).
    /// </summary>
    public Task KillByNamesAsync(IEnumerable<string> exeNames, CancellationToken ct = default)
    {
        foreach (var raw in exeNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bare = Path.GetFileNameWithoutExtension(raw);
            if (string.IsNullOrEmpty(bare)) continue;
            try
            {
                foreach (var p in Process.GetProcessesByName(bare))
                {
                    try { p.Kill(entireProcessTree: true); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Kill pid {Pid} ({Name}) failed", p.Id, bare); }
                }
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Kill by name {Name} failed", bare); }
        }
        return Task.CompletedTask;
    }

    public async Task<(bool Ok, string Output)> RunCommandAsync(string commandLine, string workingDir, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            // Cross-platform shell-out: cmd.exe /c on Windows, /bin/sh -c on Linux/macOS.
            // We pass the command as a single ArgumentList entry on POSIX so quoting/escaping
            // doesn't get re-mangled by .NET's argument splitter.
            var psi = new ProcessStartInfo
            {
                WorkingDirectory = string.IsNullOrWhiteSpace(workingDir) ? Environment.CurrentDirectory : workingDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = "cmd.exe";
                psi.Arguments = "/c " + commandLine;
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(commandLine);
            }
            using var p = Process.Start(psi);
            if (p is null) return (false, "Process.Start returned null");

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "TIMEOUT");
            }
            var stdout = await outTask;
            var stderr = await errTask;
            var combined = stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n[stderr]\n" + stderr);
            return (p.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunCommand failed: {Cmd}", commandLine);
            return (false, ex.Message);
        }
    }
}
