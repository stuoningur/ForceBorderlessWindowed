using System.Globalization;
using System.Windows.Media;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Controls;
using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>One entry in the games list, editable in the detail view.</summary>
public partial class GameProfileViewModel : ObservableObject
{
    /// <summary>
    /// Whether a change of this property is an edit: only what is saved (see <see cref="ToProfile"/>). Everything
    /// else describes the current state (running, icon, validation) or follows from a saved value (Title), and
    /// must not count. A list of the edits, not of the rest: a new derived property is then never an edit by mistake.
    /// The placement has its own rule, see <see cref="PlacementEditorViewModel.IsEdit"/>.
    /// </summary>
    public static bool IsEdit(string? propertyName) => propertyName is
        nameof(ExecutableName) or nameof(DisplayName) or nameof(ExecutablePath) or nameof(Enabled)
        or nameof(LockCursor) or nameof(HideTaskbar) or nameof(BlackBackground) or nameof(MatchExecutablePath)
        or nameof(WindowTitleContains);

    /// <param name="defaultPlacement">The editor of the global default, for "Use default (currently: ...)".</param>
    public GameProfileViewModel(GameProfile profile, PlacementEditorViewModel defaultPlacement)
    {
        Id = profile.Id;
        ExecutableName = profile.ExecutableName;
        DisplayName = profile.DisplayName;
        ExecutablePath = profile.ExecutablePath;
        MatchExecutablePath = profile.MatchExecutablePath;
        WindowTitleContains = profile.WindowTitleContains;
        Enabled = profile.Enabled;
        LockCursor = profile.LockCursor;
        HideTaskbar = profile.HideTaskbar;
        BlackBackground = profile.BlackBackground;
        Placement = new PlacementEditorViewModel(profile.Placement, defaultPlacement);
    }

    /// <summary>The game's identity, see <see cref="GameProfile.Id"/>: the watcher's statuses are per ID.</summary>
    public Guid Id { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(AccessibleName), nameof(ListSubtitle), nameof(HasListSubtitle),
        nameof(ListOffText), nameof(CanMatchExecutablePath), nameof(ExecutablePathText))]
    public partial string ExecutableName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(AccessibleName), nameof(ListSubtitle), nameof(HasListSubtitle),
        nameof(ListOffText))]
    public partial string? DisplayName { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(ListOffText), nameof(HasListSubtitle), nameof(StatusText),
        nameof(ShowStatusBadge))]
    public partial bool Enabled { get; set; }

    /// <summary>Full exe path for the icon and for <see cref="MatchExecutablePath"/>, see <see cref="GameProfile.ExecutablePath"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanMatchExecutablePath), nameof(ExecutablePathText))]
    public partial string? ExecutablePath { get; set; }

    /// <summary>"Only this exe file", see <see cref="GameProfile.MatchExecutablePath"/>.</summary>
    [ObservableProperty]
    public partial bool MatchExecutablePath { get; set; }

    /// <summary>
    /// Only with a full path that belongs to the exe name: known when the game was added from a running program, a file
    /// or the hotkey, or after Browse. A game typed in by hand has none.
    /// </summary>
    public bool CanMatchExecutablePath => PathForMatching is not null;

    private string? PathForMatching => GameProfile.PathIfItMatches(ExecutablePath, ExecutableName);

    /// <summary>Under the "only this exe file" box: which file that is, or how to get one.</summary>
    public string ExecutablePathText => PathForMatching ?? Strings.Games_MatchExecutablePath_NoPath;

    /// <summary>"Window title contains", see <see cref="GameProfile.WindowTitleContains"/>. Empty = any window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(ListSubtitle), nameof(HasListSubtitle), nameof(ListOffText),
        nameof(NotRunningText))]
    public partial string? WindowTitleContains { get; set; }

    /// <summary>
    /// The title of the running program's window (exe name and file as set here, any title), as a suggestion for
    /// <see cref="WindowTitleContains"/>: shown greyed in the empty field, "Use current title" copies it. Not saved;
    /// null while it isn't running.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseCurrentTitleCommand))]
    [NotifyPropertyChangedFor(nameof(NotRunningText), nameof(AccessibleName))]
    public partial string? CurrentWindowTitle { get; set; }

    [RelayCommand(CanExecute = nameof(CanUseCurrentTitle))]
    private void UseCurrentTitle() => WindowTitleContains = CurrentWindowTitle;

    private bool CanUseCurrentTitle() => !string.IsNullOrWhiteSpace(CurrentWindowTitle);

    /// <summary>For the title suggestion: the program as the exe name and file here take it, whatever its title.</summary>
    internal bool MatchesIgnoringTitle(RunningProgram program) => GameMatcher.Matches(
        ExecutableName, MatchExecutablePath ? PathForMatching : null, null,
        program.ExecutableName, program.ExecutablePath, program.WindowTitle);

    /// <summary>Whether this game, as edited right now, takes the running program (see GameMatcher).</summary>
    internal bool Matches(RunningProgram program) => GameMatcher.Matches(
        ExecutableName, MatchExecutablePath ? PathForMatching : null, WindowTitleContains,
        program.ExecutableName, program.ExecutablePath, program.WindowTitle);

    /// <summary>What makes two games the same for the duplicate check, with the exe name Save would store.</summary>
    internal (string Exe, string? Path, string? Title) DuplicateKey(string savedExecutableName) =>
        GameMatcher.Key(savedExecutableName, MatchExecutablePath ? PathForMatching : null, WindowTitleContains);

    [ObservableProperty]
    public partial bool LockCursor { get; set; }

    [ObservableProperty]
    public partial bool HideTaskbar { get; set; }

    [ObservableProperty]
    public partial bool BlackBackground { get; set; }

    /// <summary>The program's icon, null = unknown (the list then shows a generic one). Not saved.</summary>
    [ObservableProperty]
    public partial ImageSource? Icon { get; set; }

    /// <summary>What's wrong with the exe name (missing, used twice), shown right below the field. Not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExecutableError), nameof(AccessibleName))]
    public partial string? ExecutableError { get; set; }

    public bool HasExecutableError => ExecutableError is not null;

    /// <summary>Runtime status for the list marker, not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ProblemText), nameof(HasProblem), nameof(CanRestartAsAdmin),
        nameof(StatusSeverity), nameof(AccessibleName), nameof(ShowStatusBadge), nameof(NotRunningText))]
    public partial bool IsRunning { get; set; }

    /// <summary>What the watcher did with the window; null = nothing to report (yet). Not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ProblemText), nameof(HasProblem), nameof(CanRestartAsAdmin),
        nameof(StatusSeverity), nameof(AccessibleName), nameof(ShowStatusBadge))]
    public partial GameWindowStatus? Status { get; set; }

    /// <summary>
    /// Running, turned off, and the app has nothing of it (anymore). Based on the status, not only on the checkbox:
    /// unchecked but not saved yet, the window is still borderless, and the status says so until Save.
    /// </summary>
    private bool IsRunningButOff => IsRunning && !Enabled && Status is null;

    /// <summary>Detection is paused (tray, hotkey, General tab): the watcher has restored every window. Not saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(StatusSeverity), nameof(AccessibleName), nameof(ShowStatusBadge))]
    public partial bool IsDetectionPaused { get; set; }

    /// <summary>Running and would be made borderless, but detection is paused. Off games keep their own "off" status.</summary>
    private bool IsRunningWhilePaused => IsRunning && IsDetectionPaused && Enabled && Status is null;

    /// <summary>
    /// The badge on the list icon: only when something happened to the window (borderless) or needs a look.
    /// Not for neutral states (off, paused, no frame of its own, nothing done yet): the list would otherwise be
    /// full of dots that say nothing, and the informational color is the Windows accent color, which can look
    /// like an error (red accent) or a warning (orange accent).
    /// </summary>
    public bool ShowStatusBadge => IsRunning && StatusSeverity != InfoBarSeverity.Informational;

    /// <summary>
    /// The game runs as admin and we don't: restarting this app as admin would fix it.
    /// IsPrivilegedProcess can't change while the process runs, so it needs no change notification.
    /// </summary>
    public bool CanRestartAsAdmin =>
        IsRunning && Status == GameWindowStatus.AccessDenied && !Environment.IsPrivilegedProcess;

    /// <summary>Short status: title of the status bar in the details, and part of the list entry's spoken name.</summary>
    public string? StatusText => !IsRunning ? null : Status switch
    {
        GameWindowStatus.Borderless => Strings.Games_Status_Borderless,
        GameWindowStatus.NoFrame => Strings.Games_Status_NoFrame,
        GameWindowStatus.NotResponding => Strings.Games_Status_NotResponding,
        GameWindowStatus.FightPaused => Strings.Games_Status_FightPaused,
        GameWindowStatus.Failed => Strings.Games_Status_Failed,
        GameWindowStatus.AccessDenied => Strings.Games_Status_AccessDenied,
        _ when IsRunningButOff => Strings.Games_Status_RunningOff,
        _ when IsRunningWhilePaused => Strings.Games_Status_Paused,
        _ => Strings.Games_Running,
    };

    /// <summary>Explanation + what to do, shown in the details; null when there's no problem.</summary>
    public string? ProblemText => !IsRunning ? null : Status switch
    {
        GameWindowStatus.NoFrame => Strings.Games_Problem_NoFrame,
        GameWindowStatus.NotResponding => Strings.Games_Problem_NotResponding,
        GameWindowStatus.FightPaused => Strings.Games_Problem_FightPaused,
        GameWindowStatus.Failed => Strings.Games_Problem_Failed,
        // Already admin and still denied: the game is protected on a higher level (e.g. anti-cheat)
        GameWindowStatus.AccessDenied when Environment.IsPrivilegedProcess => Strings.Games_Problem_AccessDeniedElevated,
        GameWindowStatus.AccessDenied => Strings.Games_Problem_AccessDenied,
        _ => null,
    };

    public bool HasProblem => ProblemText is not null;

    /// <summary>Color and icon of the status bar and of the badge on the list icon.</summary>
    public InfoBarSeverity StatusSeverity => Status switch
    {
        GameWindowStatus.Borderless => InfoBarSeverity.Success,
        // Running, but nothing done yet (e.g. minimized), detection paused (the user's choice; the bar at the top
        // of the window already warns about it), or no frame of its own: fullscreen or the game's own borderless
        // mode. Nothing is wrong then, it's simply left as it is. Not responding: usually loading, it passes by itself.
        null or GameWindowStatus.NoFrame or GameWindowStatus.NotResponding => InfoBarSeverity.Informational,
        GameWindowStatus.FightPaused => InfoBarSeverity.Warning,
        _ => InfoBarSeverity.Error,
    };

    /// <summary>
    /// What a screen reader says for the list entry, e.g. "Elden Ring, eldenring.exe, Running – borderless".
    /// The list itself only shows this with icons and dimming, which a screen reader can't see.
    /// </summary>
    public string AccessibleName => string.Join(", ", new[]
    {
        Title,
        string.IsNullOrWhiteSpace(ExecutableName) || Title == ExecutableName.Trim() ? null : ExecutableName.Trim(),
        TitleFilter is { } filter ? string.Format(CultureInfo.CurrentCulture, Formats.Games_Accessible_TitleFilter, filter) : null,
        StatusText ?? NotRunningText,
        Enabled || IsRunningButOff ? null : Strings.Games_Status_Disabled,   // the off status already says "off"
        ExecutableError,
    }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>
    /// Shown instead of a status while the game isn't running. The program may run with a window whose title doesn't
    /// match <see cref="WindowTitleContains"/>: "Not running" would be wrong then.
    /// </summary>
    public string NotRunningText =>
        !IsRunning && CurrentWindowTitle is not null && TitleFilter is not null
            ? Strings.Games_Status_NoMatchingTitle
            : Strings.Games_Status_NotRunning;

    internal string? TitleFilter => GameMatcher.NormalizeTitle(WindowTitleContains);

    /// <summary>Position in the Games list, see <see cref="GameOrder"/>.</summary>
    internal int CompareForList(GameProfileViewModel other) =>
        GameOrder.Compare(Title, TitleFilter, Id, other.Title, other.TitleFilter, other.Id);

    /// <summary>
    /// Second line in the list: the exe name and the title filter, which tell games with the same exe apart.
    /// Without the exe name when the title already is the exe name (no display name): "mspaint.exe" twice says nothing.
    /// "Off" is separate (<see cref="ListOffText"/>), so trimming a long line never cuts it off.
    /// </summary>
    public string ListSubtitle
    {
        get
        {
            var exe = ExecutableName.Trim();
            var name = exe.Length == 0 || Title == exe ? null : exe;
            var filter = TitleFilter is { } title
                ? string.Format(CultureInfo.CurrentCulture, Formats.Games_ListTitleFilter, title)
                : null;
            return string.Join(" · ", new[] { name, filter }.Where(part => part is not null));
        }
    }

    /// <summary>"· Off" after <see cref="ListSubtitle"/> for games that aren't applied, just "Off" without one.</summary>
    public string ListOffText =>
        Enabled ? ""
        : ListSubtitle.Length == 0 ? Strings.Games_Status_Disabled
        : $" · {Strings.Games_Status_Disabled}";

    public bool HasListSubtitle => ListSubtitle.Length > 0 || !Enabled;

    public PlacementEditorViewModel Placement { get; }

    /// <summary>Shown in the list: display name, otherwise exe name, otherwise "New game".</summary>
    public string Title =>
        !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName.Trim()
        : !string.IsNullOrWhiteSpace(ExecutableName) ? ExecutableName.Trim()
        : Strings.Games_NewGame;

    public GameProfile ToProfile() => new()
    {
        Id = Id,
        ExecutableName = ExecutableName.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? null : DisplayName.Trim(),
        ExecutablePath = GameProfile.PathIfItMatches(ExecutablePath, ExecutableName),
        MatchExecutablePath = MatchExecutablePath && CanMatchExecutablePath,
        WindowTitleContains = GameMatcher.NormalizeTitle(WindowTitleContains),
        Enabled = Enabled,
        LockCursor = LockCursor,
        HideTaskbar = HideTaskbar,
        BlackBackground = BlackBackground,
        Placement = Placement.ToSettings(),
    };
}
