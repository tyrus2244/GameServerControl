using System.IO;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GameServerControl.Shared;

namespace GameServerControl.Client.ViewModels;

public sealed partial class ServerEditorViewModel : ObservableObject
{
    private static readonly Regex IdPattern = new("^[a-z0-9][a-z0-9-]{0,63}$");

    public bool IsEdit { get; }

    [ObservableProperty] private string id = "";
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string vmName = "";
    [ObservableProperty] private HostingMode hostingMode = HostingMode.BareMetal;
    [ObservableProperty] private GameType gameType = GameType.Custom;
    [ObservableProperty] private string guestExePath = "";
    [ObservableProperty] private string guestWorkingDir = "";
    [ObservableProperty] private string startArgsText = "";
    [ObservableProperty] private string saveDirsText = "";
    [ObservableProperty] private string steamAppId = "";
    [ObservableProperty] private string guestCredentialId = "default";
    [ObservableProperty] private string logPathInGuest = "";
    [ObservableProperty] private string rconHost = "auto";
    [ObservableProperty] private string rconPort = "";
    [ObservableProperty] private string rconPassword = "";
    [ObservableProperty] private string scheduledTaskName = "";
    [ObservableProperty] private string stopProcessNamesText = "";
    [ObservableProperty] private string? selectedPresetKey;
    [ObservableProperty] private string? validationError;

    public GamePreset[] Presets { get; } = GamePresets.All;
    public Array AvailableGameTypes { get; } = Enum.GetValues(typeof(GameType));
    public Array AvailableHostingModes { get; } = Enum.GetValues(typeof(HostingMode));

    public ServerEditorViewModel()
    {
        IsEdit = false;
        SelectedPresetKey = "custom";
    }

    /// <summary>
    /// Pre-fill from a discovery match. Applies the matching preset for defaults,
    /// then overrides exe/workingdir with the actual discovered install paths and
    /// rewrites preset-relative save dirs to point at the real install location.
    /// </summary>
    public ServerEditorViewModel(DiscoveredServer discovered)
    {
        IsEdit = false;
        // Setting SelectedPresetKey fires OnSelectedPresetKeyChanged which fills the form
        // with preset defaults (exe relative path, default working dir, default args, save dirs).
        SelectedPresetKey = discovered.PresetKey;

        // Now override with real discovered paths.
        var preset = GamePresets.FindByKey(discovered.PresetKey);
        GuestExePath = discovered.ExePath;
        GuestWorkingDir = discovered.InstallPath;
        SteamAppId = discovered.SteamAppId;

        // Rewrite save dirs from the preset's hypothetical default install location to the real one.
        // e.g. preset default "C:\PalServer\Pal\Saved" → "C:\Program Files (x86)\Steam\steamapps\common\PalServer\Pal\Saved"
        if (preset is not null && !string.IsNullOrEmpty(preset.DefaultWorkingDir))
        {
            var fromPrefix = preset.DefaultWorkingDir.TrimEnd('\\', '/');
            var toPrefix = discovered.InstallPath.TrimEnd('\\', '/');
            if (!string.Equals(fromPrefix, toPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var lines = SaveDirsText.Split('\n')
                    .Select(line => line.StartsWith(fromPrefix, StringComparison.OrdinalIgnoreCase)
                        ? toPrefix + line.Substring(fromPrefix.Length)
                        : line);
                SaveDirsText = string.Join("\n", lines);
            }
        }

        // Pre-fill name & id from the discovery
        if (string.IsNullOrWhiteSpace(Name)) Name = discovered.DisplayName;
        Id = SlugifyName(discovered.DisplayName);
    }

    public ServerEditorViewModel(ServerDef existing)
    {
        IsEdit = true;
        Id = existing.Id;
        Name = existing.Name;
        VmName = existing.VmName;
        HostingMode = existing.HostingMode;
        GameType = existing.GameType;
        GuestExePath = existing.GuestExePath;
        GuestWorkingDir = existing.GuestWorkingDir;
        StartArgsText = string.Join("\n", existing.StartArgs ?? Array.Empty<string>());
        SaveDirsText = string.Join("\n", existing.SaveDirs ?? Array.Empty<string>());
        SteamAppId = existing.SteamAppId ?? "";
        GuestCredentialId = existing.GuestCredentialId ?? "default";
        LogPathInGuest = existing.LogPathInGuest ?? "";
        RconHost = existing.RconHost ?? "auto";
        RconPort = existing.RconPort?.ToString() ?? "";
        RconPassword = existing.RconPassword ?? "";
        ScheduledTaskName = existing.ScheduledTaskName ?? "";
        StopProcessNamesText = string.Join("\n", existing.StopProcessNames ?? Array.Empty<string>());
        SelectedPresetKey = null;
    }

    partial void OnSelectedPresetKeyChanged(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        var preset = GamePresets.FindByKey(value);
        if (preset is null || preset.Key == "custom") return;

        // Only overwrite fields the user hasn't customized in a way we'd clobber.
        // Heuristic: if a field is empty OR matches a previous preset's value, replace it.
        GameType = preset.GameType;
        if (string.IsNullOrWhiteSpace(SteamAppId) || LooksLikeAppId(SteamAppId))
            SteamAppId = preset.SteamAppId ?? "";

        if (string.IsNullOrWhiteSpace(GuestWorkingDir) || GuestWorkingDir.StartsWith(@"C:\", StringComparison.OrdinalIgnoreCase))
            GuestWorkingDir = preset.DefaultWorkingDir;

        if (string.IsNullOrWhiteSpace(GuestExePath))
            GuestExePath = Path.IsPathRooted(preset.DefaultExeRelative)
                ? preset.DefaultExeRelative
                : (string.IsNullOrEmpty(preset.DefaultWorkingDir)
                    ? preset.DefaultExeRelative
                    : Path.Combine(preset.DefaultWorkingDir, preset.DefaultExeRelative));

        StartArgsText = string.Join("\n", preset.DefaultStartArgs);
        SaveDirsText = string.Join("\n", preset.DefaultSaveDirs);

        if (string.IsNullOrWhiteSpace(Name))
            Name = preset.Label;

        if (!IsEdit && string.IsNullOrWhiteSpace(Id))
            Id = preset.Key;
    }

    private static bool LooksLikeAppId(string s) => s.Length > 0 && s.All(char.IsDigit);

    partial void OnNameChanged(string value)
    {
        if (!IsEdit && string.IsNullOrWhiteSpace(Id))
            Id = SlugifyName(value);
    }

    private static string SlugifyName(string s)
    {
        var lower = s.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lower, @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrEmpty(slug) ? "" : slug;
    }

    public bool TryBuild(out ServerDef def)
    {
        ValidationError = null;
        if (!IdPattern.IsMatch(Id))
        {
            ValidationError = "ID must be lowercase letters, digits, and hyphens.";
            def = default!;
            return false;
        }
        if (string.IsNullOrWhiteSpace(Name)) { ValidationError = "Name is required."; def = default!; return false; }
        if (HostingMode == HostingMode.Vm && string.IsNullOrWhiteSpace(VmName))
        {
            ValidationError = "VM name is required when hosting mode is VM.";
            def = default!;
            return false;
        }
        if (string.IsNullOrWhiteSpace(GuestExePath)) { ValidationError = "Server EXE path is required."; def = default!; return false; }

        int? rconPortVal = null;
        if (!string.IsNullOrWhiteSpace(RconPort))
        {
            if (!int.TryParse(RconPort.Trim(), out var p) || p <= 0 || p > 65535)
            {
                ValidationError = "RCON port must be 1-65535 or empty.";
                def = default!;
                return false;
            }
            rconPortVal = p;
        }

        def = new ServerDef(
            Id: Id.Trim(),
            Name: Name.Trim(),
            VmName: VmName?.Trim() ?? "",
            GameType: GameType,
            GuestExePath: GuestExePath.Trim(),
            GuestWorkingDir: string.IsNullOrWhiteSpace(GuestWorkingDir)
                ? Path.GetDirectoryName(GuestExePath.Trim()) ?? ""
                : GuestWorkingDir.Trim(),
            StartArgs: SplitLines(StartArgsText),
            SaveDirs: SplitLines(SaveDirsText),
            SteamAppId: string.IsNullOrWhiteSpace(SteamAppId) ? null : SteamAppId.Trim(),
            GuestCredentialId: string.IsNullOrWhiteSpace(GuestCredentialId) ? "default" : GuestCredentialId.Trim(),
            LogPathInGuest: string.IsNullOrWhiteSpace(LogPathInGuest) ? null : LogPathInGuest.Trim(),
            RconHost: string.IsNullOrWhiteSpace(RconHost) ? null : RconHost.Trim(),
            RconPort: rconPortVal,
            RconPassword: string.IsNullOrWhiteSpace(RconPassword) ? null : RconPassword,
            HostingMode: HostingMode,
            ScheduledTaskName: string.IsNullOrWhiteSpace(ScheduledTaskName) ? null : ScheduledTaskName.Trim(),
            StopProcessNames: SplitLines(StopProcessNamesText) is { Length: > 0 } sn ? sn : null);
        return true;
    }

    private static string[] SplitLines(string s)
        => string.IsNullOrWhiteSpace(s)
            ? Array.Empty<string>()
            : s.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
}
