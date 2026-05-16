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
    string[]? StopProcessNames = null                   // extra .exe leaf names to kill on Stop (in addition to GuestExePath's leaf)
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

public record MaintenanceSchedule(
    bool DailyRestartEnabled = false,
    int DailyRestartHour = 5,            // local-time hour 0-23
    bool WeeklyUpdateEnabled = false,
    DayOfWeek WeeklyUpdateDay = DayOfWeek.Wednesday,
    int WeeklyUpdateHour = 4,
    bool HourlyBackupEnabled = false,
    int HourlyBackupMinute = 0            // 0-59
);
