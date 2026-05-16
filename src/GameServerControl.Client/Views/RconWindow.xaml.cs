using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Client.ViewModels;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class RconWindow : Window
{
    public RconWindow(AgentClient client, ServerDef server)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        DataContext = new RconViewModel(client, server);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
