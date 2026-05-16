using System.Windows;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.ViewModels;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class ServerEditorWindow : Window
{
    public ServerEditorViewModel ViewModel { get; }
    public ServerDef? Result { get; private set; }

    public ServerEditorWindow(ServerDef? existing = null)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        ViewModel = existing is null ? new ServerEditorViewModel() : new ServerEditorViewModel(existing);
        DataContext = ViewModel;
        if (existing is not null)
        {
            Title = $"Edit Server — {existing.Name}";
            HeaderText.Text = $"Edit “{existing.Name}”";
            IdBox.IsReadOnly = true;
            IdBox.Opacity = 0.6;
        }
        else
        {
            Title = "Add Server";
            HeaderText.Text = "Add Server";
        }
    }

    /// <summary>Pre-filled from a discovery match. User can still tweak before saving.</summary>
    public ServerEditorWindow(DiscoveredServer discovered)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        ViewModel = new ServerEditorViewModel(discovered);
        DataContext = ViewModel;
        Title = $"Add Server — {discovered.DisplayName} (auto-detected)";
        HeaderText.Text = $"Add {discovered.DisplayName}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.TryBuild(out var def))
        {
            Result = def;
            DialogResult = true;
            Close();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
