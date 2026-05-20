using System.Collections.ObjectModel;
using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class BackupsWindow : Window
{
    private readonly AgentClient _client;
    private readonly ServerDef _server;
    private readonly ObservableCollection<BackupRow> _rows = new();

    public sealed class BackupRow
    {
        public string CheckpointName { get; init; } = "";
        public DateTimeOffset CreatedAt { get; init; }
        public string CreatedLocal => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
        public long? SizeBytes { get; init; }
        public string SizeText => SizeBytes is long b
            ? (b >= 1L << 30 ? $"{b / (double)(1L << 30):0.##} GB"
              : b >= 1L << 20 ? $"{b / (double)(1L << 20):0.##} MB"
              : b >= 1L << 10 ? $"{b / (double)(1L << 10):0.##} KB"
              : $"{b} B")
            : "—";
    }

    public BackupsWindow(AgentClient client, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _server = server;
        HeaderText.Text = $"Backups — {server.Name}";
        BackupsList.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            var list = await _client.ListBackupsAsync(_server.Id);
            _rows.Clear();
            foreach (var b in list.OrderByDescending(x => x.CreatedAt))
                _rows.Add(new BackupRow
                {
                    CheckpointName = b.CheckpointName,
                    CreatedAt = b.CreatedAt,
                    SizeBytes = b.SizeBytes
                });
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = $"{_rows.Count} backup(s)";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        CreateBtn.IsEnabled = false;
        StatusText.Text = "Creating backup…";
        try
        {
            var r = await _client.ActionAsync(_server.Id, ServerActionKind.Backup);
            StatusText.Text = r.Success ? "Backup created." : "Backup FAILED: " + r.Message;
            if (r.Success) await LoadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Backup failed: " + ex.Message; }
        finally { CreateBtn.IsEnabled = true; }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string name) return;
        var ok = MessageBox.Show(
            $"Restore '{name}'?\n\n" +
            "This will:\n" +
            "  1. Verify the server is stopped (you'll need to stop it manually first)\n" +
            "  2. Create a 'pre-restore' safety backup of current saves\n" +
            "  3. Replace the save folders with the contents of the chosen backup\n\n" +
            "Continue?",
            "Restore backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;

        StatusText.Text = "Restoring…";
        try
        {
            var r = await _client.RestoreBackupAsync(_server.Id, name);
            StatusText.Text = r.Success ? "Restore complete." : "Restore FAILED: " + r.Message;
            MessageBox.Show(r.Message, r.Success ? "Restore complete" : "Restore failed",
                MessageBoxButton.OK, r.Success ? MessageBoxImage.Information : MessageBoxImage.Error);
            await LoadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Restore failed: " + ex.Message; }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string name) return;
        var ok = MessageBox.Show($"Permanently delete backup '{name}'?",
            "Delete backup", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            var ok2 = await _client.DeleteBackupAsync(_server.Id, name);
            StatusText.Text = ok2 ? "Deleted." : "Delete failed.";
            await LoadAsync();
        }
        catch (Exception ex) { StatusText.Text = "Delete failed: " + ex.Message; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
