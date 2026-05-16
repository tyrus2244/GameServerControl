using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class DiscoverServersWindow : Window
{
    private readonly AgentClient _client;
    private readonly Action<string> _toast;
    private readonly Func<Task> _afterAdd;
    public ObservableCollection<DiscoveredServer> Results { get; } = new();
    public ObservableCollection<string> Libraries { get; } = new();

    public DiscoverServersWindow(AgentClient client, Action<string> toast, Func<Task> afterAdd)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _toast = toast;
        _afterAdd = afterAdd;
        ResultList.ItemsSource = Results;
        LibrariesList.ItemsSource = Libraries;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "Scanning…";
        Results.Clear();
        Libraries.Clear();
        try
        {
            var resp = await _client.DiscoverServersAsync();
            foreach (var s in resp.Servers) Results.Add(s);
            foreach (var l in resp.LibrariesScanned) Libraries.Add(l);
            StatusText.Text = resp.Servers.Length == 0
                ? "No installed dedicated servers were found on this host."
                : $"Found {resp.Servers.Length} installed dedicated server(s).";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Discovery failed: {ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not DiscoveredServer discovered) return;

        // Open the editor pre-filled from the discovery. User can tweak ID / name / args
        // (so two installs of the same game can coexist without ID collision).
        var editor = new ServerEditorWindow(discovered) { Owner = this };
        if (editor.ShowDialog() != true || editor.Result is null) return;

        try
        {
            await _client.CreateServerAsync(editor.Result);
            _toast($"Added '{editor.Result.Name}' from auto-discovery.");
            await _afterAdd();
            // Refresh discovery so the just-added one flips to "already added"
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            _toast($"Add failed: {ex.Message}");
        }
    }
}
