using System.Collections.Concurrent;
using GameServerControl.Agent.Hubs;
using GameServerControl.Shared;
using Microsoft.AspNetCore.SignalR;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Runs SteamCMD against a given install path, then registers the supplied ServerDef on success.
/// Progress is streamed over SignalR ("installProgress" event); jobs are in-memory only so a
/// restart mid-install loses the in-flight job.
/// </summary>
public sealed class ServerInstaller
{
    private readonly SteamCmdManager _steamCmd;
    private readonly ServerStore _store;
    private readonly IHubContext<StatusHub> _hub;
    private readonly ILogger<ServerInstaller> _logger;

    // jobId → final progress snapshot, so a UI joining mid-install can grab the latest.
    private readonly ConcurrentDictionary<string, InstallProgress> _jobs = new();

    public ServerInstaller(SteamCmdManager steamCmd, ServerStore store,
        IHubContext<StatusHub> hub, ILogger<ServerInstaller> logger)
    {
        _steamCmd = steamCmd;
        _store = store;
        _hub = hub;
        _logger = logger;
    }

    public InstallProgress? Get(string jobId) => _jobs.TryGetValue(jobId, out var p) ? p : null;

    /// <summary>Kicks off an install async; returns a job ID. Caller validates the request.</summary>
    public string StartJob(InstallServerRequest req)
    {
        var jobId = Guid.NewGuid().ToString("N")[..12];
        _jobs[jobId] = new InstallProgress(jobId, "queued", "Queued.", null, false, false);
        // Fire-and-forget — exceptions are caught inside RunAsync. The Task is intentionally
        // not awaited; we rely on SignalR for completion signaling.
        _ = Task.Run(() => RunAsync(jobId, req, CancellationToken.None));
        return jobId;
    }

    private async Task RunAsync(string jobId, InstallServerRequest req, CancellationToken ct)
    {
        try
        {
            await BroadcastAsync(new InstallProgress(jobId, "steamcmd", "Resolving SteamCMD…", 0, false, false));

            var (ok, exitCode) = await _steamCmd.RunAppUpdateAsync(
                req.InstallPath,
                req.SteamAppId,
                (line, pct) =>
                {
                    // Fire-and-forget — slow SignalR shouldn't block steamcmd parsing.
                    _ = BroadcastAsync(new InstallProgress(jobId, "steamcmd", line, pct, false, false));
                },
                ct);

            if (!ok)
            {
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"SteamCMD exited with code {exitCode}. Most common cause: not enough disk space, or Steam was rate-limiting you. Try again in a minute.",
                    null, true, false));
                return;
            }

            // Register the server. The ServerDef's GuestExePath should already be the full path
            // the wizard composed from the install location + preset's relative exe path.
            await BroadcastAsync(new InstallProgress(jobId, "register", "Registering server…", 99, false, false));
            try
            {
                _store.Add(req.ServerDef);
            }
            catch (Exception ex)
            {
                // SteamCMD already finished, so the files are on disk — the user can manually
                // add the server from the dashboard. Surface this as a soft failure.
                await BroadcastAsync(new InstallProgress(jobId, "failed",
                    $"SteamCMD finished but registration failed: {ex.Message}. The game files ARE installed at {req.InstallPath}; you can add the server manually.",
                    null, true, false));
                return;
            }

            await BroadcastAsync(new InstallProgress(jobId, "done",
                $"Done. Server '{req.ServerDef.Name}' is ready to start.",
                100, true, true));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install job {Id} crashed", jobId);
            await BroadcastAsync(new InstallProgress(jobId, "failed", "Exception: " + ex.Message, null, true, false));
        }
    }

    private async Task BroadcastAsync(InstallProgress p)
    {
        _jobs[p.JobId] = p;
        await _hub.Clients.All.SendAsync("installProgress", p);
    }
}
