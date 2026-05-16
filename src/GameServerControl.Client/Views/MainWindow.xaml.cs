using System.Windows;
using System.Windows.Controls;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.ViewModels;

namespace GameServerControl.Client.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
    }

    private async void ToggleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ServerViewModel vm)
        {
            var desired = cb.IsChecked == true;
            cb.IsChecked = vm.IsOn;
            await vm.ToggleAsync(desired);
        }
    }

    private async void AutostartClicked(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is ServerViewModel vm)
        {
            var desired = cb.IsChecked == true;
            cb.IsChecked = vm.IsAutostartEnabled;
            await vm.SetAutostartAsync(desired);
        }
    }

    private void CopyInvite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string code && !string.IsNullOrEmpty(code))
        {
            try
            {
                Clipboard.SetText(code);
                btn.Content = "✓";
                _ = System.Threading.Tasks.Task.Delay(1200).ContinueWith(_ =>
                    Dispatcher.Invoke(() => btn.Content = "📋"));
            }
            catch { /* clipboard can be flaky */ }
        }
    }
}
