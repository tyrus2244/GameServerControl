using GameServerControl.Agent.Hyperv;
using GameServerControl.Shared;

namespace GameServerControl.Agent.Servers;

/// <summary>
/// Creates / removes Windows Task Scheduler entries that POST to our own API
/// to trigger restart, update, and backup at configured times.
///
/// Task names: GSC-{serverId}-restart, GSC-{serverId}-update, GSC-{serverId}-backup.
/// Action: curl.exe (built into Windows since 1803) -X POST against the local API
/// using the same bearer token the dashboard uses.
/// </summary>
public sealed class MaintenanceScheduler
{
    private readonly LocalProcessService _local;
    private readonly IConfiguration _cfg;
    private readonly ILogger<MaintenanceScheduler> _logger;

    public MaintenanceScheduler(LocalProcessService local, IConfiguration cfg, ILogger<MaintenanceScheduler> logger)
    {
        _local = local;
        _cfg = cfg;
        _logger = logger;
    }

    private string LocalApiBase
    {
        get
        {
            // Always use http://127.0.0.1:<port> for scheduled-task callbacks so they work without Tailscale.
            var bind = _cfg["Agent:Bind"] ?? "http://127.0.0.1:5099";
            try
            {
                var uri = new Uri(bind);
                return $"http://127.0.0.1:{uri.Port}";
            }
            catch { return "http://127.0.0.1:5099"; }
        }
    }

    private string Token => _cfg["Agent:ApiToken"] ?? "";

    public string TaskName(string serverId, string kind) => $"GSC-{serverId}-{kind}";

    public async Task ApplyAsync(string serverId, MaintenanceSchedule? schedule, CancellationToken ct = default)
    {
        if (!_local.HasScheduledTaskSupport)
        {
            // Linux: no Task Scheduler. Operators wire up scheduled restarts/updates/backups
            // via their own systemd timer units (or cron). The agent surfaces this as a clean
            // no-op rather than throwing — see SECURITY.md / README for the recipe.
            _logger.LogInformation(
                "MaintenanceScheduler.ApplyAsync({Id}) skipped — scheduled tasks are Windows-only. " +
                "Use a systemd timer or cron job that POSTs to /api/servers/{Id}/restart|update|backup.",
                serverId, serverId);
            return;
        }
        // Always wipe existing tasks for this server, then recreate enabled ones.
        await RemoveTaskAsync(TaskName(serverId, "restart"), ct);
        await RemoveTaskAsync(TaskName(serverId, "update"), ct);
        await RemoveTaskAsync(TaskName(serverId, "backup"), ct);
        if (schedule is null) return;

        if (schedule.DailyRestartEnabled)
        {
            await CreateAsync(
                TaskName(serverId, "restart"),
                $"Daily restart of {serverId}",
                trigger: $"/sc DAILY /st {schedule.DailyRestartHour:D2}:00",
                apiPath: $"/api/servers/{serverId}/restart",
                ct);
        }
        if (schedule.WeeklyUpdateEnabled)
        {
            var dayCode = schedule.WeeklyUpdateDay switch
            {
                DayOfWeek.Monday => "MON", DayOfWeek.Tuesday => "TUE", DayOfWeek.Wednesday => "WED",
                DayOfWeek.Thursday => "THU", DayOfWeek.Friday => "FRI", DayOfWeek.Saturday => "SAT", _ => "SUN"
            };
            await CreateAsync(
                TaskName(serverId, "update"),
                $"Weekly SteamCMD update of {serverId}",
                trigger: $"/sc WEEKLY /d {dayCode} /st {schedule.WeeklyUpdateHour:D2}:00",
                apiPath: $"/api/servers/{serverId}/update",
                ct);
        }
        if (schedule.HourlyBackupEnabled)
        {
            // /sc HOURLY /mo 1 /st HH:MM — first run starts at the next matching minute, then every hour.
            var firstHour = DateTime.Now.Hour;
            if (DateTime.Now.Minute >= schedule.HourlyBackupMinute) firstHour = (firstHour + 1) % 24;
            await CreateAsync(
                TaskName(serverId, "backup"),
                $"Hourly backup of {serverId}",
                trigger: $"/sc HOURLY /mo 1 /st {firstHour:D2}:{schedule.HourlyBackupMinute:D2}",
                apiPath: $"/api/servers/{serverId}/backup",
                ct);
        }
    }

    private async Task CreateAsync(string taskName, string description, string trigger, string apiPath, CancellationToken ct)
    {
        var url = LocalApiBase + apiPath;
        // curl.exe sits in System32 on modern Windows; -k to tolerate self-signed (we're hitting http loopback though, so unused)
        var tr = $"curl.exe -s -X POST -H \"Authorization: Bearer {Token}\" \"{url}\"";
        // /ru SYSTEM lets the task run as LocalSystem with no user logged in (matches agent's identity)
        var cmd = $"schtasks /create /f /tn \"{taskName}\" /tr \"{tr}\" {trigger} /ru SYSTEM /rl HIGHEST";
        var (ok, output) = await _local.RunCommandAsync(cmd, "", TimeSpan.FromSeconds(15), ct);
        if (!ok) _logger.LogWarning("Create task {Name} failed: {Out}", taskName, output);
    }

    private async Task RemoveTaskAsync(string taskName, CancellationToken ct)
    {
        var (ok, _) = await _local.RunCommandAsync($"schtasks /delete /f /tn \"{taskName}\"", "", TimeSpan.FromSeconds(10), ct);
        // Tolerate "not found" silently.
    }

    public async Task<MaintenanceSchedule?> ReadAsync(string serverId, CancellationToken ct = default)
    {
        if (!_local.HasScheduledTaskSupport) return null; // Linux: not supported here
        // Query the three task names; reconstruct best-effort schedule.
        var schedule = new MaintenanceSchedule();
        bool found = false;

        var restart = await ReadTaskTimeAsync(TaskName(serverId, "restart"), ct);
        if (restart is DateTime r) { found = true; schedule = schedule with { DailyRestartEnabled = true, DailyRestartHour = r.Hour }; }

        var upd = await ReadTaskTimeAsync(TaskName(serverId, "update"), ct);
        if (upd is DateTime u) { found = true; schedule = schedule with { WeeklyUpdateEnabled = true, WeeklyUpdateHour = u.Hour }; }

        var bk = await ReadTaskTimeAsync(TaskName(serverId, "backup"), ct);
        if (bk is DateTime b) { found = true; schedule = schedule with { HourlyBackupEnabled = true, HourlyBackupMinute = b.Minute }; }

        return found ? schedule : null;
    }

    private async Task<DateTime?> ReadTaskTimeAsync(string taskName, CancellationToken ct)
    {
        var (ok, output) = await _local.RunCommandAsync($"schtasks /query /tn \"{taskName}\" /fo LIST /v", "", TimeSpan.FromSeconds(10), ct);
        if (!ok) return null;
        foreach (var line in output.Replace("\r\n", "\n").Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("Start Time", StringComparison.OrdinalIgnoreCase))
            {
                var c = l.IndexOf(':');
                if (c < 0) continue;
                if (DateTime.TryParse(l.Substring(c + 1).Trim(), out var t)) return t;
            }
        }
        return null;
    }
}
