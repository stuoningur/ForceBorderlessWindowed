using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

using Microsoft.Extensions.Logging;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>The settings window: holds one ViewModel per tab and saves everything together.</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly IAutostartService _autostart;
    private readonly IDisplayService _display;
    private readonly IAppShell _shell;
    private readonly ILogger<SettingsViewModel> _logger;

    // Read once when the window opens: CanSave runs on every change, and reading the mode
    // means registry access plus starting schtasks.exe. null until known (see LoadAutostartAsync).
    private AutostartMode? _autostartMode;
    private MonitorBounds? _monitor;

    public SettingsViewModel(
        ISettingsService settings,
        IAutostartService autostart,
        IDisplayService display,
        IProcessDiscoveryService processDiscovery,
        IGameStatusProvider statusProvider,
        IGameIconService icons,
        IHotkeyService hotkeys,
        IAppShell shell,
        IScreenPreview screenPreview,
        IWindowManipulator manipulator,
        ILogger<SettingsViewModel> logger)
    {
        _settings = settings;
        _autostart = autostart;
        _display = display;
        _shell = shell;
        _logger = logger;

        // Usually known already (checked at app startup). If not, the window opens anyway and the
        // autostart checkboxes are enabled once the check is done, instead of freezing the window.
        var autostartMode = autostart.GetModeAsync();
        if (autostartMode.IsCompletedSuccessfully)
        {
            _autostartMode = autostartMode.Result;
        }

        General = new GeneralSettingsViewModel(settings.Current, _autostartMode, autostart.CanStartElevated);
        General.DefaultPlacement.ScreenPreview = screenPreview;
        Games = new GamesViewModel(settings.Current.Games, General.DefaultPlacement, processDiscovery, statusProvider, icons,
            screenPreview, game => processDiscovery.FindGameWindows([game]) is [var window, ..]
                ? new RunningGameWindow(display.GetMonitorForWindow(window.Handle), manipulator.GetClientSize(window.Handle))
                : null,
            display.GetMonitorForWindow);
        Hotkeys = new HotkeysViewModel(settings.Current.Hotkeys, hotkeys);
        Games.SetDetectionPaused(settings.Current.IsPaused);
        _savedGames = settings.Current.Games;

        // Any edit in a tab can change whether Save (and Restart) is possible
        General.PropertyChanged += OnEditorChanged;
        General.DefaultPlacement.PropertyChanged += OnEditorChanged;
        Games.Changed += (_, _) => OnUserEdit();
        Hotkeys.Changed += (_, _) => OnUserEdit();

        // Pause (tray, hotkey) and added games (hotkey) can change while this window is open
        _settings.SettingsChanged += OnSettingsChanged;
    }

    /// <summary>The games as last saved, to tell which ones a save outside this window added.</summary>
    private IReadOnlyList<GameProfile> _savedGames;

    /// <summary>
    /// Started by the window once it is shown: the autostart mode, if it wasn't known yet, and whether the admin
    /// autostart copy needs an update.
    /// </summary>
    public async Task LoadAutostartAsync()
    {
        if (_autostartMode is null)
        {
            var mode = await _autostart.GetModeAsync();
            _autostartMode = mode;
            General.SetAutostart(mode);
            RefreshUnsavedStateAndCommands();
        }

        IsAdminCopyOutdated = await _autostart.IsElevatedCopyOutdatedAsync();
    }

    /// <summary>
    /// "Start with Windows" with administrator rights would start an older version at the next sign-in (after an
    /// update): a bar offers to update that copy. Not a Save thing, it applies right away.
    /// </summary>
    [ObservableProperty]
    public partial bool IsAdminCopyOutdated { get; private set; }

    /// <summary>Why the last update of the admin copy didn't work; null while there's nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdminCopyMessage))]
    public partial string? AdminCopyProblem { get; private set; }

    public string AdminCopyMessage => AdminCopyProblem ?? Strings.Settings_AdminCopyOutdated;

    [RelayCommand]
    private async Task UpdateAdminCopyAsync()
    {
        var change = await _autostart.UpdateElevatedCopyAsync();
        AdminCopyProblem = change switch
        {
            AutostartChangeResult.Changed => null,
            AutostartChangeResult.CopyInUse => Strings.Settings_AdminCopyInUse,
            _ => Strings.Settings_AdminCopyNotUpdated,
        };
        IsAdminCopyOutdated = await _autostart.IsElevatedCopyOutdatedAsync();
    }

    /// <summary>
    /// Must be called when the window closes. The settings service lives as long as the app; without
    /// unsubscribing, its event would keep every closed settings window (and this ViewModel) in memory.
    /// Not IDisposable on purpose: the DI container would then keep all transient instances until exit.
    /// </summary>
    public void Detach()
    {
        _settings.SettingsChanged -= OnSettingsChanged;
        Hotkeys.Detach();

        // End the theme preview: back to the saved theme (a no-op if the previewed one was saved)
        _shell.ApplyTheme(_settings.Current.Theme);
    }

    /// <summary>
    /// settings.json couldn't be read at startup and the defaults are shown: says so, and where the old file is.
    /// Shown until the app restarts, because the copy stays relevant after a save too.
    /// </summary>
    public string? LoadProblemMessage => _settings.LoadProblem switch
    {
        null => null,
        { BackupFileName: { } fileName } =>
            string.Format(CultureInfo.CurrentCulture, Formats.Settings_LoadFailed_BackedUp, fileName),
        _ => Strings.Settings_LoadFailed_NotBackedUp,
    };

    public bool HasLoadProblem => _settings.LoadProblem is not null;

    [RelayCommand]
    private void OpenSettingsFolder() => _shell.OpenSettingsFolder();

    /// <summary>Detection is paused (tray menu): nothing gets changed, worth a visible hint.</summary>
    public bool IsPaused => _settings.Current.IsPaused;

    /// <summary>The shortcuts that work in this window right now: the saved ones, not the ones being edited.</summary>
    public HotkeySettings SavedHotkeys => _settings.Current.Hotkeys;

    /// <summary>Tooltip of the Save button, e.g. "Ctrl + S".</summary>
    public string SaveShortcutText => HotkeyRules.Format(SavedHotkeys.Save);

    // Raised on the UI thread (Save is called from the UI or a hotkey), so updating bindings directly is fine
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        // A game added by the hotkey: into the list too, or saving this window would remove it again.
        // Only games that are new in the saved settings: one the user removed here (not saved yet) stays removed.
        var current = _settings.Current.Games;
        if (!ReferenceEquals(current, _savedGames))
        {
            var before = _savedGames.ToDictionary(g => g.Id);
            foreach (var game in current)
            {
                if (!before.TryGetValue(game.Id, out var old))
                {
                    Games.AddSavedProfile(game);
                }
                else if (old.Enabled != game.Enabled)
                {
                    // Turned on or off in the tray: the same here, or saving this window would undo it
                    Games.ApplySavedEnabled(game.Id, game.Enabled);
                }
            }

            _savedGames = current;
        }

        OnPropertyChanged(nameof(IsPaused));
        Games.SetDetectionPaused(IsPaused);
        OnPropertyChanged(nameof(SavedHotkeys));
        OnPropertyChanged(nameof(SaveShortcutText));
    }

    /// <summary>
    /// Saves only the pause flag on top of the saved settings, so unsaved edits in this window stay unsaved.
    /// The tray menu picks up the change through SettingsChanged.
    /// </summary>
    [RelayCommand]
    private void Resume() => _settings.Save(_settings.Current with { IsPaused = false });

    /// <summary>
    /// The Pause/Resume button in the General tab. Applies immediately like the tray menu, not on Save
    /// (hence a button, not a checkbox); same "only the pause flag" rule as <see cref="Resume"/>.
    /// </summary>
    [RelayCommand]
    private void TogglePause() => _settings.Save(_settings.Current with { IsPaused = !_settings.Current.IsPaused });

    public GeneralSettingsViewModel General { get; }

    public GamesViewModel Games { get; }

    public HotkeysViewModel Hotkeys { get; }

    public bool IsRestartRequired => _settings.Current.Language != _settings.ActiveLanguage;

    /// <summary>
    /// True if something differs from what is saved, valid or not. Invalid input counts as a change too:
    /// a field showing "abc" holds something the user typed, even though the last valid number is unchanged.
    /// </summary>
    public bool HasUnsavedChanges =>
        !AreEqual(BuildSettings(), _settings.Current) || IsAutostartChanged || FindFirstInputError() is not null;

    /// <summary>
    /// Raised when Save can't happen because of invalid input: the view goes to that field.
    /// Saving with Save always enabled and then showing what's wrong tells more than a disabled button.
    /// </summary>
    public event EventHandler<InputErrorLocation>? InputErrorFound;

    /// <summary>What the last Save couldn't do (it was only partly saved); null = nothing to report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSaveProblem))]
    public partial string? SaveProblem { get; private set; }

    public bool HasSaveProblem => SaveProblem is not null;

    /// <summary>How long "Saved" stays in the footer after a save that went through completely.</summary>
    private static readonly TimeSpan SavedConfirmationDuration = TimeSpan.FromSeconds(3);

    // Each save restarts the timer: a delay from an earlier save must not hide a newer confirmation
    private int _savedConfirmationId;

    /// <summary>
    /// True for a few seconds after everything was saved: without it, the only sign of a save would be
    /// the "not applied yet" hint disappearing. Hidden right away by the next edit.
    /// </summary>
    [ObservableProperty]
    public partial bool IsSavedConfirmationVisible { get; private set; }

    private async void ShowSavedConfirmation()
    {
        var id = ++_savedConfirmationId;
        IsSavedConfirmationVisible = true;

        // Continues on the UI thread (the save runs there)
        await Task.Delay(SavedConfirmationDuration);
        if (id == _savedConfirmationId)
        {
            IsSavedConfirmationVisible = false;
        }
    }

    private void HideSavedConfirmation()
    {
        _savedConfirmationId++;
        IsSavedConfirmationVisible = false;
    }

    /// <summary>The first invalid field in tab order: General, then the games in list order, then the hotkeys.</summary>
    private InputErrorLocation? FindFirstInputError() =>
        General.DefaultPlacement.FirstInvalidField is { } field
            ? new InputErrorLocation(SettingsTab.General, null, field)
            : Games.FindFirstInputError() ?? Hotkeys.FindFirstInputError();

    // Not known yet = the checkboxes are disabled and can't have been changed
    private bool IsAutostartChanged => _autostartMode is { } mode && General.Autostart != mode;

    /// <summary>
    /// Whether closing the window should ask "Save changes?". Not while the app is exiting:
    /// at that point closing can no longer be cancelled.
    /// </summary>
    public bool ShouldConfirmClose => HasUnsavedChanges && !_shell.IsShuttingDown;

    public bool IsShuttingDown => _shell.IsShuttingDown;

    /// <summary>
    /// Called by the window when it is created or moved (its monitor is the preview for "display the game is on"),
    /// and with <paramref name="displaysChanged"/> when a monitor was plugged in/out or a resolution changed.
    /// </summary>
    public void UpdateDisplays(nint windowHandle, bool displaysChanged = false)
    {
        var monitor = _display.GetMonitorForWindow(windowHandle);
        if (monitor == _monitor && !displaysChanged)
        {
            return;
        }

        _monitor = monitor;
        var displays = _display.GetDisplays();
        General.DefaultPlacement.SetDisplays(displays, monitor);
        Games.SetDisplays(displays, monitor);

        // Not an edit (the messages about the last Save stay), but custom sizes may fit or not fit anymore
        RefreshUnsavedStateAndCommands();
    }

    /// <summary>
    /// Async: changing the elevated autostart waits for the UAC prompt and the elevated helper.
    /// While it runs, the command is disabled (AsyncRelayCommand does that), so Save can't run twice.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await SaveChangesAsync();

    /// <returns>
    /// False if not everything is saved: invalid input (nothing saved, the view shows the field), or the
    /// autostart change was declined or failed (the rest is saved, <see cref="SaveProblem"/> says so).
    /// </returns>
    public async Task<bool> SaveChangesAsync()
    {
        // ".exe" added where it was left out, and problems that were kept quiet until now (an empty exe field of a
        // game just added by hand) are shown. Both before the check, so it sees and shows the final state.
        Games.CompleteExecutableNames();
        Games.ShowAllErrors();

        SaveProblem = null;
        HideSavedConfirmation();

        if (FindFirstInputError() is { } error)
        {
            InputErrorFound?.Invoke(this, error);
            return false;
        }

        // Renamed and new games to their A–Z place; the file is saved in that order too
        Games.SortByName();

        var newSettings = BuildSettings();
        var settingsChanged = !AreEqual(newSettings, _settings.Current);
        if (settingsChanged)
        {
            var themeChanged = newSettings.Theme != _settings.Current.Theme;
            LogSaving(newSettings.Language, newSettings.DefaultPlacement.Mode, newSettings.Games.Count);
            _settings.Save(newSettings);
            Hotkeys.MarkSaved(newSettings.Hotkeys);

            // Unlike the language, the theme can switch at runtime, so it's applied right away. Only when it
            // changed: applying swaps the color resources, which can rebuild parts of the window (and reset the
            // game details' scroll position) for nothing.
            if (themeChanged)
            {
                _shell.ApplyTheme(newSettings.Theme);
            }
        }

        // Autostart lives in the registry / Task Scheduler, not in settings.json.
        // If the UAC prompt is declined, the actual mode stays the old one: the checkboxes then
        // still differ from it, so the window keeps counting that as an unsaved change.
        var autostartSaved = true;
        if (IsAutostartChanged)
        {
            // Read once: the checkboxes stay editable during the UAC prompt. A click meanwhile must not be taken
            // for what was registered; it simply stays an unsaved change.
            var requested = General.Autostart;
            var change = await _autostart.SetModeAsync(requested);
            autostartSaved = change == AutostartChangeResult.Changed;
            _autostartMode = autostartSaved ? requested : await _autostart.GetModeAsync();

            // Turning admin autostart off removes the old copy's task, turning it on copies this version
            IsAdminCopyOutdated = await _autostart.IsElevatedCopyOutdatedAsync();

            if (!autostartSaved)
            {
                SaveProblem = (change, settingsChanged) switch
                {
                    (AutostartChangeResult.CopyInUse, true) => Strings.Settings_AutostartInUse_OthersSaved,
                    (AutostartChangeResult.CopyInUse, false) => Strings.Settings_AutostartInUse,
                    (_, true) => Strings.Settings_AutostartNotChanged_OthersSaved,
                    _ => Strings.Settings_AutostartNotChanged,
                };
            }
        }

        // The saved state has changed, so the commands and IsRestartRequired need to be re-evaluated
        RefreshUnsavedStateAndCommands();
        OnPropertyChanged(nameof(IsRestartRequired));

        // Partly saved: the warning (SaveProblem) says what happened instead. Edited again while the autostart
        // change waited for UAC: those edits aren't saved, so "Saved" would be wrong next to the unsaved hint.
        if (autostartSaved && !HasUnsavedChanges)
        {
            ShowSavedConfirmation();
        }

        return autostartSaved;
    }

    // Also with invalid input: Save then shows the first problem (see SaveChangesAsync)
    private bool CanSave() => HasUnsavedChanges;

    // "with" starts from the latest saved state, so values changed elsewhere (e.g. IsPaused from the tray) are kept
    private AppSettings BuildSettings() => _settings.Current with
    {
        Language = General.SelectedLanguage.CultureName,
        Theme = General.SelectedTheme.Theme,
        DefaultPlacement = General.DefaultPlacement.ToSettings() ?? new PlacementSettings(),
        Games = Games.ToProfiles(),
        Hotkeys = Hotkeys.ToSettings(),
    };

    private static readonly IReadOnlyList<GameProfile> NoGames = [];

    // Records compare lists by reference, so == alone would always see the games as changed.
    // Compare everything except the list with ==, and the games item by item (GameProfile is a record).
    // The games' order doesn't count: the list shows them A–Z whatever order the file has (an older file, or a
    // rename since the last Save), and that alone isn't a change to save
    private static bool AreEqual(AppSettings a, AppSettings b) =>
        a with { Games = NoGames } == b with { Games = NoGames }
        && a.Games.OrderBy(game => game.Id).SequenceEqual(b.Games.OrderBy(game => game.Id));

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Live preview: the theme switches as soon as it is picked, so you can compare before saving.
        // Closing without saving switches back (see Detach).
        if (e.PropertyName == nameof(GeneralSettingsViewModel.SelectedTheme))
        {
            _shell.ApplyTheme(General.SelectedTheme.Theme);
        }

        // The default placement's preview and display list also change without the user (see IsEdit)
        if (sender is PlacementEditorViewModel placement && !placement.IsEdit(e.PropertyName))
        {
            return;
        }

        OnUserEdit();
    }

    /// <summary>An edit after a Save: the message about that Save (problem or confirmation) is outdated now.</summary>
    private void OnUserEdit()
    {
        SaveProblem = null;
        HideSavedConfirmation();
        RefreshUnsavedStateAndCommands();
    }

    private void RefreshUnsavedStateAndCommands()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        SaveCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
        RestartAsAdminCommand.NotifyCanExecuteChanged();
    }

    // Restarting would silently throw away unsaved edits, so it's only possible once everything is saved
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void Restart() => _shell.RestartApplication();

    // Same rule as Restart: unsaved edits would be lost
    [RelayCommand(CanExecute = nameof(CanRestart))]
    private void RestartAsAdmin() => _shell.RestartApplication(asAdministrator: true);

    private bool CanRestart() => !HasUnsavedChanges;

    [LoggerMessage(Level = LogLevel.Information, Message = "Saving settings: language {Language}, default placement {Mode}, {GameCount} games")]
    private partial void LogSaving(string? language, PlacementMode mode, int gameCount);
}
