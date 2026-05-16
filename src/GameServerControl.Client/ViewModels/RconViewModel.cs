using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class RconViewModel : ObservableObject
{
    private readonly AgentClient _client;
    public ServerDef Server { get; }

    public ObservableCollection<RconPlayer> Players { get; } = new();
    public ObservableCollection<string> Log { get; } = new();

    [ObservableProperty] private string commandText = "";
    [ObservableProperty] private string broadcastText = "";
    [ObservableProperty] private string shutdownSeconds = "30";
    [ObservableProperty] private string shutdownMessage = "Server restarting";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string status = "Ready";
    [ObservableProperty] private RconPlayer? selectedPlayer;

    public RconViewModel(AgentClient client, ServerDef server)
    {
        _client = client;
        Server = server;
        _ = RefreshPlayers();
    }

    private void Append(string line)
    {
        var stamped = $"[{DateTime.Now:HH:mm:ss}] {line}";
        Application.Current.Dispatcher.Invoke(() =>
        {
            Log.Insert(0, stamped);
            while (Log.Count > 200) Log.RemoveAt(Log.Count - 1);
        });
    }

    [RelayCommand]
    private async Task RefreshPlayers()
    {
        if (IsBusy) return;
        IsBusy = true; Status = "Listing players…";
        try
        {
            var list = await _client.RconListPlayersAsync(Server.Id);
            Application.Current.Dispatcher.Invoke(() =>
            {
                Players.Clear();
                foreach (var p in list) Players.Add(p);
            });
            Status = $"{list.Length} player(s)";
        }
        catch (Exception ex) { Status = "Failed: " + ex.Message; Append("ERROR: " + ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private Task SaveWorld() => Run(RconStandardCommand.Save, null, "save");

    [RelayCommand]
    private Task Broadcast() => Run(RconStandardCommand.BroadcastMessage, BroadcastText, "broadcast");

    [RelayCommand]
    private Task Shutdown() => Run(RconStandardCommand.Shutdown,
        $"{ShutdownSeconds} {ShutdownMessage?.Replace(" ", "_") ?? ""}".Trim(), "shutdown");

    [RelayCommand]
    private async Task KickSelected()
    {
        if (SelectedPlayer is null) { Status = "No player selected."; return; }
        await Run(RconStandardCommand.KickPlayer, SelectedPlayer.SteamId, "kick " + SelectedPlayer.Name);
        await RefreshPlayers();
    }

    [RelayCommand]
    private async Task BanSelected()
    {
        if (SelectedPlayer is null) { Status = "No player selected."; return; }
        var ok = MessageBox.Show($"Ban {SelectedPlayer.Name} (SteamID {SelectedPlayer.SteamId})?",
            "Ban player", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        await Run(RconStandardCommand.BanPlayer, SelectedPlayer.SteamId, "ban " + SelectedPlayer.Name);
        await RefreshPlayers();
    }

    [RelayCommand]
    private async Task SendRaw()
    {
        if (string.IsNullOrWhiteSpace(CommandText)) return;
        await Run(RconStandardCommand.Raw, CommandText, ">>> " + CommandText);
        CommandText = "";
    }

    private async Task Run(RconStandardCommand cmd, string? payload, string label)
    {
        if (IsBusy) return;
        IsBusy = true; Status = label + "…";
        Append(label);
        try
        {
            var r = await _client.RconRunAsync(Server.Id, cmd, payload);
            if (r.Success)
            {
                Status = "OK";
                if (!string.IsNullOrWhiteSpace(r.Output)) Append("  " + r.Output.Trim());
            }
            else
            {
                Status = "Failed";
                Append("  ERROR: " + (r.Error ?? "?"));
            }
        }
        catch (Exception ex)
        {
            Status = "Exception"; Append("  EXCEPTION: " + ex.Message);
        }
        finally { IsBusy = false; }
    }
}
