using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>An entry in the "Add" menu.</summary>
public sealed record RunningProgramItem(RunningProgram Program, bool IsAlreadyAdded, ImageSource? Icon);

/// <summary>The "Games" tab: list of game profiles plus the selected one in the detail view.</summary>
public partial class GamesViewModel : ObservableObject
{
    private readonly IProcessDiscoveryService _discovery;
    private readonly IGameIconService _icons;
    private readonly PlacementEditorViewModel _defaultPlacement;
    private MonitorBounds? _monitor;
    private IReadOnlyList<DisplayInfo> _displays = [];
    private readonly IGameStatusProvider _statusProvider;
    private readonly IScreenPreview? _screenPreview;
    private readonly Func<GameProfile, RunningGameWindow?>? _runningGame;
    private IReadOnlyList<RunningProgram> _runningPrograms = [];
    private IReadOnlyDictionary<Guid, GameWindowStatus> _statuses = new Dictionary<Guid, GameWindowStatus>();

    /// <param name="defaultPlacement">The editor of the global default, shown under each game's "Use default".</param>
    public GamesViewModel(
        IEnumerable<GameProfile> profiles,
        PlacementEditorViewModel defaultPlacement,
        IProcessDiscoveryService discovery,
        IGameStatusProvider statusProvider,
        IGameIconService icons,
        IScreenPreview? screenPreview = null,
        Func<GameProfile, RunningGameWindow?>? runningGame = null)
    {
        _screenPreview = screenPreview;
        _runningGame = runningGame;
        _discovery = discovery;
        _statusProvider = statusProvider;
        _icons = icons;
        _defaultPlacement = defaultPlacement;
        RunningPrograms = [];

        foreach (var profile in profiles)
        {
            AddProfile(new GameProfileViewModel(profile, defaultPlacement));
        }

        UpdateValidation();
        Profiles.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnChanged();
        };
        SelectedProfile = Profiles.FirstOrDefault();
    }

    /// <summary>Raised on every edit that changes what would be saved (list or any field of any game).</summary>
    public event EventHandler? Changed;

    public ObservableCollection<GameProfileViewModel> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    public partial GameProfileViewModel? SelectedProfile { get; set; }

    /// <summary>No games yet: the details area shows the "add your first game" page instead.</summary>
    public bool IsEmpty => Profiles.Count == 0;

    /// <summary>Running programs from the last refresh, for the "Add" menu.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RunningProgramItem> RunningPrograms { get; private set; }

    /// <summary>
    /// Why the games can't be saved right now, naming the affected game(s), or null if everything is fine.
    /// Shown below the list, because the game with the problem may not be the selected one.
    /// </summary>
    public string? ValidationMessage
    {
        get
        {
            // The same results as the fields (see UpdateValidation), the most basic problem first
            if (FirstWith(ExeNameProblem.Missing) is { } missing)
            {
                return string.Format(CultureInfo.CurrentCulture, Formats.Games_Error_MissingExe, missing.Profile.Title);
            }

            if (FirstWith(ExeNameProblem.NotFileName) is { } notFileName)
            {
                return string.Format(CultureInfo.CurrentCulture, Formats.Games_Error_ExeNotFileName, notFileName.Profile.Title);
            }

            return FirstWith(ExeNameProblem.Duplicate) is { } duplicate
                ? string.Format(CultureInfo.CurrentCulture, Formats.Games_Error_DuplicateExe,
                    duplicate.Profile.Title, duplicate.SameNameAs!.Title)
                : null;

            ExeNameCheck? FirstWith(ExeNameProblem problem) => _exeNameChecks.FirstOrDefault(c => c.Problem == problem);
        }
    }

    public bool HasValidationMessage => ValidationMessage is not null;

    /// <summary>
    /// Games just added with "Enter manually": their empty exe field isn't shown as an error yet. Starting with a
    /// red field and a banner for something the user hasn't had a chance to type is just noise. Shown once they
    /// type into it (and clear it again), or when they try to save.
    /// </summary>
    private readonly HashSet<GameProfileViewModel> _quietUntilSave = [];

    /// <summary>Save was attempted: every problem is shown now, also the quiet ones.</summary>
    public void ShowAllErrors()
    {
        if (_quietUntilSave.Count > 0)
        {
            _quietUntilSave.Clear();
            RefreshValidation();
        }
    }

    /// <summary>
    /// Called before saving: an exe name typed without extension ("eldenring") gets ".exe", and trailing dots and
    /// spaces go ("eldenring.exe."): the watcher compares with the file names of running programs, and Windows never
    /// has those at the end. Otherwise the name would be saved and never match anything. Changes the field, so the
    /// user sees what was saved.
    /// </summary>
    public void CompleteExecutableNames()
    {
        foreach (var profile in Profiles)
        {
            // Nothing to complete if it can't be saved anyway: the field says why
            if (ToSavedExecutableName(profile.ExecutableName) is { } exe && exe != profile.ExecutableName.Trim())
            {
                profile.ExecutableName = exe;
            }
        }
    }

    /// <summary>
    /// The exe name Save stores for what was typed (see <see cref="CompleteExecutableNames"/>), or null if it can't
    /// be one: empty, nothing but dots, or more than a file name (a path). The one rule for completing, the field
    /// errors and the banner, so they always agree.
    /// </summary>
    private static string? ToSavedExecutableName(string typed)
    {
        var exe = typed.Trim().TrimEnd('.', ' ');

        // Only a file name, no path: the watcher compares it with the file name of each running program
        if (exe.Length == 0 || exe.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return Path.HasExtension(exe) ? exe : exe + ".exe";
    }

    private enum ExeNameProblem { None, Missing, NotFileName, Duplicate }

    /// <param name="SameNameAs">For <see cref="ExeNameProblem.Duplicate"/>: the first other game with that name.</param>
    private sealed record ExeNameCheck(GameProfileViewModel Profile, ExeNameProblem Problem, GameProfileViewModel? SameNameAs = null);

    /// <summary>The result for each game's exe name, in list order; set by <see cref="UpdateValidation"/>.</summary>
    private IReadOnlyList<ExeNameCheck> _exeNameChecks = [];

    /// <summary>The first field in the list order that can't be saved, or null if all are fine.</summary>
    public InputErrorLocation? FindFirstInputError()
    {
        foreach (var profile in Profiles)
        {
            // Also an empty exe name that isn't shown as an error yet (see _quietUntilSave)
            if (profile.ExecutableError is not null || string.IsNullOrWhiteSpace(profile.ExecutableName))
            {
                return new InputErrorLocation(SettingsTab.Games, profile, SettingsField.ExecutableName);
            }

            if (profile.Placement.FirstInvalidField is { } field)
            {
                return new InputErrorLocation(SettingsTab.Games, profile, field);
            }
        }

        return null;
    }

    public IReadOnlyList<GameProfile> ToProfiles() => [.. Profiles.Select(p => p.ToProfile())];

    /// <summary>See <see cref="PlacementEditorViewModel.SetDisplays"/>; remembered for games added later.</summary>
    public void SetDisplays(IReadOnlyList<DisplayInfo> displays, MonitorBounds windowMonitor)
    {
        _displays = displays;
        _monitor = windowMonitor;
        foreach (var profile in Profiles)
        {
            profile.Placement.SetDisplays(displays, windowMonitor);
        }
    }

    /// <summary>The refresh in progress, if any; further calls wait for it instead of starting another one.</summary>
    private Task? _refreshing;

    /// <summary>
    /// Re-reads the running programs: updates the status markers, icons and the "Add" menu.
    /// The slow part (asking every process for its exe, reading icons from the files) runs on a background
    /// thread, so the window stays responsive even if a file is on a sleeping disk.
    /// </summary>
    public async Task RefreshRunningAsync()
    {
        if (_refreshing is { } running)
        {
            await running;
            return;
        }

        _refreshing = RefreshRunningCoreAsync();
        try
        {
            await _refreshing;
        }
        finally
        {
            _refreshing = null;
        }
    }

    private async Task RefreshRunningCoreAsync()
    {
        // Read on the UI thread, used on the background thread
        var configured = Profiles.Select(p => p.ExecutableName.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // For the games that may run (seen by the running list or the watcher last time): which monitor their window
        // is on, for "the display the game is on" in their placement (see PlacementEditorViewModel.GameMonitor)
        var monitorCandidates = _runningGame is null ? [] : Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.ExecutableName))
            .Select(p => (Profile: p, Settings: p.ToProfile() with { Enabled = true }, WasRunning: p.IsRunning))
            .ToList();
        var savedPaths = Profiles
            .Where(p => p.Icon is null && GameProfile.PathIfItMatches(p.ExecutablePath, p.ExecutableName) is not null)
            .Select(p => (Exe: p.ExecutableName.Trim(), Path: p.ExecutablePath!))
            .ToList();

        var (programs, icons, gameMonitors) = await Task.Run(() =>
        {
            // Games that aren't running: their icon from the saved path. Read once per file (the icon service
            // caches per path), so this costs nothing on the next refreshes. A moved or deleted file has no icon.
            foreach (var (exe, path) in savedPaths)
            {
                _icons.Remember(exe, path);
            }

            var found = _discovery.GetRunningPrograms();

            // A running configured game: its icon stays available for the rest of the session
            foreach (var program in found.Where(p => configured.Contains(p.ExecutableName)))
            {
                _icons.Remember(program.ExecutableName, program.ExecutablePath);
            }

            var runningExes = found.Select(p => p.ExecutableName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var monitors = new Dictionary<GameProfileViewModel, MonitorBounds>();
            foreach (var (profile, settings, wasRunning) in monitorCandidates)
            {
                // Only asked for programs that run (or untitled windows the watcher already knows): one window search each
                if (!wasRunning && !runningExes.Contains(settings.ExecutableName.Trim()))
                {
                    continue;
                }

                try
                {
                    if (_runningGame!(settings) is { } window)
                    {
                        monitors[profile] = window.Monitor;
                    }
                }
                catch (Win32Exception)
                {
                    // The window closed in between: no monitor, like a game that isn't running
                }
            }

            return (found, found.Select(p => _icons.GetIconForFile(p.ExecutablePath)).ToList(), monitors);
        });

        // Back on the UI thread
        _runningPrograms = programs;

        // Refreshed together with the running markers (every 3 s while the window is open)
        _statuses = _statusProvider.GetStatuses();

        foreach (var profile in Profiles)
        {
            UpdateIsRunning(profile);
            profile.Icon ??= _icons.GetIcon(profile.ExecutableName);
            profile.Placement.GameMonitor = profile.IsRunning && gameMonitors.TryGetValue(profile, out var monitor) ? monitor : null;
        }

        IReadOnlyList<RunningProgramItem> items = [.. programs.Select((p, i) => new RunningProgramItem(
            p, IsConfigured(p), icons[i]))];

        // Only replace the list when something changed, so its bindings aren't rebuilt every 3 s
        if (!items.SequenceEqual(RunningPrograms))
        {
            RunningPrograms = items;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddRunning))]
    private void AddRunning(RunningProgramItem? item)
    {
        if (item is null || IsConfigured(item.Program))
        {
            return;
        }

        var friendlyName = GameNames.GetFriendlyName(item.Program.ExecutablePath);
        var profile = new GameProfileViewModel(GameProfile.FromRunningProgram(item.Program, friendlyName), _defaultPlacement);
        _icons.Remember(item.Program.ExecutableName, item.Program.ExecutablePath);
        AddProfile(profile);
        SelectedProfile = profile;
    }

    private static bool CanAddRunning(RunningProgramItem? item) => item is { IsAlreadyAdded: false };

    [RelayCommand]
    private void AddManually()
    {
        var profile = new GameProfileViewModel(new GameProfile { ExecutableName = "" }, _defaultPlacement);
        _quietUntilSave.Add(profile);   // before adding: adding runs the validation
        AddProfile(profile);
        SelectedProfile = profile;
    }

    /// <summary>"Choose program file…" in the Add menu. The file dialog itself is shown by the view.</summary>
    public void AddFromFile(string path)
    {
        var executableName = Path.GetFileName(path);

        // Already in the list: just go there instead of creating a duplicate. A game that only takes certain window
        // titles doesn't count: the file says nothing about titles, so this adds one for the program's other windows.
        if (Profiles.FirstOrDefault(p => p.Matches(new RunningProgram(0, executableName, "", path))) is { } existing)
        {
            SelectedProfile = existing;
            return;
        }

        var profile = new GameProfileViewModel(new GameProfile
        {
            ExecutableName = executableName,
            DisplayName = GameNames.GetFriendlyName(path),
            ExecutablePath = path,
        }, _defaultPlacement);
        _icons.Remember(executableName, path);
        AddProfile(profile);
        SelectedProfile = profile;
    }

    /// <summary>
    /// A game that was saved outside this window (the "add the game in the foreground" hotkey) while it is open.
    /// Added to the list so a Save here doesn't drop it again; unsaved edits and the selection stay as they are.
    /// </summary>
    public void AddSavedProfile(GameProfile profile)
    {
        if (Profiles.All(p => p.Id != profile.Id))
        {
            AddProfile(new GameProfileViewModel(profile, _defaultPlacement));
        }
    }

    /// <summary>A game was turned on or off outside this window (tray menu): the same here, other edits stay.</summary>
    public void ApplySavedEnabled(Guid id, bool enabled)
    {
        if (Profiles.FirstOrDefault(p => p.Id == id) is { } profile)
        {
            profile.Enabled = enabled;
        }
    }

    /// <summary>"Browse…" next to the exe name of a game. The file dialog itself is shown by the view.</summary>
    public void SetExecutableFromFile(GameProfileViewModel profile, string path)
    {
        var executableName = Path.GetFileName(path);
        _icons.Remember(executableName, path);
        profile.ExecutableName = executableName;   // also updates the running state, see OnProfileChanged
        profile.ExecutablePath = path;             // and this the icon, also when the file name stays the same

        if (string.IsNullOrWhiteSpace(profile.DisplayName))
        {
            profile.DisplayName = GameNames.GetFriendlyName(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove()
    {
        if (SelectedProfile is not { } profile)
        {
            return;
        }

        var index = Profiles.IndexOf(profile);
        profile.PropertyChanged -= OnProfileChanged;
        profile.Placement.PropertyChanged -= OnProfileChanged;
        profile.Placement.Detach();
        _quietUntilSave.Remove(profile);
        Profiles.RemoveAt(index);

        // Select the neighbor, so you can keep removing without clicking into the list again
        SelectedProfile = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    private bool CanRemove() => SelectedProfile is not null;

    /// <summary>A game in the list takes this program already (exe name, and its exe file and title filters).</summary>
    private bool IsConfigured(RunningProgram program) => Profiles.Any(p => p.Matches(program));

    private void UpdateIsRunning(GameProfileViewModel profile)
    {
        var hasStatus = _statuses.TryGetValue(profile.Id, out var status);
        profile.Status = hasStatus ? status : null;

        // The running-programs list only has windows with a title; the watcher also handles untitled ones,
        // and any status from it means it found the game's window
        profile.IsRunning = hasStatus || _runningPrograms.Any(profile.Matches);
        profile.Placement.IsGameRunning = profile.IsRunning;

        // The suggestion for the title filter: the program's window, even if the filter typed so far excludes it
        profile.CurrentWindowTitle = _runningPrograms.FirstOrDefault(profile.MatchesIgnoringTitle)?.WindowTitle;
    }

    private bool _isDetectionPaused;

    /// <summary>Paused or resumed (tray, hotkey, General tab): every game's status says so. Remembered for games added later.</summary>
    public void SetDetectionPaused(bool paused)
    {
        _isDetectionPaused = paused;
        foreach (var profile in Profiles)
        {
            profile.IsDetectionPaused = paused;
        }
    }

    private void AddProfile(GameProfileViewModel profile)
    {
        profile.IsDetectionPaused = _isDetectionPaused;
        profile.Placement.ScreenPreview = _screenPreview;
        if (_runningGame is { } runningGame)
        {
            // As edited right now: a changed exe name or filter finds the window the watcher would take after Save
            profile.Placement.RunningGameProvider = () => runningGame(profile.ToProfile() with { Enabled = true });
        }

        if (_monitor is { } monitor)
        {
            profile.Placement.SetDisplays(_displays, monitor);
        }

        UpdateIsRunning(profile);
        profile.Icon = _icons.GetIcon(profile.ExecutableName);
        profile.PropertyChanged += OnProfileChanged;
        profile.Placement.PropertyChanged += OnProfileChanged;

        // At its alphabetical place (see GameOrder)
        var index = 0;
        while (index < Profiles.Count && Profiles[index].CompareForList(profile) <= 0)
        {
            index++;
        }

        Profiles.Insert(index, profile);
    }

    /// <summary>
    /// Back to A–Z, called on Save: a renamed game moves to its place then, not while its name is being typed
    /// (it would jump away under the cursor). The selection stays on the same game.
    /// </summary>
    public void SortByName()
    {
        var selected = SelectedProfile;
        var sorted = Profiles.Order(Comparer<GameProfileViewModel>.Create((a, b) => a.CompareForList(b))).ToList();
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = Profiles.IndexOf(sorted[target]);
            if (current != target)
            {
                Profiles.Move(current, target);
            }
        }

        SelectedProfile = selected;
    }

    private void OnProfileChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Runtime state (running, icon, validation result) or a derived value (Title): nothing that would be saved
        if (sender is GameProfileViewModel && !GameProfileViewModel.IsEdit(e.PropertyName))
        {
            return;
        }

        // Same for the placement: only the user's choices, not its preview or display list (see IsEdit). Validity
        // can change with the displays too; the settings window re-checks that itself when they change.
        if (sender is PlacementEditorViewModel placement && !placement.IsEdit(e.PropertyName))
        {
            return;
        }

        // Other filters can change whether the game counts as running
        if (e.PropertyName is nameof(GameProfileViewModel.MatchExecutablePath) or nameof(GameProfileViewModel.WindowTitleContains)
            && sender is GameProfileViewModel filtered)
        {
            UpdateIsRunning(filtered);
        }

        // Typing a different exe name can change the running status and the icon right away
        if (e.PropertyName == nameof(GameProfileViewModel.ExecutableName) && sender is GameProfileViewModel profile)
        {
            _quietUntilSave.Remove(profile);   // typed into it: from now on an empty field is an error
            UpdateIsRunning(profile);
            profile.Icon = _icons.GetIcon(profile.ExecutableName);
        }

        // "Browse…" to another exe with the same file name: only the path changes, and with it the icon
        if (e.PropertyName == nameof(GameProfileViewModel.ExecutablePath) && sender is GameProfileViewModel browsed)
        {
            browsed.Icon = _icons.GetIcon(browsed.ExecutableName);
        }

        OnChanged();
    }

    private void OnChanged()
    {
        RefreshValidation();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshValidation()
    {
        UpdateValidation();
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(HasValidationMessage));
    }

    /// <summary>
    /// Checks every game's exe name once: the error directly below its field, and <see cref="_exeNameChecks"/> for
    /// the banner. Duplicates are found by the name Save would store, so "game" and "game.exe" count as the same.
    /// The same exe name is fine when the filters differ ("only this exe file", window title): see GameMatcher.
    /// </summary>
    private void UpdateValidation()
    {
        var savedNames = Profiles.ToDictionary(p => p, p => ToSavedExecutableName(p.ExecutableName));
        var gamesByName = savedNames
            .Where(pair => pair.Value is not null)
            .GroupBy(pair => pair.Key.DuplicateKey(pair.Value!), pair => pair.Key)
            .ToDictionary(group => group.Key, group => group.ToList());

        var checks = new List<ExeNameCheck>(Profiles.Count);
        foreach (var profile in Profiles)
        {
            var check =
                string.IsNullOrWhiteSpace(profile.ExecutableName)
                    ? new ExeNameCheck(profile, _quietUntilSave.Contains(profile) ? ExeNameProblem.None : ExeNameProblem.Missing)
                : savedNames[profile] is not { } name
                    ? new ExeNameCheck(profile, ExeNameProblem.NotFileName)
                : gamesByName[profile.DuplicateKey(name)].FirstOrDefault(other => other != profile) is { } other
                    ? new ExeNameCheck(profile, ExeNameProblem.Duplicate, other)
                : new ExeNameCheck(profile, ExeNameProblem.None);
            checks.Add(check);

            profile.ExecutableError = check.Problem switch
            {
                ExeNameProblem.Missing => Strings.Games_Error_MissingExe_Field,
                ExeNameProblem.NotFileName => Strings.Games_Error_ExeNotFileName_Field,
                ExeNameProblem.Duplicate => string.Format(CultureInfo.CurrentCulture, Formats.Games_Error_DuplicateExe_Field, check.SameNameAs!.Title),
                _ => null,
            };
        }

        _exeNameChecks = checks;
    }
}
