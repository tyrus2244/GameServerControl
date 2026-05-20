using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ServerViewModel : ObservableObject
{
    public sealed record Callbacks(
        Action<ServerViewModel> Edit,
        Action<ServerViewModel> Delete,
        Action<ServerViewModel> Configure,
        Action<ServerViewModel> Console,
        Action<ServerViewModel> Log,
        Action<ServerViewModel>? Mods = null,
        Action<ServerViewModel>? Backups = null,
        Action<ServerViewModel>? Schedule = null,
        Action<ServerViewModel>? Stats = null);

    private readonly Func<AgentClient?> _getClient;
    private readonly Action<string> _toast;
    private readonly Callbacks _cb;

    public ServerDef Def { get; private set; }
    /// <summary>Which agent owns this server — empty for legacy single-agent flows.</summary>
    public string AgentId { get; }
    /// <summary>Display name of the owning agent (e.g. "Home server"). Shown on the card.</summary>
    public string AgentNickname { get; }
    public bool HasMultipleAgents { get; set; }

    public ServerViewModel(ServerDef def, Func<AgentClient?> getClient, Action<string> toast, Callbacks callbacks,
        string agentId = "", string agentNickname = "")
    {
        _getClient = getClient;
        _toast = toast;
        _cb = callbacks;
        Def = def;
        Id = def.Id;
        Name = def.Name;
        VmName = def.VmName;
        GameType = def.GameType.ToString();
        AgentId = agentId;
        AgentNickname = agentNickname;
    }

    [RelayCommand]
    private void Edit() => _cb.Edit(this);

    [RelayCommand]
    private void Delete() => _cb.Delete(this);

    [RelayCommand]
    private void Configure() => _cb.Configure(this);

    [RelayCommand]
    private void Console() => _cb.Console(this);

    [RelayCommand]
    private void Log() => _cb.Log(this);

    [RelayCommand]
    private void Mods() => _cb.Mods?.Invoke(this);

    [RelayCommand]
    private void Backups() => _cb.Backups?.Invoke(this);

    [RelayCommand]
    private void Schedule() => _cb.Schedule?.Invoke(this);

    [RelayCommand]
    private void Stats() => _cb.Stats?.Invoke(this);

    public bool HasRcon => Def.RconPort is > 0;
    public bool HasScheduledTask => !string.IsNullOrWhiteSpace(Def.ScheduledTaskName);
    public bool HasLogPath => !string.IsNullOrWhiteSpace(Def.LogPathInGuest);

    [ObservableProperty] private bool isAutostartEnabled;
    [ObservableProperty] private bool isAutostartLoading;

    public async Task LoadAutostartAsync()
    {
        var c = _getClient();
        if (c is null || !HasScheduledTask) return;
        IsAutostartLoading = true;
        try
        {
            var s = await c.GetAutostartAsync(Id);
            if (s.Supported && s.Enabled is bool b) IsAutostartEnabled = b;
        }
        catch { /* tolerate */ }
        finally { IsAutostartLoading = false; }
    }

    public async Task SetAutostartAsync(bool desired)
    {
        var c = _getClient();
        if (c is null || !HasScheduledTask) return;
        IsAutostartLoading = true;
        try
        {
            await c.SetAutostartAsync(Id, desired);
            IsAutostartEnabled = desired;
            _toast($"[{Name}] Autostart {(desired ? "enabled" : "disabled")}");
        }
        catch (Exception ex)
        {
            _toast($"[{Name}] Autostart change failed: {ex.Message}");
        }
        finally { IsAutostartLoading = false; }
    }

    public string Id { get; }
    public string Name { get; }
    public string VmName { get; }
    public string GameType { get; }

    [ObservableProperty] private VmState vmState = VmState.Unknown;
    [ObservableProperty] private ProcessState processState = ProcessState.Unknown;
    [ObservableProperty] private int? pidInGuest;
    [ObservableProperty] private DateTimeOffset? startedAt;
    [ObservableProperty] private DateTimeOffset? lastBackupAt;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string? lastError;
    [ObservableProperty] private double? cpuPercent;
    [ObservableProperty] private long? memoryMB;
    [ObservableProperty] private string? inviteCode;

    public string GameIcon
    {
        get
        {
            if (Def.GameType == GameServerControl.Shared.GameType.Windrose) return "⛵";
            if (Def.GameType == GameServerControl.Shared.GameType.Minecraft) return "⛏";
            if (Def.GameType == GameServerControl.Shared.GameType.SteamGeneric)
            {
                return Def.SteamAppId switch
                {
                    "896660" => "⚔",      // Valheim
                    "2394010" => "🐾",    // Palworld
                    "1690800" => "⚙",    // Satisfactory
                    "2430930" => "🦖",    // ARK ASA
                    "376030"  => "🦖",    // ARK SE
                    "258550" => "🔧",     // Rust
                    "294420" => "🧟",     // 7 Days to Die
                    "105600" => "🌳",     // Terraria
                    "343050" => "❄",     // Don't Starve Together
                    "380870" => "🧟",     // Project Zomboid
                    _ => "🎮"
                };
            }
            return "🎮";
        }
    }

    public bool HasInviteCode => !string.IsNullOrEmpty(InviteCode);
    public bool HasCpuRam => CpuPercent is not null || MemoryMB is not null;
    public string CpuRamText
    {
        get
        {
            var c = CpuPercent is double cp ? $"{cp:0.#}% CPU" : "";
            var m = MemoryMB is long mb ? $"{mb:N0} MB" : "";
            return string.Join(" · ", new[] { c, m }.Where(s => !string.IsNullOrEmpty(s)));
        }
    }

    public bool IsOn => ProcessState == ProcessState.Running;

    public string VmBadge => VmState switch
    {
        VmState.Running => "VM RUNNING",
        VmState.Off => "VM OFF",
        VmState.Starting => "VM STARTING",
        VmState.Stopping => "VM STOPPING",
        VmState.Saved => "VM SAVED",
        VmState.Paused => "VM PAUSED",
        VmState.NotApplicable => "BARE METAL",
        _ => "VM ?"
    };

    public bool IsBareMetal => Def.HostingMode == GameServerControl.Shared.HostingMode.BareMetal;
    public string HostingLabel => IsBareMetal ? "Bare metal" : ("VM: " + VmName);

    public string ProcBadge => ProcessState switch
    {
        ProcessState.Running => "GAME UP" + (PidInGuest is int p ? $" · pid {p}" : ""),
        ProcessState.NotRunning => "GAME DOWN",
        ProcessState.Starting => "GAME STARTING",
        ProcessState.Stopping => "GAME STOPPING",
        _ => "GAME ?"
    };

    // Matched to Dark.xaml: Good (powered) = warm orange-red, Bad (offline) = desat grey-red,
    // Unknown = muted text-dim, transitional = pale rose info color.
    public string VmBadgeColor => VmState == VmState.Running ? "#FF6B3D" :
        VmState == VmState.Off ? "#6F575A" :
        VmState == VmState.Unknown ? "#8A6B70" : "#E5B0B4";

    public string ProcBadgeColor => ProcessState == ProcessState.Running ? "#FF6B3D" :
        ProcessState == ProcessState.NotRunning ? "#6F575A" :
        ProcessState == ProcessState.Unknown ? "#8A6B70" : "#E5B0B4";

    public void ApplyStatus(ServerStatus s)
    {
        VmState = s.VmState;
        ProcessState = s.ProcessState;
        PidInGuest = s.PidInGuest;
        StartedAt = s.StartedAt;
        LastBackupAt = s.LastBackupAt;
        LastError = s.LastError;
        CpuPercent = s.CpuPercent;
        MemoryMB = s.MemoryMB;
        if (s.Metadata is not null && s.Metadata.TryGetValue("InviteCode", out var inv))
            InviteCode = inv;
        else if (s.Metadata is not null)
            InviteCode = null;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(VmBadge));
        OnPropertyChanged(nameof(ProcBadge));
        OnPropertyChanged(nameof(VmBadgeColor));
        OnPropertyChanged(nameof(ProcBadgeColor));
        OnPropertyChanged(nameof(HasInviteCode));
        OnPropertyChanged(nameof(HasCpuRam));
        OnPropertyChanged(nameof(CpuRamText));
    }

    [RelayCommand]
    private Task Start() => Invoke(ServerActionKind.Start, "Starting…");

    [RelayCommand]
    private Task Stop() => Invoke(ServerActionKind.Stop, "Stopping…");

    [RelayCommand]
    private Task Restart() => Invoke(ServerActionKind.Restart, "Restarting…");

    [RelayCommand]
    private Task Backup() => Invoke(ServerActionKind.Backup, "Creating checkpoint…");

    [RelayCommand]
    private Task Update() => Invoke(ServerActionKind.Update, "Running SteamCMD update…");

    [RelayCommand]
    private Task ApplyConfig() => Invoke(ServerActionKind.ApplyConfig, "Applying config (restart)…");

    public async Task ToggleAsync(bool desired)
    {
        if (desired && !IsOn) await Invoke(ServerActionKind.Start, "Starting…");
        else if (!desired && IsOn) await Invoke(ServerActionKind.Stop, "Stopping…");
    }

    private async Task Invoke(ServerActionKind kind, string busyMessage)
    {
        var client = _getClient();
        if (client is null) { _toast("Not connected to agent. Open settings to configure."); return; }
        if (IsBusy) return;
        IsBusy = true;
        _toast($"[{Name}] {busyMessage}");
        try
        {
            var r = await client.ActionAsync(Id, kind);
            _toast($"[{Name}] {(r.Success ? "OK" : "FAIL")}: {r.Message}");
            // Start/Stop now also flip the scheduled task's enabled state — refresh so the UI checkbox follows.
            if (HasScheduledTask && kind is ServerActionKind.Start or ServerActionKind.Stop or ServerActionKind.Restart)
                await LoadAutostartAsync();
        }
        catch (Exception ex)
        {
            _toast($"[{Name}] ERROR: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
