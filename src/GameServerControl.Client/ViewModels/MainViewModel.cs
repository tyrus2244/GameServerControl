using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameServerControl.Client.Services;
using GameServerControl.Client.Views;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

/// <summary>
/// Federation-aware dashboard. Connects to N agents concurrently and merges their servers into
/// a single list. Each ServerViewModel carries its AgentId so per-server actions route back to
/// the correct AgentClient via the <see cref="_clients"/> lookup.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private AppSettings _settings = AppSettings.Load();
    // Per-agent state — empty when no agents configured. Keyed by AgentConfig.Id.
    private readonly Dictionary<string, AgentClient> _clients = new();
    private readonly Dictionary<string, StatusHubClient> _hubs = new();
    private readonly Dictionary<string, string> _agentNicknames = new();
    private CancellationTokenSource? _pollCts;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<string> Toasts { get; } = new();

    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string connectionLabel = "Disconnected";
    [ObservableProperty] private string agentUrl = "";

    public MainViewModel()
    {
        AgentUrl = string.Join(" · ", _settings.Agents.Select(a => a.Nickname));
        _ = ConnectAsync();
    }

    /// <summary>Look up the right AgentClient for a server VM. Server VMs without an AgentId fall back to the first client.</summary>
    private AgentClient? ClientFor(ServerViewModel vm)
    {
        if (!string.IsNullOrEmpty(vm.AgentId) && _clients.TryGetValue(vm.AgentId, out var c)) return c;
        return _clients.Values.FirstOrDefault();
    }

    private AgentClient? ClientFor(string agentId)
        => _clients.TryGetValue(agentId, out var c) ? c : _clients.Values.FirstOrDefault();

    private StatusHubClient? HubFor(string agentId)
        => _hubs.TryGetValue(agentId, out var h) ? h : _hubs.Values.FirstOrDefault();

    private void Toast(string msg)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            var stamped = $"[{DateTime.Now:HH:mm:ss}] {msg}";
            Toasts.Insert(0, stamped);
            while (Toasts.Count > 200) Toasts.RemoveAt(Toasts.Count - 1);
        });
    }

    [RelayCommand]
    private async Task Reconnect() => await ConnectAsync();

    [RelayCommand]
    private void OpenTokens()
    {
        if (_clients.Count == 0) { Toast("Connect to an agent first (Settings)."); return; }
        // Token management is per-agent; for now we use the first agent.
        var win = new TokensWindow(_clients.Values.First()) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    [RelayCommand]
    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            _settings = win.Result;
            _settings.Save();
            AgentUrl = string.Join(" · ", _settings.Agents.Select(a => a.Nickname));
            _ = ConnectAsync();
        }
    }

    [RelayCommand]
    private async Task AddServer()
    {
        if (_clients.Count == 0) { Toast("Not connected — open Settings first."); return; }
        // Multi-agent: new server is added to the first agent. UX could let the user pick later.
        var client = _clients.Values.First();
        var win = new ServerEditorWindow { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result is null) return;
        try
        {
            await client.CreateServerAsync(win.Result);
            Toast($"Added server '{win.Result.Name}'.");
            await ReloadServerList();
        }
        catch (Exception ex)
        {
            Toast($"Add failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void DiscoverServers()
    {
        if (_clients.Count == 0) { Toast("Not connected — open Settings first."); return; }
        var win = new DiscoverServersWindow(_clients.Values.First(), Toast, ReloadServerList)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    private async Task EditServer(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) return;
        var win = new ServerEditorWindow(vm.Def) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result is null) return;
        try
        {
            await c.UpdateServerAsync(vm.Id, win.Result);
            Toast($"Updated '{win.Result.Name}'.");
            await ReloadServerList();
        }
        catch (Exception ex) { Toast($"Update failed: {ex.Message}"); }
    }

    private void OpenModsWindow(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected — open Settings first."); return; }
        var win = new ModsWindow(c, vm.Id, vm.Def.Name, Toast) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private void OpenBackupsWindow(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected — open Settings first."); return; }
        var win = new BackupsWindow(c, vm.Def) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private void OpenScheduleWindow(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected — open Settings first."); return; }
        var win = new ScheduleWindow(c, vm.Def) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private void OpenStatsWindow(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected — open Settings first."); return; }
        var win = new ResourceMonitorWindow(c, vm.Def) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private ServerViewModel.Callbacks BuildCallbacks() => new(
        Edit: vm => _ = EditServer(vm),
        Delete: vm => _ = DeleteServer(vm),
        Configure: vm => _ = ConfigureServer(vm),
        Console: vm => OpenConsole(vm),
        Log: vm => OpenLogWindow(vm),
        Mods: vm => OpenModsWindow(vm),
        Backups: vm => OpenBackupsWindow(vm),
        Schedule: vm => OpenScheduleWindow(vm),
        Stats: vm => OpenStatsWindow(vm));

    private void OpenLogWindow(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        var h = string.IsNullOrEmpty(vm.AgentId) ? _hubs.Values.FirstOrDefault() : HubFor(vm.AgentId);
        if (c is null || h is null) { Toast("Not connected."); return; }
        if (!vm.HasLogPath)
        {
            MessageBox.Show(
                "No LogPathInGuest set for this server.\n\nOpen Edit and set the path to the server's log file (must be a path on the host for bare-metal servers).",
                "No log path",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new LogWindow(c, h, vm.Def) { Owner = Application.Current.MainWindow };
        w.Show();
    }

    private void OpenConsole(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected."); return; }
        if (vm.Def.RconPort is null || vm.Def.RconPort == 0)
        {
            MessageBox.Show(
                "RCON isn't configured for this server.\n\nOpen Edit and set:\n  • RCON Host (auto, or VM IP)\n  • RCON Port (e.g. 25575 for Palworld)\n  • RCON Password\n\nThen make sure RCON is enabled in the game's config (Configure → RCON section).",
                "RCON not configured",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new RconWindow(c, vm.Def) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private async Task ConfigureServer(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) { Toast("Not connected."); return; }
        Toast($"Loading config for '{vm.Name}'…");
        try
        {
            var payload = await c.GetServerConfigAsync(vm.Id);
            var win = new ServerConfigWindow(c, vm.Def, payload.Schema, payload.Values)
            {
                Owner = Application.Current.MainWindow
            };
            var ok = win.ShowDialog() == true;
            if (ok)
            {
                Toast($"Config saved for '{vm.Name}'.");
                if (win.RestartRequested)
                {
                    Toast($"Restarting '{vm.Name}'…");
                    var r = await c.ActionAsync(vm.Id, ServerActionKind.Restart);
                    Toast($"[{vm.Name}] {(r.Success ? "Restart OK" : "Restart failed")}: {r.Message}");
                }
            }
        }
        catch (Exception ex) { Toast($"Configure failed: {ex.Message}"); }
    }

    private async Task DeleteServer(ServerViewModel vm)
    {
        var c = ClientFor(vm);
        if (c is null) return;
        var confirm = MessageBox.Show(
            $"Delete '{vm.Name}'?\n\nThis only removes it from the control UI — the VM and its files are not touched.",
            "Delete server",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            await c.DeleteServerAsync(vm.Id);
            Toast($"Deleted '{vm.Name}'.");
            await ReloadServerList();
        }
        catch (Exception ex) { Toast($"Delete failed: {ex.Message}"); }
    }

    private async Task ReloadServerList()
    {
        if (_clients.Count == 0) return;
        var collected = new List<(string agentId, string nickname, ServerDef def)>();
        foreach (var (agentId, client) in _clients)
        {
            try
            {
                var defs = await client.ListServersAsync();
                var nick = _agentNicknames.TryGetValue(agentId, out var n) ? n : agentId;
                foreach (var d in defs) collected.Add((agentId, nick, d));
            }
            catch (Exception ex) { Toast($"List servers failed for agent '{agentId}': {ex.Message}"); }
        }
        var multi = _clients.Count > 1;
        Application.Current.Dispatcher.Invoke(() =>
        {
            Servers.Clear();
            foreach (var (agentId, nick, d) in collected)
            {
                var svm = new ServerViewModel(d, () => ClientFor(agentId), Toast, BuildCallbacks(),
                    agentId: agentId, agentNickname: nick) { HasMultipleAgents = multi };
                Servers.Add(svm);
                _ = svm.LoadAutostartAsync();
            }
        });
        await RefreshAll();
    }

    private async Task PollLoop(CancellationToken ct)
    {
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct); }
            catch (OperationCanceledException) { return; }
            if (_clients.Count == 0) continue;

            try
            {
                // Pull status from every agent in parallel; failures isolated per agent.
                var tasks = _clients.Select(async kv =>
                {
                    try { return (kv.Key, await kv.Value.GetAllStatusAsync(ct)); }
                    catch { return (kv.Key, new List<ServerStatus>()); }
                }).ToArray();
                var results = await Task.WhenAll(tasks);
                consecutiveFailures = 0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var (agentId, statuses) in results)
                    foreach (var s in statuses)
                    {
                        // Match on (AgentId, ServerId) — server IDs aren't necessarily unique across agents.
                        var vm = Servers.FirstOrDefault(x => x.AgentId == agentId && x.Id == s.Id);
                        vm?.ApplyStatus(s);
                    }
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        ConnectionLabel = $"Connected ({_clients.Count} agent{(_clients.Count == 1 ? "" : "s")}, poll)";
                    }
                });
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                consecutiveFailures++;
                if (consecutiveFailures >= 2)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        IsConnected = false;
                        ConnectionLabel = $"Lost contact ({consecutiveFailures} polls) — retrying…";
                    });
                }
                System.Diagnostics.Debug.WriteLine("Poll failed: " + ex.Message);
            }
        }
    }

    [RelayCommand]
    private async Task RefreshAll()
    {
        foreach (var (agentId, client) in _clients)
        {
            try
            {
                var all = await client.GetAllStatusAsync();
                foreach (var s in all)
                {
                    var vm = Servers.FirstOrDefault(x => x.AgentId == agentId && x.Id == s.Id);
                    vm?.ApplyStatus(s);
                }
            }
            catch (Exception ex) { Toast($"Refresh failed for '{agentId}': {ex.Message}"); }
        }
    }

    public async Task ConnectAsync()
    {
        ConnectionLabel = "Connecting…";
        IsConnected = false;

        _pollCts?.Cancel();
        _pollCts = null;

        // Tear down any existing hubs + clients
        foreach (var h in _hubs.Values)
            try { await h.DisposeAsync(); } catch { /* ignore */ }
        _hubs.Clear();
        _clients.Clear();
        _agentNicknames.Clear();

        if (_settings.Agents.Count == 0)
        {
            ConnectionLabel = "No agents configured";
            Toast("Open Settings to add an agent (URL + token).");
            return;
        }

        int connected = 0;
        foreach (var agent in _settings.Agents)
        {
            if (string.IsNullOrWhiteSpace(agent.Url) || string.IsNullOrWhiteSpace(agent.Token)) continue;
            try
            {
                var client = new AgentClient(agent.Url, agent.Token);
                if (!await client.PingAsync())
                {
                    Toast($"Agent '{agent.Nickname}' unreachable at {agent.Url}");
                    continue;
                }
                _clients[agent.Id] = client;
                _agentNicknames[agent.Id] = string.IsNullOrWhiteSpace(agent.Nickname) ? agent.Id : agent.Nickname;

                var hub = new StatusHubClient(agent.Url, agent.Token);
                var thisAgent = agent;   // capture for closure
                hub.StatusChanged += s => Application.Current.Dispatcher.Invoke(() =>
                {
                    var vm = Servers.FirstOrDefault(x => x.AgentId == thisAgent.Id && x.Id == s.Id);
                    vm?.ApplyStatus(s);
                });
                hub.LogLine += l => Toast($"[{thisAgent.Nickname}/{l.ServerId}/{l.Source}] {l.Text}");
                await hub.StartAsync();
                _hubs[agent.Id] = hub;
                connected++;
            }
            catch (Exception ex) { Toast($"Connect to '{agent.Nickname}' failed: {ex.Message}"); }
        }

        if (connected == 0)
        {
            ConnectionLabel = "No agents reachable";
            return;
        }

        await ReloadServerList();
        IsConnected = true;
        ConnectionLabel = $"Connected to {connected} agent{(connected == 1 ? "" : "s")}";

        _pollCts = new CancellationTokenSource();
        _ = Task.Run(() => PollLoop(_pollCts.Token));
    }
}
