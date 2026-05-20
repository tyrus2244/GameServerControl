using System.Collections.ObjectModel;
using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;

namespace GameServerControl.Client.Views;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }
    private readonly ObservableCollection<AgentConfig> _agents = new();

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Result = current;
        // Clone so cancel doesn't mutate the live settings.
        foreach (var a in current.Agents)
            _agents.Add(new AgentConfig { Id = a.Id, Nickname = a.Nickname, Url = a.Url, Token = a.Token });
        if (_agents.Count == 0)
            _agents.Add(NewBlankAgent());
        AgentsList.ItemsSource = _agents;
    }

    private static AgentConfig NewBlankAgent() => new()
    {
        Id = Guid.NewGuid().ToString("N")[..8],
        Nickname = "New agent",
        Url = "http://127.0.0.1:5099",
        Token = ""
    };

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        _agents.Add(NewBlankAgent());
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string id) return;
        var match = _agents.FirstOrDefault(a => a.Id == id);
        if (match is not null) _agents.Remove(match);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var settings = new AppSettings { Agents = _agents.ToList() };
        // Keep legacy fields in sync — back-compat for any code that still reads AgentUrl/ApiToken.
        if (settings.Agents.Count > 0)
        {
            settings.AgentUrl = settings.Agents[0].Url;
            settings.ApiToken = settings.Agents[0].Token;
        }
        Result = settings;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
