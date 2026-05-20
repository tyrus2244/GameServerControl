using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    public ObservableCollection<SearchRowVm> SearchRows { get; } = new();

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
        SearchList.ItemsSource = SearchRows;
        Loaded += async (_, _) =>
        {
            await ReloadAsync();
            // Pre-load popular mods so the Browse tab isn't empty on first open
            await SearchAsync("");
        };
    }

    // ---- installed tab ----

    private async Task ReloadAsync()
    {
        StatusText.Text = "Loading installed mods…";
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
            StatusText.Text = $"{resp.Mods.Length} mod(s) installed. Restart the server for changes to take effect.";
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
        if (string.IsNullOrEmpty(url)) { StatusText.Text = "Enter a URL first."; return; }
        await InstallFromUrlAsync(url, displayName: null);
        UrlBox.Clear();
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
            if (ok) { _toast($"Uninstalled '{modId}'."); await ReloadAsync(); }
            else { StatusText.Text = "Uninstall failed."; }
        }
        catch (Exception ex) { StatusText.Text = $"Uninstall failed: {ex.Message}"; }
    }

    // ---- browse tab ----

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, new RoutedEventArgs());
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync(SearchBox.Text);

    private async Task SearchAsync(string query)
    {
        SearchRows.Clear();
        SearchEmptyText.Visibility = Visibility.Collapsed;
        StatusText.Text = string.IsNullOrEmpty(query) ? "Loading popular mods…" : $"Searching for \"{query}\"…";
        try
        {
            var resp = await _client.SearchModsAsync(_serverId, query, limit: 30);
            SearchSourceText.Text = resp.Source is null
                ? (resp.UnsupportedReason ?? "")
                : $"Source: {resp.Source} · {resp.Results.Length} result(s) shown (top by downloads)";
            if (!resp.Supported)
            {
                SearchEmptyText.Text = resp.UnsupportedReason ?? "Search not available for this server.";
                SearchEmptyText.Visibility = Visibility.Visible;
                StatusText.Text = "";
                return;
            }
            foreach (var r in resp.Results) SearchRows.Add(new SearchRowVm(r));
            SearchEmptyText.Visibility = resp.Results.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"{resp.Results.Length} mod(s) found. Click Install on any row to add it to this server.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search failed: {ex.Message}";
        }
    }

    private async void SearchInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not SearchRowVm row) return;
        StatusText.Text = $"Installing {row.Name}…";
        await InstallFromUrlAsync(row.DownloadUrl, displayName: row.Name);
    }

    private async Task InstallFromUrlAsync(string url, string? displayName)
    {
        try
        {
            var result = await _client.InstallModAsync(_serverId, url, displayName);
            if (result.Ok && result.Mod is not null)
            {
                _toast($"Installed mod '{result.Mod.DisplayName}'.");
                await ReloadAsync();
            }
            else
            {
                StatusText.Text = $"Install failed: {result.Error}";
            }
        }
        catch (Exception ex) { StatusText.Text = $"Install failed: {ex.Message}"; }
    }

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string url || string.IsNullOrEmpty(url)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { StatusText.Text = $"Couldn't open: {ex.Message}"; }
    }

    // ---- view models ----

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

    public sealed class SearchRowVm
    {
        public SearchRowVm(ModSearchResult r)
        {
            Name = r.Name;
            Owner = r.Owner;
            Version = r.Version;
            IconUrl = r.IconUrl;
            DownloadUrl = r.DownloadUrl;
            PackageUrl = r.PackageUrl;
            RatingScore = r.RatingScore;
            // Truncate description for the card
            var d = (r.Description ?? "").Replace("\n", " ").Trim();
            DescriptionShort = d.Length > 180 ? d[..180] + "…" : d;
            DownloadsLabel = r.Downloads switch
            {
                >= 1_000_000 => $"{r.Downloads / 1_000_000.0:0.#}M",
                >= 1_000     => $"{r.Downloads / 1_000.0:0.#}k",
                _            => r.Downloads.ToString()
            };
            CategoriesLabel = string.Join(", ", r.Categories.Take(3));
        }
        public string Name { get; }
        public string Owner { get; }
        public string Version { get; }
        public string? IconUrl { get; }
        public string DownloadUrl { get; }
        public string PackageUrl { get; }
        public string DescriptionShort { get; }
        public string DownloadsLabel { get; }
        public int RatingScore { get; }
        public string CategoriesLabel { get; }
    }
}
