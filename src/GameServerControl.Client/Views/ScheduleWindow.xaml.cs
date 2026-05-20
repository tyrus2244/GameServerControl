using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ScheduleWindow : Window
{
    private readonly AgentClient _client;
    private readonly ServerDef _server;

    public ScheduleWindow(AgentClient client, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _server = server;
        HeaderText.Text = $"Schedule — {server.Name}";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        StatusText.Text = "Loading…";
        try
        {
            var s = await _client.GetScheduleAsync(_server.Id);
            if (s is null)
            {
                StatusText.Text = "No schedule configured.";
                return;
            }
            DailyEnabled.IsChecked  = s.DailyRestartEnabled;
            DailyHour.Text          = s.DailyRestartHour.ToString();
            WeeklyEnabled.IsChecked = s.WeeklyUpdateEnabled;
            WeeklyDay.SelectedIndex = (int)s.WeeklyUpdateDay;
            WeeklyHour.Text         = s.WeeklyUpdateHour.ToString();
            HourlyEnabled.IsChecked = s.HourlyBackupEnabled;
            HourlyMinute.Text       = s.HourlyBackupMinute.ToString();
            StatusText.Text = "Loaded current schedule.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Load failed: " + ex.Message;
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DailyHour.Text,    out var dh) || dh < 0 || dh > 23) { Warn("Daily hour must be 0-23."); return; }
        if (!int.TryParse(WeeklyHour.Text,   out var wh) || wh < 0 || wh > 23) { Warn("Weekly hour must be 0-23."); return; }
        if (!int.TryParse(HourlyMinute.Text, out var hm) || hm < 0 || hm > 59) { Warn("Hourly minute must be 0-59."); return; }
        var sched = new MaintenanceSchedule(
            DailyRestartEnabled:   DailyEnabled.IsChecked == true,
            DailyRestartHour:      dh,
            WeeklyUpdateEnabled:   WeeklyEnabled.IsChecked == true,
            WeeklyUpdateDay:       (DayOfWeek)WeeklyDay.SelectedIndex,
            WeeklyUpdateHour:      wh,
            HourlyBackupEnabled:   HourlyEnabled.IsChecked == true,
            HourlyBackupMinute:    hm);
        StatusText.Text = "Saving…";
        try
        {
            await _client.SetScheduleAsync(_server.Id, sched);
            StatusText.Text = "Saved. Windows Task Scheduler updated.";
        }
        catch (Exception ex) { StatusText.Text = "Save failed: " + ex.Message; }
    }

    private async void DisableAll_Click(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show("Remove all scheduled tasks for this server?",
            "Disable schedule", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.OK) return;
        try
        {
            await _client.ClearScheduleAsync(_server.Id);
            DailyEnabled.IsChecked = false;
            WeeklyEnabled.IsChecked = false;
            HourlyEnabled.IsChecked = false;
            StatusText.Text = "All scheduled tasks removed.";
        }
        catch (Exception ex) { StatusText.Text = "Disable failed: " + ex.Message; }
    }

    private void Warn(string msg) =>
        MessageBox.Show(msg, "Invalid value", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
