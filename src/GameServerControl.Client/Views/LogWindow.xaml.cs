using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class LogWindow : Window
{
    private readonly AgentClient _client;
    private readonly StatusHubClient _hub;
    private readonly ServerDef _server;
    public ObservableCollection<string> LinesView { get; } = new();

    public LogWindow(AgentClient client, StatusHubClient hub, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _hub = hub;
        _server = server;

        HeaderText.Text = $"Log — {server.Name}";
        PathText.Text = string.IsNullOrEmpty(server.LogPathInGuest) ? "(no LogPathInGuest configured)" : server.LogPathInGuest;
        Lines.ItemsSource = LinesView;

        _hub.LogLine += OnLogLine;
        Loaded += async (_, _) => await _client.StartLogTailAsync(server.Id);
        Closed += async (_, _) =>
        {
            _hub.LogLine -= OnLogLine;
            try { await _client.StopLogTailAsync(server.Id); } catch { /* ignore */ }
        };
    }

    private void OnLogLine(LogLine line)
    {
        if (!string.Equals(line.ServerId, _server.Id, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(line.Source, "log", StringComparison.OrdinalIgnoreCase)) return;
        Dispatcher.Invoke(() =>
        {
            LinesView.Add(line.Text);
            while (LinesView.Count > 5000) LinesView.RemoveAt(0);
            if (AutoScrollCheck.IsChecked == true && LinesView.Count > 0)
                Lines.ScrollIntoView(LinesView[^1]);
        });
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => LinesView.Clear();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
