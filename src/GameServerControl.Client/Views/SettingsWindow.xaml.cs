using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;

namespace GameServerControl.Client.Views;

public partial class SettingsWindow : Window
{
    public AppSettings Result { get; private set; }

    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        Result = current;
        UrlBox.Text = current.AgentUrl;
        TokenBox.Text = current.ApiToken;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result = new AppSettings { AgentUrl = UrlBox.Text.Trim(), ApiToken = TokenBox.Text.Trim() };
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
