using System.Collections.ObjectModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameServerControl.Client.Helpers;
using GameServerControl.Client.Services;
using GameServerControl.Client.ViewModels;
using GameServerControl.Shared;

namespace GameServerControl.Client.Views;

public partial class CreateServerWindow : Window
{
    private readonly AgentClient _client;
    private readonly StatusHubClient _hub;
    private readonly Action _onCreated;
    private readonly ObservableCollection<InstallableGame> _games = new();
    private InstallableGame? _selected;
    private string? _activeJobId;

    /// <summary>
    /// One game on the wizard's step-1 picker. We mirror the relevant pieces of GamePreset here so the
    /// wizard owns the "what's installable via SteamCMD + how big is it" knowledge — GamePresets is a
    /// pure-data record optimized for the "manual add" form path.
    /// </summary>
    public sealed record InstallableGame(
        string Key,
        string Label,
        string Icon,
        string SteamAppId,
        string EstimatedSize,    // human-readable; from steamdb.info typical sizes
        string DefaultDirName,   // e.g. "Valheim"  → install path becomes "C:\GameServers\Valheim"
        GamePreset Preset);

    public CreateServerWindow(AgentClient client, StatusHubClient hub, Action onCreated)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _client = client;
        _hub = hub;
        _onCreated = onCreated;

        // Curated list of "we know SteamCMD can install this anonymously" games. Keep order roughly
        // by popularity. Sizes are typical — actual download depends on game updates.
        _games.Add(new("valheim",      "Valheim",                 "⚔",  "896660",  "~1 GB",   "Valheim",      GamePresets.FindByKey("valheim")!));
        _games.Add(new("palworld",     "Palworld",                "🐾", "2394010", "~7 GB",   "PalServer",    GamePresets.FindByKey("palworld")!));
        _games.Add(new("satisfactory", "Satisfactory",            "⚙",  "1690800", "~12 GB",  "Satisfactory", GamePresets.FindByKey("satisfactory")!));
        _games.Add(new("ark-asa",      "ARK: Survival Ascended",  "🦖", "2430930", "~50 GB",  "ArkAscended",  GamePresets.FindByKey("ark-asa")!));
        _games.Add(new("ark-se",       "ARK: Survival Evolved",   "🦖", "376030",  "~14 GB",  "ArkSE",        GamePresets.FindByKey("ark-se")!));
        _games.Add(new("rust",         "Rust",                    "🔧", "258550",  "~8 GB",   "Rust",         GamePresets.FindByKey("rust")!));
        _games.Add(new("7dtd",         "7 Days to Die",           "🧟", "294420",  "~2 GB",   "SevenDaysToDie", GamePresets.FindByKey("7dtd")!));
        _games.Add(new("terraria",     "Terraria",                "🌳", "105600",  "~0.5 GB", "Terraria",     GamePresets.FindByKey("terraria")!));
        _games.Add(new("dst",          "Don't Starve Together",   "❄",  "343050",  "~1 GB",   "DST",          GamePresets.FindByKey("dst")!));
        _games.Add(new("zomboid",      "Project Zomboid",         "🧟", "380870",  "~1 GB",   "ProjectZomboid", GamePresets.FindByKey("zomboid")!));
        GameList.ItemsSource = _games;

        // Default install root — C:\GameServers on Windows, ~/gameservers on Linux/macOS.
        ShowStep(1);

        _hub.InstallProgress += OnInstallProgress;
        Closed += (_, _) => _hub.InstallProgress -= OnInstallProgress;
    }

    // ===== Step 1: game picker =====

    private void GameRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string key) return;
        _selected = _games.FirstOrDefault(g => g.Key == key);
        if (_selected is null) return;

        // Pre-fill step 2 from the selected game.
        DisplayNameBox.Text = _selected.Label;
        IdBox.Text          = SlugifyName(_selected.Label);
        InstallPathBox.Text = DefaultInstallRoot() + Path.DirectorySeparatorChar + _selected.DefaultDirName;
        Step2GameHeader.Text = $"{_selected.Icon}  {_selected.Label}  ·  Steam App ID {_selected.SteamAppId}";
        UpdatePreview();

        // Wire change tracking now that the boxes are populated.
        DisplayNameBox.TextChanged -= OnFieldChanged;
        IdBox.TextChanged          -= OnFieldChanged;
        InstallPathBox.TextChanged -= OnFieldChanged;
        DisplayNameBox.TextChanged += OnFieldChanged;
        IdBox.TextChanged          += OnFieldChanged;
        InstallPathBox.TextChanged += OnFieldChanged;

        ShowStep(2);
    }

    private void OnFieldChanged(object? sender, EventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (_selected is null) return;
        var path = (InstallPathBox.Text ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar);
        var exeRel = _selected.Preset.DefaultExeRelative;
        var exeAbs = string.IsNullOrEmpty(path) ? exeRel : Path.Combine(path, exeRel);
        PreviewExe.Text  = "EXE: " + exeAbs;
        PreviewArgs.Text = "ARGS: " + (_selected.Preset.DefaultStartArgs.Length == 0 ? "(none)" : string.Join(" ", _selected.Preset.DefaultStartArgs));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        // .NET 8 added a proper WPF folder picker; no need for WinForms or Ookii.Dialogs.
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where to install the dedicated server",
            InitialDirectory = (InstallPathBox.Text ?? "").Trim()
        };
        if (dlg.ShowDialog() == true)
        {
            InstallPathBox.Text = dlg.FolderName;
            UpdatePreview();
        }
    }

    // ===== Step 3: install =====

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        Step2Error.Visibility = Visibility.Collapsed;
        if (_selected is null) { Show2Err("Pick a game first."); return; }

        var name = (DisplayNameBox.Text ?? "").Trim();
        var id   = (IdBox.Text ?? "").Trim();
        var path = (InstallPathBox.Text ?? "").Trim().TrimEnd(Path.DirectorySeparatorChar);
        if (name.Length == 0) { Show2Err("Display name is required."); return; }
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]{0,63}$")) { Show2Err("ID must be lowercase letters, digits, and hyphens."); return; }
        if (path.Length == 0) { Show2Err("Install location is required."); return; }

        // Compose the ServerDef from the preset, overriding paths to point at the chosen install location.
        var exeAbs = Path.Combine(path, _selected.Preset.DefaultExeRelative);
        // Rewrite save dirs from the preset's default location to the actual install location.
        var presetRoot = _selected.Preset.DefaultWorkingDir.TrimEnd('\\', '/');
        var rewrittenSaves = _selected.Preset.DefaultSaveDirs
            .Select(s => s.StartsWith(presetRoot, StringComparison.OrdinalIgnoreCase)
                ? path + s.Substring(presetRoot.Length)
                : s)
            .ToArray();

        var def = new ServerDef(
            Id: id,
            Name: name,
            VmName: "",
            GameType: _selected.Preset.GameType,
            GuestExePath: exeAbs,
            GuestWorkingDir: path,
            StartArgs: _selected.Preset.DefaultStartArgs,
            SaveDirs: rewrittenSaves,
            SteamAppId: _selected.SteamAppId,
            GuestCredentialId: "default",
            LogPathInGuest: null,
            RconHost: "auto",
            RconPort: null,
            RconPassword: null,
            HostingMode: HostingMode.BareMetal,
            ScheduledTaskName: null,
            StopProcessNames: null,
            DiscordWebhookUrl: null);

        ShowStep(3);
        Step3Header.Text = $"Installing {_selected.Label}…";
        AppendLog("$ steamcmd +force_install_dir \"" + path + "\" +login anonymous +app_update " + _selected.SteamAppId + " validate +quit");

        try
        {
            var ack = await _client.InstallServerAsync(new InstallServerRequest(_selected.SteamAppId, path, def));
            _activeJobId = ack.JobId;
            Step3Status.Text = $"Job: {ack.JobId} (live updates from agent)";
        }
        catch (Exception ex)
        {
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 0;
            AppendLog("ERROR: " + ex.Message);
            Step3Status.Text = "Install request failed — fix above and try again.";
            // Let the user back out and retry.
            BackBtn.Visibility = Visibility.Visible;
        }
    }

    private void OnInstallProgress(InstallProgress p)
    {
        if (_activeJobId is null || p.JobId != _activeJobId) return;
        Application.Current.Dispatcher.Invoke(() =>
        {
            AppendLog(p.Line);
            if (p.PercentHint is int pct)
            {
                InstallProgressBar.IsIndeterminate = false;
                InstallProgressBar.Value = pct;
                InstallProgressBar.Maximum = 100;
            }
            Step3Status.Text = p.Phase switch
            {
                "queued"   => "Queued…",
                "steamcmd" => p.PercentHint is int v ? $"SteamCMD · {v}%" : "SteamCMD running…",
                "register" => "Registering with dashboard…",
                "done"     => "✅ Done — server is ready.",
                "failed"   => "❌ Failed — see log above.",
                _          => p.Phase
            };
            if (p.Finished)
            {
                InstallProgressBar.IsIndeterminate = false;
                if (p.Success)
                {
                    InstallProgressBar.Value = 100;
                    Step3Header.Text = $"✅ {_selected?.Label} installed.";
                    DoneBtn.Visibility = Visibility.Visible;
                    _onCreated?.Invoke();
                }
                else
                {
                    Step3Header.Text = $"❌ Install failed.";
                    BackBtn.Visibility = Visibility.Visible;
                }
            }
        });
    }

    // ===== Navigation =====

    private void ShowStep(int step)
    {
        Step1Panel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2Panel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3Panel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        // Pill highlighting — current = accent fill, future = dim.
        Step1Pill.Background = step >= 1 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        Step2Pill.Background = step >= 2 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        Step3Pill.Background = step >= 3 ? (Brush)FindResource("AccentBrush") : (Brush)FindResource("BgBrush3");
        // Footer buttons by step.
        BackBtn.Visibility    = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        NextBtn.Visibility    = Visibility.Collapsed;
        InstallBtn.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        DoneBtn.Visibility    = Visibility.Collapsed;
        StepSubtitle.Text = step switch
        {
            1 => "Pick a game, choose where to install it, and we'll do the rest.",
            2 => "Name your server and pick where it lands on disk.",
            _ => "SteamCMD is doing the heavy lifting — this can take a few minutes."
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Step3Panel.Visibility == Visibility.Visible) ShowStep(2);
        else if (Step2Panel.Visibility == Visibility.Visible) ShowStep(1);
    }

    private void Next_Click(object sender, RoutedEventArgs e) { /* unused; clicking a game advances */ }

    private void Done_Click(object sender, RoutedEventArgs e) => Close();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ===== Helpers =====

    private void Show2Err(string msg) { Step2Error.Text = msg; Step2Error.Visibility = Visibility.Visible; }

    private void AppendLog(string line)
    {
        // Cap log to ~1000 lines so the window doesn't bloat memory on multi-hour ARK installs.
        var current = InstallLog.Text;
        var lines = current.Split('\n');
        if (lines.Length > 1000) current = string.Join('\n', lines.Skip(lines.Length - 800));
        InstallLog.Text = current + line + "\n";
        LogScroll.ScrollToEnd();
    }

    private static string SlugifyName(string s)
    {
        var lower = s.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "" : slug;
    }

    private static string DefaultInstallRoot() =>
        OperatingSystem.IsWindows()
            ? @"C:\GameServers"
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "gameservers");
}
