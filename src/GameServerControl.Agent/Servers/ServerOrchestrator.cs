using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using GameServerControl.Agent.Hubs;
using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Servers;

public sealed class ServerOrchestrator
{
    private readonly ConcurrentDictionary<int, (TimeSpan cpu, DateTime at)> _cpuSamples = new();
    private readonly HypervService _hv;
    private readonly GuestProcessService _guest;
    private readonly LocalProcessService _local;
    private readonly ServerRegistry _registry;
    private readonly StatusTracker _tracker;
    private readonly IHubContext<StatusHub> _hub;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ServerOrchestrator> _logger;

    public ServerOrchestrator(
        HypervService hv,
        GuestProcessService guest,
        LocalProcessService local,
        ServerRegistry registry,
        StatusTracker tracker,
        IHubContext<StatusHub> hub,
        IConfiguration cfg,
        ILogger<ServerOrchestrator> logger)
    {
        _hv = hv;
        _guest = guest;
        _local = local;
        _registry = registry;
        _tracker = tracker;
        _hub = hub;
        _cfg = cfg;
        _logger = logger;
    }

    private GuestCredential GetCred(string? credId)
    {
        var id = string.IsNullOrEmpty(credId) ? "default" : credId;
        var section = _cfg.GetSection($"Agent:GuestCredentials:{id}");
        return new GuestCredential
        {
            Username = section["Username"] ?? "",
            Password = section["Password"] ?? ""
        };
    }

    public async Task<ServerStatus> RefreshStatusAsync(string id, CancellationToken ct = default)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);

        VmState vmState;
        ProcessState procState;
        int? pid = null;

        if (def.HostingMode == HostingMode.BareMetal)
        {
            vmState = VmState.NotApplicable;
            var (ok, foundPid) = await _local.FindProcessAsync(def.GuestExePath, ct);
            procState = !ok ? ProcessState.Unknown : (foundPid is null ? ProcessState.NotRunning : ProcessState.Running);
            pid = foundPid;
        }
        else
        {
            vmState = await _hv.GetStateAsync(def.VmName, ct);
            procState = ProcessState.Unknown;

            if (vmState == VmState.Running)
            {
                var cred = GetCred(def.GuestCredentialId);
                try
                {
                    var (ok, foundPid) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
                    if (ok) { pid = foundPid; procState = foundPid is null ? ProcessState.NotRunning : ProcessState.Running; }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "FindProcess failed for {Vm}", def.VmName);
                }
            }
            else
            {
                procState = ProcessState.NotRunning;
            }
        }

        // CPU / RAM (BareMetal only for now — Hyper-V VM metrics are a future enhancement)
        double? cpuPct = null;
        long? memMB = null;
        if (def.HostingMode == HostingMode.BareMetal && pid is int pp)
        {
            try
            {
                using var p = Process.GetProcessById(pp);
                memMB = p.WorkingSet64 / (1024 * 1024);
                var now = DateTime.UtcNow;
                var cpu = p.TotalProcessorTime;
                if (_cpuSamples.TryGetValue(pp, out var prev))
                {
                    var dt = (now - prev.at).TotalMilliseconds;
                    var dc = (cpu - prev.cpu).TotalMilliseconds;
                    if (dt > 100)
                        cpuPct = Math.Round(Math.Max(0, dc / dt / Environment.ProcessorCount * 100.0), 1);
                }
                _cpuSamples[pp] = (cpu, now);
            }
            catch
            {
                _cpuSamples.TryRemove(pp, out _);
            }
        }

        // Per-game metadata (e.g. Windrose InviteCode)
        var metadata = await BuildMetadataAsync(def, ct);

        var status = _tracker.Update(id, cur => cur with
        {
            VmState = vmState,
            ProcessState = procState,
            PidInGuest = pid,
            CpuPercent = cpuPct,
            MemoryMB = memMB,
            Metadata = metadata
        });
        await BroadcastAsync(status);
        return status;
    }

    private async Task<Dictionary<string, string>?> BuildMetadataAsync(ServerDef def, CancellationToken ct)
    {
        if (def.GameType != GameType.Windrose) return null;
        // Try both layouts — the modern Windrose install nests under R5\, but older saves had it at the root.
        var candidates = new[]
        {
            Path.Combine(def.GuestWorkingDir, "R5", "ServerDescription.json"),
            Path.Combine(def.GuestWorkingDir, "ServerDescription.json"),
        };
        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null) return null;
        try
        {
            var text = await File.ReadAllTextAsync(path, ct);
            var section = (JsonNode.Parse(text) as JsonObject)?["ServerDescription_Persistent"] as JsonObject;
            var dict = new Dictionary<string, string>();
            if (section?.TryGetPropertyValue("InviteCode", out var inv) == true && inv is not null)
                dict["InviteCode"] = inv.ToString();
            if (section?.TryGetPropertyValue("ServerName", out var sn) == true && sn is not null)
                dict["ServerName"] = sn.ToString();
            return dict.Count > 0 ? dict : null;
        }
        catch { return null; }
    }

    private async Task BroadcastAsync(ServerStatus s)
        => await _hub.Clients.All.SendAsync("statusChanged", s);

    private async Task LogAsync(string id, string source, string text)
    {
        var line = new LogLine(id, DateTimeOffset.UtcNow, source, text);
        await _hub.Clients.All.SendAsync("logLine", line);
        _logger.LogInformation("[{Id}] {Src}: {Text}", id, source, text);
    }

    // ---------------- Start ----------------

    public async Task<ActionResult> StartAsync(string id, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        await LogAsync(id, "orch", $"Start requested ({def.HostingMode})");
        return def.HostingMode == HostingMode.BareMetal
            ? await StartBareMetalAsync(def, corr, ct)
            : await StartVmAsync(def, corr, ct);
    }

    private async Task<ActionResult> StartBareMetalAsync(ServerDef def, string corr, CancellationToken ct)
    {
        var (alreadyOk, existingPid) = await _local.FindProcessAsync(def.GuestExePath, ct);
        if (alreadyOk && existingPid is int pidAlready)
        {
            _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pidAlready });
            await RefreshStatusAsync(def.Id, ct);
            return new ActionResult(true, $"Already running (pid={pidAlready})", corr);
        }

        // Scheduled-task lifecycle (Windrose pattern) — preserves the existing autostart-on-boot config
        if (!string.IsNullOrWhiteSpace(def.ScheduledTaskName))
        {
            // The toggle is authoritative: starting via the dashboard means autostart is intended,
            // so make sure the task is enabled (Start fails if the task is currently disabled).
            await _local.SetAutostartAsync(def.ScheduledTaskName, true, ct);
            await LogAsync(def.Id, "orch", $"schtasks /run /tn \"{def.ScheduledTaskName}\" (autostart enabled)");
            if (!await _local.StartScheduledTaskAsync(def.ScheduledTaskName, ct))
                return new ActionResult(false, "schtasks /run failed (task may not exist)", corr);

            // Poll for the spawned process so we can report a real PID
            var deadline = DateTime.UtcNow.AddSeconds(45);
            int? foundPid = null;
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(2000, ct);
                var (ok, p) = await _local.FindProcessAsync(def.GuestExePath, ct);
                if (ok && p is int pp) { foundPid = pp; break; }
            }
            _tracker.Update(def.Id, s => s with
            {
                ProcessState = foundPid is null ? ProcessState.Starting : ProcessState.Running,
                PidInGuest = foundPid,
                StartedAt = DateTimeOffset.UtcNow,
                LastError = null
            });
            await RefreshStatusAsync(def.Id, ct);
            return new ActionResult(true,
                foundPid is null
                    ? "Scheduled task triggered; process not yet visible (will appear shortly)"
                    : $"Started via scheduled task (pid={foundPid})",
                corr);
        }

        await LogAsync(def.Id, "orch", "Launching " + def.GuestExePath);
        var (started, pid, err) = await _local.StartProcessAsync(def.GuestExePath, def.StartArgs, def.GuestWorkingDir, ct);
        if (!started) return new ActionResult(false, "Process launch failed: " + err, corr);

        _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pid, StartedAt = DateTimeOffset.UtcNow, LastError = null });
        await RefreshStatusAsync(def.Id, ct);
        return new ActionResult(true, $"Started (pid={pid})", corr);
    }

    private async Task<ActionResult> StartVmAsync(ServerDef def, string corr, CancellationToken ct)
    {
        var state = await _hv.GetStateAsync(def.VmName, ct);
        if (state != VmState.Running)
        {
            await LogAsync(def.Id, "orch", "Starting VM " + def.VmName);
            if (!await _hv.StartVmAsync(def.VmName, ct))
                return new ActionResult(false, "Failed to start VM", corr);

            var deadline = DateTime.UtcNow.AddMinutes(3);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                state = await _hv.GetStateAsync(def.VmName, ct);
                if (state == VmState.Running) break;
                await Task.Delay(2000, ct);
            }
            if (state != VmState.Running) return new ActionResult(false, "VM did not reach Running state", corr);
        }

        var cred = GetCred(def.GuestCredentialId);
        var reachable = false;
        var reachDeadline = DateTime.UtcNow.AddMinutes(2);
        while (DateTime.UtcNow < reachDeadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (ok, _) = await _guest.FindProcessAsync(def.VmName, cred, def.GuestExePath, ct);
                if (ok) { reachable = true; break; }
            }
            catch { /* keep trying */ }
            await Task.Delay(3000, ct);
        }
        if (!reachable) return new ActionResult(false, "VM running but guest not reachable via PowerShell Direct", corr);

        await LogAsync(def.Id, "orch", "Launching " + def.GuestExePath);
        var (started, pid, err) = await _guest.StartProcessAsync(def.VmName, cred, def.GuestExePath, def.StartArgs, def.GuestWorkingDir, ct);
        if (!started) return new ActionResult(false, "Process launch failed: " + err, corr);

        _tracker.Update(def.Id, s => s with { ProcessState = ProcessState.Running, PidInGuest = pid, StartedAt = DateTimeOffset.UtcNow, LastError = null });
        await RefreshStatusAsync(def.Id, ct);
        return new ActionResult(true, $"Started (pid={pid})", corr);
    }

    // ---------------- Stop ----------------

    public async Task<ActionResult> StopAsync(string id, bool stopVm, bool force, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        await LogAsync(id, "orch", $"Stop requested (mode={def.HostingMode}, stopVm={stopVm}, force={force})");

        if (def.HostingMode == HostingMode.BareMetal)
        {
            if (!string.IsNullOrWhiteSpace(def.ScheduledTaskName))
            {
                // Disable BEFORE ending — prevents "restart on failure" or boot-time autostart
                // from racing our taskkill. The dashboard toggle is authoritative; stopping here
                // means "stay off until the user explicitly starts again."
                await _local.SetAutostartAsync(def.ScheduledTaskName, false, ct);
                await LogAsync(id, "orch", $"Disabled + ending scheduled task \"{def.ScheduledTaskName}\"");
                await _local.EndScheduledTaskAsync(def.ScheduledTaskName, ct);
            }

            var pid = _tracker.Get(id).PidInGuest;
            await _local.StopProcessAsync(def.GuestExePath, pid, force, ct);

            if (def.StopProcessNames is { Length: > 0 } extras)
            {
                await LogAsync(id, "orch", "Killing additional process names: " + string.Join(", ", extras));
                await _local.KillByNamesAsync(extras, ct);
            }

            _tracker.Update(id, s => s with { ProcessState = ProcessState.NotRunning, PidInGuest = null });
            await RefreshStatusAsync(id, ct);
            return new ActionResult(true, "Stopped", corr);
        }

        var cred = GetCred(def.GuestCredentialId);
        var pidVm = _tracker.Get(id).PidInGuest;
        try
        {
            await _guest.StopProcessAsync(def.VmName, cred, def.GuestExePath, pidVm, force, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Stop process error (continuing)"); }

        if (stopVm)
        {
            await LogAsync(id, "orch", "Stopping VM " + def.VmName);
            await _hv.StopVmAsync(def.VmName, force, ct);
        }

        _tracker.Update(id, s => s with { ProcessState = ProcessState.NotRunning, PidInGuest = null });
        await RefreshStatusAsync(id, ct);
        return new ActionResult(true, "Stopped", corr);
    }

    public async Task<ActionResult> RestartAsync(string id, CancellationToken ct = default)
    {
        var stop = await StopAsync(id, stopVm: false, force: false, ct);
        if (!stop.Success) return stop;
        await Task.Delay(2000, ct);
        return await StartAsync(id, ct);
    }

    // ---------------- Backup ----------------

    public async Task<ActionResult> BackupAsync(string id, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);

        if (def.HostingMode == HostingMode.BareMetal)
            return await BackupBareMetalAsync(def, corr, ct);

        var name = $"{def.Id}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        await LogAsync(id, "orch", "Creating checkpoint " + name);
        var ok = await _hv.CreateCheckpointAsync(def.VmName, name, ct);
        if (!ok) return new ActionResult(false, "Checkpoint failed", corr);

        _tracker.Update(id, s => s with { LastBackupAt = DateTimeOffset.UtcNow });
        await RefreshStatusAsync(id, ct);
        return new ActionResult(true, "Checkpoint created: " + name, corr);
    }

    private async Task<ActionResult> BackupBareMetalAsync(ServerDef def, string corr, CancellationToken ct)
    {
        if (def.SaveDirs is null || def.SaveDirs.Length == 0)
            return new ActionResult(false, "No SaveDirs configured for this server.", corr);

        var backupRoot = _cfg["Agent:BackupRoot"] ?? @"C:\GameServerControl\Backups";
        var dest = Path.Combine(backupRoot, def.Id);
        Directory.CreateDirectory(dest);
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
        var zipPath = Path.Combine(dest, $"{def.Id}-{stamp}.zip");

        await LogAsync(def.Id, "orch", "Zipping save dirs to " + zipPath);
        await Task.Run(() =>
        {
            using var fs = File.Create(zipPath);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var dir in def.SaveDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var prefix = Path.GetFileName(dir.TrimEnd('\\', '/'));
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    ct.ThrowIfCancellationRequested();
                    var rel = Path.GetRelativePath(dir, file);
                    var entryName = Path.Combine(prefix, rel).Replace('\\', '/');
                    try
                    {
                        var e = archive.CreateEntry(entryName, CompressionLevel.Fastest);
                        using var es = e.Open();
                        using var src = File.OpenRead(file);
                        src.CopyTo(es);
                    }
                    catch (IOException ex)
                    {
                        _logger.LogWarning("Skipping locked file {File}: {Msg}", file, ex.Message);
                    }
                }
            }
        }, ct);

        var size = new FileInfo(zipPath).Length;

        // Retention: keep newest N (config: Agent:BackupRetention, default 10)
        var keep = int.TryParse(_cfg["Agent:BackupRetention"], out var k) && k > 0 ? k : 10;
        var olds = new DirectoryInfo(dest).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc).Skip(keep).ToList();
        foreach (var f in olds)
        {
            try { f.Delete(); await LogAsync(def.Id, "backup", $"Pruned old backup {f.Name}"); }
            catch (Exception ex) { _logger.LogWarning(ex, "Prune failed for {File}", f.FullName); }
        }

        _tracker.Update(def.Id, s => s with { LastBackupAt = DateTimeOffset.UtcNow });
        await RefreshStatusAsync(def.Id, ct);
        return new ActionResult(true, $"Backup created: {zipPath} ({size / 1024 / 1024} MB) — kept {Math.Min(keep, new DirectoryInfo(dest).GetFiles("*.zip").Length)}", corr);
    }

    public Task<List<BackupInfo>> ListBackupsAsync(string id, CancellationToken ct = default)
    {
        var def = _registry.Get(id) ?? throw new KeyNotFoundException(id);
        var backupRoot = _cfg["Agent:BackupRoot"] ?? @"C:\GameServerControl\Backups";
        var dir = Path.Combine(backupRoot, def.Id);
        var list = new List<BackupInfo>();
        if (!Directory.Exists(dir)) return Task.FromResult(list);
        foreach (var f in new DirectoryInfo(dir).GetFiles("*.zip").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            list.Add(new BackupInfo(
                Id: f.Name,
                ServerId: def.Id,
                CreatedAt: new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero),
                CheckpointName: f.Name,
                SizeBytes: f.Length));
        }
        return Task.FromResult(list);
    }

    public async Task<ActionResult> RestoreBackupAsync(string id, string backupName, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);
        if (def.SaveDirs is null || def.SaveDirs.Length == 0)
            return new ActionResult(false, "No SaveDirs configured", corr);
        if (def.HostingMode != HostingMode.BareMetal)
            return new ActionResult(false, "Restore is only implemented for BareMetal servers", corr);

        // Don't restore while the server is running
        var st = _tracker.Get(id);
        if (st.ProcessState == ProcessState.Running)
            return new ActionResult(false, "Stop the server before restoring a backup.", corr);

        var backupRoot = _cfg["Agent:BackupRoot"] ?? @"C:\GameServerControl\Backups";
        var zipPath = Path.Combine(backupRoot, def.Id, backupName);
        if (!File.Exists(zipPath)) return new ActionResult(false, "Backup not found: " + backupName, corr);

        // Safety net: save current saves as a "pre-restore" backup
        var safetyName = $"{def.Id}-prerestore-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip";
        var safetyPath = Path.Combine(backupRoot, def.Id, safetyName);
        try
        {
            await LogAsync(id, "restore", "Creating safety backup " + safetyName);
            using var fs = File.Create(safetyPath);
            using var ar = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var dir in def.SaveDirs)
            {
                if (!Directory.Exists(dir)) continue;
                var prefix = Path.GetFileName(dir.TrimEnd('\\', '/'));
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(dir, f);
                    var entry = ar.CreateEntry(Path.Combine(prefix, rel).Replace('\\', '/'), CompressionLevel.Fastest);
                    using var es = entry.Open();
                    using var src = File.OpenRead(f);
                    src.CopyTo(es);
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Safety backup failed (continuing)"); }

        // Extract zip back into the SaveDirs, replacing existing files
        try
        {
            await LogAsync(id, "restore", $"Restoring from {backupName}");
            using var archive = ZipFile.OpenRead(zipPath);
            foreach (var dir in def.SaveDirs)
            {
                var prefix = Path.GetFileName(dir.TrimEnd('\\', '/')) + "/";
                Directory.CreateDirectory(dir);
                foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Name)))
                {
                    var rel = entry.FullName.Substring(prefix.Length);
                    var dest = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
        }
        catch (Exception ex)
        {
            await LogAsync(id, "restore", "ERROR: " + ex.Message);
            return new ActionResult(false, "Restore failed: " + ex.Message, corr);
        }

        await RefreshStatusAsync(id, ct);
        return new ActionResult(true, $"Restored from {backupName}. Safety copy: {safetyName}", corr);
    }

    public Task<bool> DeleteBackupAsync(string id, string backupName, CancellationToken ct = default)
    {
        var def = _registry.Get(id);
        if (def is null) return Task.FromResult(false);
        var backupRoot = _cfg["Agent:BackupRoot"] ?? @"C:\GameServerControl\Backups";
        var zipPath = Path.Combine(backupRoot, def.Id, backupName);
        if (!File.Exists(zipPath)) return Task.FromResult(false);
        try { File.Delete(zipPath); return Task.FromResult(true); }
        catch (Exception ex) { _logger.LogWarning(ex, "Delete backup failed"); return Task.FromResult(false); }
    }

    // ---------------- Update (SteamCMD) ----------------

    public async Task<ActionResult> UpdateAsync(string id, CancellationToken ct = default)
    {
        var corr = Guid.NewGuid().ToString("n");
        var def = _registry.Get(id);
        if (def is null) return new ActionResult(false, "Unknown server", corr);
        if (string.IsNullOrWhiteSpace(def.SteamAppId))
            return new ActionResult(false, "No SteamAppId set; cannot update via SteamCMD", corr);

        if (def.HostingMode == HostingMode.BareMetal)
        {
            var steamCmd = _cfg["Agent:SteamCmdPath"] ?? "steamcmd";
            var cmdLine = $"\"{steamCmd}\" +login anonymous +force_install_dir \"{def.GuestWorkingDir}\" +app_update {def.SteamAppId} validate +quit";
            await LogAsync(id, "orch", "Local SteamCMD: " + cmdLine);
            var (ok, output) = await _local.RunCommandAsync(cmdLine, def.GuestWorkingDir, TimeSpan.FromMinutes(30), ct);
            await LogAsync(id, "steamcmd", output.Length > 4000 ? output[^4000..] : output);
            return new ActionResult(ok, ok ? "Update finished" : "Update failed", corr);
        }

        var cred = GetCred(def.GuestCredentialId);
        var state = await _hv.GetStateAsync(def.VmName, ct);
        if (state != VmState.Running)
            return new ActionResult(false, "VM not running", corr);

        var inGuestCmd = $"steamcmd +login anonymous +force_install_dir \"{def.GuestWorkingDir}\" +app_update {def.SteamAppId} validate +quit";
        await LogAsync(id, "orch", "In-guest SteamCMD: " + inGuestCmd);
        var (okGuest, outputGuest) = await _guest.RunCommandInGuestAsync(def.VmName, cred, inGuestCmd, def.GuestWorkingDir, TimeSpan.FromMinutes(30), ct);
        await LogAsync(id, "steamcmd", outputGuest);
        return new ActionResult(okGuest, okGuest ? "Update finished" : "Update failed", corr);
    }

    public async Task<ActionResult> ApplyConfigAsync(string id, CancellationToken ct = default)
        => await RestartAsync(id, ct);
}
