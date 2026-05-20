using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

/// <summary>
/// Player list with kick/ban/broadcast. Auto-refreshes every 10s while open. The full
/// RCON console (raw command input, shutdown countdown, save-world) lives in RconWindow.
/// </summary>
public partial class PlayersWindow : Window
{
    private readonly AgentClient _client;
    private readonly ServerDef _server;
    private readonly DispatcherTimer _timer;
    private readonly ObservableCollection<RconPlayer> _players = new();

    public PlayersWindow(AgentClient client, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _server = server;
        HeaderText.Text = $"Players — {server.Name}";
        PlayersList.ItemsSource = _players;

        // Poll once on open + every 10s thereafter while AutoBox is checked. Stop on close so a
        // closed-but-not-disposed window doesn't keep hitting RCON.
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += async (_, _) =>
        {
            if (AutoBox.IsChecked == true) await ReloadAsync();
        };
        Loaded += async (_, _) => { await ReloadAsync(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "Refreshing…";
        try
        {
            var list = await _client.RconListPlayersAsync(_server.Id);
            _players.Clear();
            foreach (var p in list) _players.Add(p);
            EmptyText.Visibility = _players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"{_players.Count} player(s)  ·  last refresh {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex) { StatusText.Text = "RCON unreachable: " + ex.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private async void Kick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string sid) return;
        var name = _players.FirstOrDefault(p => p.SteamId == sid)?.Name ?? sid;
        try
        {
            var r = await _client.RconRunAsync(_server.Id, RconStandardCommand.KickPlayer, sid);
            StatusText.Text = r.Success ? $"Kicked {name}." : "Kick failed: " + (r.Error ?? "?");
            await ReloadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Kick failed: " + ex.Message; }
    }

    private async void Ban_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string sid) return;
        var name = _players.FirstOrDefault(p => p.SteamId == sid)?.Name ?? sid;
        var ok = MessageBox.Show($"Ban {name} (SteamID {sid})?\n\nThis writes to the server's ban list and disconnects them immediately.",
            "Ban player", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            var r = await _client.RconRunAsync(_server.Id, RconStandardCommand.BanPlayer, sid);
            StatusText.Text = r.Success ? $"Banned {name}." : "Ban failed: " + (r.Error ?? "?");
            await ReloadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Ban failed: " + ex.Message; }
    }

    private async void Broadcast_Click(object sender, RoutedEventArgs e)
    {
        var text = (BroadcastBox.Text ?? "").Trim();
        if (text.Length == 0) return;
        try
        {
            var r = await _client.RconRunAsync(_server.Id, RconStandardCommand.BroadcastMessage, text);
            StatusText.Text = r.Success ? "Broadcast sent." : "Broadcast failed: " + (r.Error ?? "?");
            if (r.Success) BroadcastBox.Text = "";
        }
        catch (Exception ex) { StatusText.Text = "Broadcast failed: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
