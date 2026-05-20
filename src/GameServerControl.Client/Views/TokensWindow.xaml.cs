using System.Collections.ObjectModel;
using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class TokensWindow : Window
{
    private readonly AgentClient _client;
    private readonly ObservableCollection<TokenRow> _rows = new();

    public sealed class TokenRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string Token { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedLocal => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    }

    public TokensWindow(AgentClient client)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        TokensList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            var list = await _client.ListTokensAsync();
            _rows.Clear();
            foreach (var t in list.OrderBy(t => t.CreatedAt))
                _rows.Add(new TokenRow
                {
                    Id = t.Id, Name = t.Name, Role = t.Role.ToString(),
                    Token = t.Token, CreatedAt = t.CreatedAt
                });
            StatusText.Text = $"{_rows.Count} token(s).";
        }
        catch (Exception ex) { StatusText.Text = "Load failed: " + ex.Message; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var id = IdBox.Text.Trim();
        var name = NameBox.Text.Trim();
        var role = (RoleCombo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() switch
        {
            "ReadOnly" => TokenRole.ReadOnly,
            _ => TokenRole.Admin
        };
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            MessageBox.Show("ID and name are required.", "Create token", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            var rec = await _client.CreateTokenAsync(id, name, role);
            // SHOW the token to the user ONCE, then never display again.
            var fullToken = rec.Token;
            MessageBox.Show(
                "Save this token NOW — it cannot be retrieved later.\n\n" + fullToken,
                "New token created", MessageBoxButton.OK, MessageBoxImage.Information);
            try { System.Windows.Clipboard.SetText(fullToken); StatusText.Text = "Token copied to clipboard."; }
            catch { /* tolerate */ }
            IdBox.Text = ""; NameBox.Text = "";
            await LoadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Create failed: " + ex.Message; }
    }

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var ok = MessageBox.Show($"Revoke token '{id}'?\n\nAnyone currently using it will be locked out immediately.",
            "Revoke token", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            var done = await _client.DeleteTokenAsync(id);
            StatusText.Text = done ? "Revoked." : "Token not found.";
            await LoadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Revoke failed: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
