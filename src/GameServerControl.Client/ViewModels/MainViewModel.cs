using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameServerControl.Client.Services;
using GameServerControl.Client.Views;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private AppSettings _settings = AppSettings.Load();
    private AgentClient? _client;
    private StatusHubClient? _hub;
    private CancellationTokenSource? _pollCts;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);

    public ObservableCollection<ServerViewModel> Servers { get; } = new();
    public ObservableCollection<string> Toasts { get; } = new();

    [ObservableProperty] private bool isConnected;
    [ObservableProperty] private string connectionLabel = "Disconnected";
    [ObservableProperty] private string agentUrl = "";

    public MainViewModel()
    {
        AgentUrl = _settings.AgentUrl;
        _ = ConnectAsync();
    }

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
    private void OpenSettings()
    {
        var win = new SettingsWindow(_settings) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() == true)
        {
            _settings = win.Result;
            _settings.Save();
            AgentUrl = _settings.AgentUrl;
            _ = ConnectAsync();
        }
    }

    [RelayCommand]
    private async Task AddServer()
    {
        if (_client is null) { Toast("Not connected — open Settings first."); return; }
        var win = new ServerEditorWindow { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result is null) return;
        try
        {
            await _client.CreateServerAsync(win.Result);
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
        if (_client is null) { Toast("Not connected — open Settings first."); return; }
        var win = new DiscoverServersWindow(_client, Toast, ReloadServerList)
        {
            Owner = Application.Current.MainWindow
        };
        win.ShowDialog();
    }

    private async Task EditServer(ServerViewModel vm)
    {
        if (_client is null) return;
        var win = new ServerEditorWindow(vm.Def) { Owner = Application.Current.MainWindow };
        if (win.ShowDialog() != true || win.Result is null) return;
        try
        {
            await _client.UpdateServerAsync(vm.Id, win.Result);
            Toast($"Updated '{win.Result.Name}'.");
            await ReloadServerList();
        }
        catch (Exception ex)
        {
            Toast($"Update failed: {ex.Message}");
        }
    }

    private void OpenLogWindow(ServerViewModel vm)
    {
        if (_client is null || _hub is null) { Toast("Not connected."); return; }
        if (!vm.HasLogPath)
        {
            MessageBox.Show(
                "No LogPathInGuest set for this server.\n\nOpen Edit and set the path to the server's log file (must be a path on the host for bare-metal servers).",
                "No log path",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var w = new LogWindow(_client, _hub, vm.Def) { Owner = Application.Current.MainWindow };
        w.Show();
    }

    private void OpenConsole(ServerViewModel vm)
    {
        if (_client is null) { Toast("Not connected."); return; }
        if (vm.Def.RconPort is null || vm.Def.RconPort == 0)
        {
            MessageBox.Show(
                "RCON isn't configured for this server.\n\nOpen Edit and set:\n  • RCON Host (auto, or VM IP)\n  • RCON Port (e.g. 25575 for Palworld)\n  • RCON Password\n\nThen make sure RCON is enabled in the game's config (Configure → RCON section).",
                "RCON not configured",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var win = new RconWindow(_client, vm.Def) { Owner = Application.Current.MainWindow };
        win.Show();
    }

    private async Task ConfigureServer(ServerViewModel vm)
    {
        if (_client is null) { Toast("Not connected."); return; }
        Toast($"Loading config for '{vm.Name}'…");
        try
        {
            var payload = await _client.GetServerConfigAsync(vm.Id);
            var win = new ServerConfigWindow(_client, vm.Def, payload.Schema, payload.Values)
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
                    var r = await _client.ActionAsync(vm.Id, ServerActionKind.Restart);
                    Toast($"[{vm.Name}] {(r.Success ? "Restart OK" : "Restart failed")}: {r.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Toast($"Configure failed: {ex.Message}");
        }
    }

    private async Task DeleteServer(ServerViewModel vm)
    {
        if (_client is null) return;
        var confirm = MessageBox.Show(
            $"Delete '{vm.Name}'?\n\nThis only removes it from the control UI — the VM and its files are not touched.",
            "Delete server",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;
        try
        {
            await _client.DeleteServerAsync(vm.Id);
            Toast($"Deleted '{vm.Name}'.");
            await ReloadServerList();
        }
        catch (Exception ex)
        {
            Toast($"Delete failed: {ex.Message}");
        }
    }

    private async Task ReloadServerList()
    {
        if (_client is null) return;
        var defs = await _client.ListServersAsync();
        Application.Current.Dispatcher.Invoke(() =>
        {
            Servers.Clear();
            foreach (var d in defs)
                {
                    var svm = new ServerViewModel(d, () => _client, Toast,
                        vm => _ = EditServer(vm),
                        vm => _ = DeleteServer(vm),
                        vm => _ = ConfigureServer(vm),
                        vm => OpenConsole(vm),
                        vm => OpenLogWindow(vm));
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
            if (_client is null) continue;

            try
            {
                var all = await _client.GetAllStatusAsync(ct);
                consecutiveFailures = 0;
                Application.Current.Dispatcher.Invoke(() =>
                {
                    foreach (var s in all)
                    {
                        var vm = Servers.FirstOrDefault(x => x.Id == s.Id);
                        vm?.ApplyStatus(s);
                    }
                    if (!IsConnected)
                    {
                        IsConnected = true;
                        ConnectionLabel = "Connected (poll)";
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
        if (_client is null) return;
        try
        {
            var all = await _client.GetAllStatusAsync();
            foreach (var s in all)
            {
                var vm = Servers.FirstOrDefault(x => x.Id == s.Id);
                vm?.ApplyStatus(s);
            }
        }
        catch (Exception ex) { Toast("Refresh failed: " + ex.Message); }
    }

    public async Task ConnectAsync()
    {
        ConnectionLabel = "Connecting…";
        IsConnected = false;

        // Stop any previous polling loop
        _pollCts?.Cancel();
        _pollCts = null;

        if (_hub is not null)
        {
            try { await _hub.DisposeAsync(); } catch { /* ignore */ }
            _hub = null;
        }
        _client = null;

        if (string.IsNullOrWhiteSpace(_settings.AgentUrl) || string.IsNullOrWhiteSpace(_settings.ApiToken))
        {
            ConnectionLabel = "No agent configured";
            Toast("Open Settings (gear) to set the agent URL and token.");
            return;
        }

        try
        {
            var client = new AgentClient(_settings.AgentUrl, _settings.ApiToken);
            if (!await client.PingAsync())
            {
                ConnectionLabel = "Agent unreachable";
                Toast("Could not reach " + _settings.AgentUrl);
                return;
            }
            _client = client;

            var defs = await client.ListServersAsync();
            Application.Current.Dispatcher.Invoke(() =>
            {
                Servers.Clear();
                foreach (var d in defs)
                {
                    var svm = new ServerViewModel(d, () => _client, Toast,
                        vm => _ = EditServer(vm),
                        vm => _ = DeleteServer(vm),
                        vm => _ = ConfigureServer(vm),
                        vm => OpenConsole(vm),
                        vm => OpenLogWindow(vm));
                    Servers.Add(svm);
                    _ = svm.LoadAutostartAsync();
                }
            });

            var hub = new StatusHubClient(_settings.AgentUrl, _settings.ApiToken);
            hub.StatusChanged += s => Application.Current.Dispatcher.Invoke(() =>
            {
                var vm = Servers.FirstOrDefault(x => x.Id == s.Id);
                vm?.ApplyStatus(s);
            });
            hub.LogLine += l => Toast($"[{l.ServerId}/{l.Source}] {l.Text}");
            hub.ConnectionChanged += ok =>
            {
                IsConnected = ok;
                ConnectionLabel = ok ? "Connected" : "Reconnecting…";
            };
            await hub.StartAsync();
            _hub = hub;
            IsConnected = true;
            ConnectionLabel = "Connected to " + _settings.AgentUrl;
            await RefreshAll();

            // Polling fallback — runs forever, survives SignalR drops, and is the source of truth
            // for connection health going forward. SignalR provides low-latency push when alive;
            // this gives correctness even when it's silent.
            _pollCts = new CancellationTokenSource();
            _ = Task.Run(() => PollLoop(_pollCts.Token));
        }
        catch (Exception ex)
        {
            ConnectionLabel = "Connection failed";
            Toast("Connect failed: " + ex.Message);
        }
    }
}
