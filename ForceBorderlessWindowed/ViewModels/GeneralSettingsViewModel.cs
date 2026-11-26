using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>The "General" tab.</summary>
public partial class GeneralSettingsViewModel : ObservableObject
{
    /// <param name="autostart">null while the autostart check is still running, see <see cref="SetAutostart"/>.</param>
    public GeneralSettingsViewModel(AppSettings settings, AutostartMode? autostart, bool canStartElevated)
    {
        SelectedLanguage = Languages.FirstOrDefault(l => l.CultureName == settings.Language) ?? Languages[0];
        SelectedTheme = Themes.FirstOrDefault(t => t.Theme == settings.Theme) ?? Themes[0];
        CanStartElevated = canStartElevated;
        if (autostart is { } mode)
        {
            SetAutostart(mode);
        }

        DefaultPlacement = new PlacementEditorViewModel(settings.DefaultPlacement, defaultEditor: null);
    }

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(null, Strings.Settings_Language_System),
        new("en", "English"),
        new("de", "Deutsch"),
    ];

    [ObservableProperty]
    public partial LanguageOption SelectedLanguage { get; set; }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.System, Strings.Settings_Theme_System),
        new(AppTheme.Light, Strings.Settings_Theme_Light),
        new(AppTheme.Dark, Strings.Settings_Theme_Dark),
    ];

    [ObservableProperty]
    public partial ThemeOption SelectedTheme { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Autostart), nameof(CanEditStartAsAdmin), nameof(StartAsAdminDescription))]
    public partial bool StartWithWindows { get; set; }

    /// <summary>Sub-option of <see cref="StartWithWindows"/>; kept when that is unchecked, so re-checking restores it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Autostart), nameof(CanEditStartAsAdmin))]
    public partial bool StartAsAdmin { get; set; }

    /// <summary>
    /// False until the current autostart registration is known (the task check runs in the background).
    /// The checkboxes stay disabled until then, so they can't show or save a wrong state.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditStartAsAdmin), nameof(StartWithWindowsDescription))]
    public partial bool IsAutostartKnown { get; private set; }

    /// <summary>See <see cref="IAutostartService.CanStartElevated"/>.</summary>
    public bool CanStartElevated { get; }

    /// <summary>
    /// Only while "start with Windows" is on. Without admin rights it can still be turned off
    /// (e.g. set up by an earlier version), just not on.
    /// </summary>
    public bool CanEditStartAsAdmin => IsAutostartKnown && StartWithWindows && (CanStartElevated || StartAsAdmin);

    public string StartWithWindowsDescription =>
        IsAutostartKnown ? Strings.Settings_StartWithWindows_Description : Strings.Settings_Autostart_Loading;

    /// <summary>
    /// What the option does, and while "start with Windows" is off, first why the row is disabled:
    /// the indentation alone doesn't tell that it depends on the row above.
    /// </summary>
    public string StartAsAdminDescription =>
        !CanStartElevated ? Strings.Settings_StartAsAdmin_NotAdmin
        : StartWithWindows ? Strings.Settings_StartAsAdmin_Hint
        : $"{Strings.Settings_StartAsAdmin_NeedsAutostart} {Strings.Settings_StartAsAdmin_Hint}";

    /// <summary>Shows the actual registration once it is known (when the window opens).</summary>
    public void SetAutostart(AutostartMode mode)
    {
        StartWithWindows = mode != AutostartMode.Off;
        StartAsAdmin = mode == AutostartMode.Elevated;
        IsAutostartKnown = true;
    }

    /// <summary>The two checkboxes combined into what the autostart service understands.</summary>
    public AutostartMode Autostart =>
        !StartWithWindows ? AutostartMode.Off
        : StartAsAdmin ? AutostartMode.Elevated
        : AutostartMode.Normal;

    public PlacementEditorViewModel DefaultPlacement { get; }

    /// <summary>About: "Version 0.9.0-beta · © 2026 …", what a bug report needs.</summary>
    public string AboutDescription { get; } =
        $"{string.Format(CultureInfo.CurrentCulture, Formats.Settings_About_Version, AppInfo.DisplayVersion)} · {AppInfo.Copyright}";
}
