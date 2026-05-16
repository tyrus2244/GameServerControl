using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Client.ViewModels;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ServerConfigWindow : Window
{
    private readonly AgentClient _client;
    private readonly ConfigEditorViewModel _vm;
    public bool RestartRequested { get; private set; }

    public ServerConfigWindow(AgentClient client, ServerDef server, ConfigSchema? schema, IDictionary<string, string> initial)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _vm = new ConfigEditorViewModel(server, schema, initial);
        DataContext = _vm;
        if (!_vm.HasSchema)
        {
            _vm.StatusMessage = "No config schema is defined for this game yet. Use Edit on the card to change launch args.";
            _vm.IsError = false;
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.HasSchema) { Close(); return; }
        try
        {
            _vm.IsError = false;
            _vm.StatusMessage = "Applying…";
            var values = _vm.CollectValues();
            await _client.PutServerConfigAsync(_vm.Server.Id, values);
            _vm.StatusMessage = "Saved. Restart the server to pick up changes.";
            var ans = MessageBox.Show(
                "Config saved. Restart the server now so changes take effect?",
                "Restart server",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ans == MessageBoxResult.Yes) RestartRequested = true;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _vm.IsError = true;
            _vm.StatusMessage = "Failed: " + ex.Message;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
