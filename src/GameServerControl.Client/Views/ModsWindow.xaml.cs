using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ModsWindow : Window
{
    private readonly AgentClient _client;
    private readonly string _serverId;
    private readonly Action<string> _toast;
    public ObservableCollection<ModRowVm> Rows { get; } = new();

    public ModsWindow(AgentClient client, string serverId, string serverDisplayName, Action<string> toast)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _serverId = serverId;
        _toast = toast;
        Title = $"Mods — {serverDisplayName}";
        HeaderText.Text = $"Server-side mods · {serverDisplayName}";
        ModList.ItemsSource = Rows;
        Loaded += async (_, _) => await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        StatusText.Text = "Loading…";
        Rows.Clear();
        try
        {
            var resp = await _client.ListModsAsync(_serverId);
            ModsFolderText.Text = resp.ModsFolder is null ? "" : $"Mod folder: {resp.ModsFolder}";
            if (!resp.Supported)
            {
                StatusText.Text = $"Mod management not available for this server. {resp.UnsupportedReason ?? ""}";
                EmptyText.Visibility = Visibility.Collapsed;
                return;
            }
            foreach (var m in resp.Mods) Rows.Add(new ModRowVm(m));
            StatusText.Text = $"{resp.Mods.Length} mod(s) installed. Server restart required for changes to take effect.";
            EmptyText.Visibility = resp.Mods.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"List failed: {ex.Message}";
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (string.IsNullOrEmpty(url))
        {
            StatusText.Text = "Enter a URL first.";
            return;
        }
        StatusText.Text = "Downloading + installing…";
        try
        {
            var result = await _client.InstallModAsync(_serverId, url, displayName: null);
            if (result.Ok && result.Mod is not null)
            {
                _toast($"Installed mod '{result.Mod.DisplayName}'.");
                UrlBox.Clear();
                await ReloadAsync();
            }
            else
            {
                StatusText.Text = $"Install failed: {result.Error}";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Install failed: {ex.Message}";
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string modId) return;
        var confirm = MessageBox.Show(this, $"Uninstall '{modId}'?\n\nFiles will be deleted from disk.", "Uninstall mod",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var ok = await _client.UninstallModAsync(_serverId, modId);
            if (ok)
            {
                _toast($"Uninstalled '{modId}'.");
                await ReloadAsync();
            }
            else
            {
                StatusText.Text = "Uninstall failed.";
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Uninstall failed: {ex.Message}";
        }
    }

    public sealed class ModRowVm
    {
        public ModRowVm(ModInfo info)
        {
            ModId = info.ModId;
            DisplayName = info.DisplayName;
            Version = info.Version;
            SizeKb = info.SizeBytes / 1024;
            InstalledAtLabel = info.InstalledAt is { } at ? at.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "(unknown)";
        }
        public string ModId { get; }
        public string DisplayName { get; }
        public string? Version { get; }
        public long SizeKb { get; }
        public string InstalledAtLabel { get; }
    }
}
