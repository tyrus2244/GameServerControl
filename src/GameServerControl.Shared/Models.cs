namespace GameServerControl.Shared;

public enum GameType
{
    Windrose,
    SteamGeneric,
    Minecraft,
    Custom
}

public enum VmState
{
    Unknown,
    Off,
    Starting,
    Running,
    Stopping,
    Saved,
    Paused,
    NotApplicable    // bare-metal hosting: there is no VM to manage
}

public enum HostingMode
{
    BareMetal,   // server process runs directly on the agent host
    Vm           // server process runs inside a Hyper-V guest; agent uses PowerShell Direct
}

public enum ProcessState
{
    Unknown,
    NotRunning,
    Starting,
    Running,
    Stopping
}

public enum ServerActionKind
{
    Start,
    Stop,
    Restart,
    Backup,
    Update,
    ApplyConfig
}

public record ServerDef(
    string Id,
    string Name,
    string VmName,
    GameType GameType,
    string GuestExePath,            // Field name kept for back-compat. For BareMetal, this is the host path.
    string GuestWorkingDir,         // Field name kept for back-compat. For BareMetal, this is the host working dir.
    string[] StartArgs,
    string[] SaveDirs,
    string? SteamAppId,
    string? GuestCredentialId,
    string? LogPathInGuest,
    string? RconHost = null,
    int? RconPort = null,
    string? RconPassword = null,
    HostingMode HostingMode = HostingMode.Vm,           // back-compat default for existing servers.json entries
    string? ScheduledTaskName = null,                   // if set, Start/Stop drive Windows Task Scheduler instead of Process.Start
    string[]? StopProcessNames = null,                  // extra .exe leaf names to kill on Stop (in addition to GuestExePath's leaf)
    string? DiscordWebhookUrl = null                    // optional: POST lifecycle events (start/stop/crash/backup/update) here
);

public record ServerStatus(
    string Id,
    VmState VmState,
    ProcessState ProcessState,
    int? PidInGuest,
    DateTimeOffset? StartedAt,
    DateTimeOffset? LastBackupAt,
    string? LastError,
    double? CpuPercent = null,
    long? MemoryMB = null,
    Dictionary<string, string>? Metadata = null
);

public record LogLine(
    string ServerId,
    DateTimeOffset At,
    string Source,
    string Text
);

public record ActionResult(
    bool Success,
    string Message,
    string? CorrelationId
);

public record BackupInfo(
    string Id,
    string ServerId,
    DateTimeOffset CreatedAt,
    string CheckpointName,
    long? SizeBytes
);

public record DiscordWebhookTestRequest(string WebhookUrl);

public record UpdateStatus(
    string CurrentVersion,
    string? LatestVersion,
    string? LatestUrl,
    bool UpdateAvailable,
    DateTimeOffset? CheckedAt);

/// <summary>
/// Request to provision a brand-new dedicated server from scratch via SteamCMD.
/// The agent downloads SteamCMD if missing, runs +app_update, then registers the supplied
/// ServerDef. SteamAppId and ServerDef.SteamAppId should match — backend uses the request
/// field for the actual install command.
/// </summary>
public record InstallServerRequest(
    string SteamAppId,        // Steam dedicated-server app ID, e.g. "896660" (Valheim)
    string InstallPath,       // Where the server should land on the agent host
    ServerDef ServerDef);     // The ServerDef to register on success (GuestWorkingDir = InstallPath)

/// <summary>Single line of progress for an in-flight install. Streamed via SignalR.</summary>
public record InstallProgress(
    string JobId,
    string Phase,             // "queued" | "steamcmd" | "register" | "done" | "failed"
    string Line,              // Latest stdout/log line, or a human-readable status message
    int? PercentHint,         // Best-effort 0-100 from SteamCMD's "Update state (0x[…]) downloading, progress: 42.51" lines
    bool Finished,
    bool Success);

/// <summary>Synchronous response from POST /api/servers/install — the job is now running async.</summary>
public record InstallJobAck(string JobId, string Message);

public enum TokenRole
{
    Admin,
    ReadOnly
}

public record CreateTokenRequest(string Id, string Name, TokenRole Role);

public record TokenMetadata(string Id, string Name, string Token, TokenRole Role, DateTimeOffset CreatedAt);

public record MaintenanceSchedule(
    bool DailyRestartEnabled = false,
    int DailyRestartHour = 5,            // local-time hour 0-23
    bool WeeklyUpdateEnabled = false,
    DayOfWeek WeeklyUpdateDay = DayOfWeek.Wednesday,
    int WeeklyUpdateHour = 4,
    bool HourlyBackupEnabled = false,
    int HourlyBackupMinute = 0            // 0-59
);
