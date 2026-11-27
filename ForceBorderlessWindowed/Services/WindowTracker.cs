using System.IO;

using ForceBorderlessWindowed.Models;

using Microsoft.Extensions.Logging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The rules of the watcher: which game windows to make borderless, remembering their original state, putting them
/// back (Pause, game turned off, exit), the fight limit, and taking over windows a crashed run left behind.
/// No threads and no timers: <see cref="GameWindowWatcher"/> decides when to call what, always from its own thread,
/// so the fields need no locking. Everything about the desktop goes through interfaces (and the clock through
/// <see cref="TimeProvider"/>), so these rules can be tested against a fake desktop.
/// </summary>
internal sealed partial class WindowTracker(
    IProcessDiscoveryService discovery,
    IWindowManipulator manipulator,
    IDisplayService display,
    IWindowQueries queries,
    IChangedWindowsStore changedWindows,
    TimeProvider time,
    ILogger<WindowTracker> logger)
{
    // Fight limit: a game that undoes our change more than MaxApplies times within FightWindow
    // is left alone for FightPause, then we try again. Never "give up forever".
    internal const int MaxApplies = 5;
    internal static readonly TimeSpan FightWindow = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan FightPause = TimeSpan.FromSeconds(30);

    private readonly ILogger<WindowTracker> _logger = logger;

    /// <summary>What was last written to <see cref="IChangedWindowsStore"/>; null = not written yet.</summary>
    private HashSet<ChangedWindowRecord>? _persistedRecords;

    /// <summary>
    /// The file of the last run hasn't been read yet: not tried yet (true from the start), or it couldn't be read
    /// (locked, access denied). Until it is, it is read again on every check and never written: writing would
    /// replace the original state of those windows.
    /// </summary>
    private bool _recoveryPending = true;

    /// <summary>Windows we have changed (or tried to), with their original state for restoring.</summary>
    private readonly Dictionary<nint, TrackedWindow> _tracked = [];

    /// <summary>Frameless windows we decided not to touch, with their game; for the "no frame" status and to log it once.</summary>
    private readonly Dictionary<nint, (string Exe, Guid ProfileId)> _ignored = [];

    /// <summary>
    /// Exe name per process ID for the event filter (null = no exe name available).
    /// Cleared on every poll, so a reused process ID can't stay wrong for long.
    /// </summary>
    private readonly Dictionary<uint, string?> _exeByProcess = [];

    /// <summary>Connected displays, refreshed on every poll and right away when the display setup changes.</summary>
    private IReadOnlyList<DisplayInfo> _displays = [];

    /// <summary>Selected displays that aren't connected; only remembered so the log says it once.</summary>
    private readonly HashSet<string> _missingDisplaysLogged = new(StringComparer.OrdinalIgnoreCase);

    // Enabled games, rebuilt only when the settings object changes (Save swaps it)
    private AppSettings? _gamesSource;
    private EnabledGames _games = new([], [], new(StringComparer.OrdinalIgnoreCase));

    /// <param name="ById">For looking up the game of a window found earlier.</param>
    /// <param name="InOrder">In list order (which game wins a window doesn't depend on it, see GameMatcher).</param>
    /// <param name="ByExe">For the cheap event filter and for matching one window, see <see cref="FindGame"/>.</param>
    private sealed record EnabledGames(
        Dictionary<Guid, GameProfile> ById, List<GameProfile> InOrder, Dictionary<string, List<GameProfile>> ByExe);

    /// <summary>
    /// Windows made borderless with the one-off hotkey, not as a game (see IOneOffWindows). Handled like a game window
    /// with the default placement until closed, Pause, exit, or the hotkey again; never taken back on by itself.
    /// With the window's process ID: Windows reuses the numbers of closed windows, and with a missed "destroyed"
    /// event the request must not carry over to another program's window (see <see cref="IsOneOffGone"/>).
    /// </summary>
    private readonly Dictionary<nint, uint> _oneOff = [];

    /// <summary>The "game" of one-off windows: never a real game's ID, so the UI shows no status for it.</summary>
    internal static readonly Guid OneOffId = new("0b1f0ff0-0000-4000-8000-000000000001");

    /// <summary>Set when the app exits: the final restore has no "next check" to retry in.</summary>
    private bool _stopping;

    private DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    /// <summary>True while any window is tracked (changed, or a restore still pending).</summary>
    public bool HasTrackedWindows => _tracked.Count > 0;

    public bool IsTracked(nint handle) => _tracked.ContainsKey(handle);

    /// <summary>Called on every poll: a reused process ID can't keep a wrong exe name for long.</summary>
    public void ForgetProcessCache() => _exeByProcess.Clear();

    public void RefreshDisplays()
    {
        var displays = display.GetDisplays();

        // A different setup: a missing display may be back (or gone again), worth logging again
        if (!displays.SequenceEqual(_displays))
        {
            _missingDisplaysLogged.Clear();
        }

        _displays = displays;
    }

    /// <summary>
    /// A save is the user asking for the new settings now: the fight limit starts over. Otherwise a few quick
    /// saves would count as "the game keeps resetting its window", and a running fight pause would hold back
    /// the new placement for up to 30 s.
    /// </summary>
    public void ResetFightLimits()
    {
        foreach (var tracked in _tracked.Values)
        {
            tracked.RecentApplies.Clear();
            tracked.PausedUntil = default;
        }
    }

    /// <summary>
    /// After the check that followed <see cref="ToggleOneOff"/>: what really happened to the window, for the overlay and
    /// the sound. A change Windows refused is given up (a one-off window has no status anywhere that would show the
    /// retries); a restore that didn't work yet stays pending and is tried again on every check, like any restore.
    /// </summary>
    public OneOffResult ConfirmOneOff(nint handle, OneOffResult requested)
    {
        switch (requested)
        {
            case OneOffResult.Added:
                if (!_oneOff.ContainsKey(handle))
                {
                    return OneOffResult.NoFrame;   // left alone by the check (no frame anymore) or closed
                }

                if (_tracked.TryGetValue(handle, out var tracked)
                    && tracked.LastResult is WindowChangeResult.AccessDenied or WindowChangeResult.Failed)
                {
                    var failed = tracked.LastResult == WindowChangeResult.AccessDenied ? OneOffResult.AccessDenied : OneOffResult.Failed;
                    _oneOff.Remove(handle);
                    RestoreWhere(t => t.Handle == handle, "one-off change failed");   // only puts back a partial change
                    return failed;
                }

                return requested;

            case OneOffResult.Restored when _tracked.TryGetValue(handle, out var pending):
                return pending.LastRestoreResult == WindowChangeResult.AccessDenied ? OneOffResult.AccessDenied : OneOffResult.Failed;

            default:
                return requested;
        }
    }

    /// <summary>
    /// The window was destroyed: forget it. Returns true if it was tracked or ignored, i.e. the statuses,
    /// the foreground effects and the file of changed windows need updating.
    /// </summary>
    public bool ForgetDestroyed(nint handle) =>
        _tracked.Remove(handle) | _ignored.Remove(handle) | _oneOff.Remove(handle);   // | on purpose: all must run

    /// <summary>
    /// The one-off hotkey: makes the window borderless with the default placement (taking effect with the next check),
    /// or puts it back if it already is a one-off window. Refused while paused, for a window without a frame (fullscreen
    /// or its own borderless mode, same rule as for games) and for a configured game's window (already handled).
    /// </summary>
    public OneOffResult ToggleOneOff(nint handle, AppSettings current)
    {
        var processId = queries.GetProcessId(handle);
        var exe = Path.GetFileName(queries.GetExecutablePath(processId)) ?? "?";

        // The number belongs to another program's window by now: that old request is over, this is a new one
        if (_oneOff.TryGetValue(handle, out var oneOffProcessId) && oneOffProcessId != processId)
        {
            _oneOff.Remove(handle);
        }

        if (_oneOff.Remove(handle))
        {
            RestoreWhere(t => t.Handle == handle, "one-off hotkey pressed again");
            return OneOffResult.Restored;
        }

        if (current.IsPaused)
        {
            return OneOffResult.Paused;
        }

        if (_tracked.ContainsKey(handle) || FindGame(GetEnabledGames(current), handle, processId) is not null)
        {
            return OneOffResult.AlreadyAGame;
        }

        if (processId == 0 || !manipulator.HasFrame(handle))
        {
            return OneOffResult.NoFrame;
        }

        _oneOff[handle] = processId;
        LogOneOffAdded(exe);
        return OneOffResult.Added;
    }

    /// <summary>Top-level window of a process whose exe is an enabled game. Called for window events: must stay cheap.</summary>
    public bool IsGameWindow(nint handle, AppSettings current)
    {
        if (!queries.IsTopLevel(handle))
        {
            return false;
        }

        var processId = queries.GetProcessId(handle);
        if (processId == 0)
        {
            return false;
        }

        // Opening the process for its exe path is the expensive part, so it happens once per process
        if (!_exeByProcess.TryGetValue(processId, out var exe))
        {
            exe = Path.GetFileName(queries.GetExecutablePath(processId));
            _exeByProcess[processId] = exe;
        }

        return exe is not null && GetEnabledGames(current).ByExe.ContainsKey(exe);
    }

    /// <summary>
    /// One full check: takes over the last run's windows if that's still pending, forgets closed windows, then
    /// makes the game windows borderless (or restores them when paused or turned off).
    /// </summary>
    /// <returns>The process IDs of the game and one-off windows, whose moves are worth watching.</returns>
    public HashSet<uint> Reconcile(AppSettings current)
    {
        if (_recoveryPending)
        {
            RecoverFromLastRun();
        }

        ForgetClosedWindows();
        return CheckWindows(current);
    }

    /// <summary>
    /// The game whose window is in the foreground, if mouse lock / taskbar hiding may apply to it: only for windows
    /// we made borderless; a minimized or paused game gets nothing.
    /// </summary>
    public GameProfile? GetForegroundGame(nint foreground, AppSettings current)
    {
        if (!current.IsPaused
            && _tracked.TryGetValue(foreground, out var tracked)
            && !tracked.RestorePending
            && tracked.LastResult is WindowChangeResult.Applied or WindowChangeResult.NoChange
            && !queries.IsMinimized(foreground))
        {
            return GetEnabledGames(current).ById.GetValueOrDefault(tracked.ProfileId);
        }

        return null;
    }

    /// <summary>
    /// Writes the changed windows to disk when anything about them has changed (a window changed for the first time,
    /// restored, forgotten or closed, or changed again with another placement). Cheap otherwise: it compares first.
    /// Whole records, not only the handles: a handle that was forgotten and tracked again within one check (Windows
    /// reuses the numbers of closed windows) belongs to another window now, with its own process and original state.
    /// </summary>
    /// <returns>True if the file holds the current state, false if it couldn't be written (yet).</returns>
    public bool PersistChangedWindows()
    {
        // The file still holds the last run's windows: left as it is (see _recoveryPending)
        if (_recoveryPending)
        {
            return false;
        }

        var records = _tracked.Values
            .Where(tracked => tracked.WasChanged || tracked.ChangeStarting)
            .Select(ToRecord)
            .ToList();

        if (_persistedRecords is not null && _persistedRecords.SetEquals(records))
        {
            return true;
        }

        var saved = changedWindows.Save(records);

        // Not written (file locked, disk full...): the next check tries again instead of thinking it's on disk
        _persistedRecords = saved ? [.. records] : null;
        return saved;
    }

    private static ChangedWindowRecord ToRecord(TrackedWindow tracked) => new(
        tracked.Handle,
        tracked.ProcessId,
        tracked.ProcessStartTime,
        tracked.ExecutableName,
        tracked.Original.Style,
        tracked.Original.ExStyle,
        tracked.Original.NormalRect.Left,
        tracked.Original.NormalRect.Top,
        tracked.Original.NormalRect.Right,
        tracked.Original.NormalRect.Bottom,
        tracked.Original.WasMaximized,
        tracked.CompetingArea,
        tracked.LastAppliedLook is { } look
            ? new ChangedWindowLook(look.Style, look.Bounds.Left, look.Bounds.Top, look.Bounds.Right, look.Bounds.Bottom)
            : null);

    /// <summary>
    /// Takes over the windows an earlier run changed and couldn't put back: only those that are still the same
    /// window of the same run of the game (handle, process ID and its start time if known, exe name). The first check
    /// decides what happens: restored when paused, turned off or no longer in the settings (removed while its
    /// restore was still pending), otherwise simply kept borderless and tracked, as if this app had never stopped.
    /// The others were closed since: nothing to put back.
    /// If the file can't be read yet, it is tried again on every check (see <see cref="_recoveryPending"/>); a
    /// window this run took for a game's own borderless mode in the meantime is taken over then.
    /// </summary>
    public void RecoverFromLastRun()
    {
        var records = changedWindows.Load();
        _recoveryPending = records is null;
        if (records is null)
        {
            return;
        }

        foreach (var record in records)
        {
            var handle = (nint)record.Handle;

            // Compared only when known on both sides (see ChangedWindowRecord): otherwise handle, process ID and exe
            // name decide, and all three reused together by another window is very unlikely
            if (!manipulator.Exists(handle)
                || queries.GetProcessId(handle) != record.ProcessId
                || (record.ProcessStartTime is { } recordedStart
                    && queries.GetProcessStartTime(record.ProcessId) is { } currentStart
                    && currentStart != recordedStart)
                || !string.Equals(Path.GetFileName(queries.GetExecutablePath(record.ProcessId)), record.ExecutableName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var snapshot = new WindowSnapshot
            {
                Style = record.Style,
                ExStyle = record.ExStyle,
                NormalRect = (record.Left, record.Top, record.Right, record.Bottom),
                WasMaximized = record.WasMaximized,
            };

            _tracked[handle] = new TrackedWindow(handle, record.ExecutableName, record.ProcessId, record.ProcessStartTime, snapshot)
            {
                WasChanged = true,
                CompetingArea = record.Area > 0 ? record.Area : NormalArea(snapshot),

                // Our look too, so the first check notices a game that switched to its own fullscreen or borderless
                // mode while this app wasn't running: left alone then, instead of resized (or restored by Pause)
                LastAppliedLook = record.Look is { } look
                    ? new WindowLook { Style = look.Style, Bounds = (look.Left, look.Top, look.Right, look.Bottom) }
                    : null,
            };
            _ignored.Remove(handle);
            LogRecovered(record.ExecutableName);
        }

        PersistChangedWindows();   // without the closed ones
    }

    /// <summary>
    /// Exiting: from now on a failed restore isn't logged per retry (what's left is logged once at the end, see
    /// <see cref="LogWindowsLeftForNextStart"/>). A last try at the last run's file: whatever it holds is put back
    /// now too, like every other changed window.
    /// </summary>
    public void BeginExit()
    {
        _stopping = true;
        if (_recoveryPending)
        {
            RecoverFromLastRun();
        }
    }

    /// <summary>Puts every changed window back (exit). What fails stays tracked; call again to retry.</summary>
    public void RestoreAll(string reason) => RestoreWhere(_ => true, reason);

    /// <summary>Exit: the windows that are still tracked stay in the file; the next start puts them back (or takes them over again).</summary>
    public void LogWindowsLeftForNextStart()
    {
        foreach (var tracked in _tracked.Values)
        {
            LogLeftForNextStart(tracked.ExecutableName, tracked.LastRestoreResult ?? WindowChangeResult.Failed);
        }
    }

    /// <returns>The process IDs of the game and one-off windows, whose moves are worth watching.</returns>
    private HashSet<uint> CheckWindows(AppSettings current)
    {
        var gameProcesses = new HashSet<uint>();

        // Agreed: Pause restores immediately
        if (current.IsPaused)
        {
            RestoreWhere(_ => true, "paused");
            _ignored.Clear();
            _oneOff.Clear();   // forgotten, not taken back on when resuming
            return gameProcesses;
        }

        // Windows of games that were disabled or removed since we changed them, or whose filters (exe file, window
        // title) don't take the window anymore. Asked of each window instead of looking up its game by ID: a window
        // taken over from the last run doesn't know its game yet, and another game may take it now.
        // Looked up once per window: each lookup opens the game's process for its exe path.
        var games = GetEnabledGames(current);
        var gameOf = new Dictionary<nint, GameProfile?>();
        foreach (var tracked in _tracked.Values)
        {
            var game = FindGame(games, tracked.Handle, tracked.ProcessId);
            gameOf[tracked.Handle] = game;
            if (game is not null)
            {
                tracked.ProfileId = game.Id;
            }
        }

        RestoreWhere(t => !_oneOff.ContainsKey(t.Handle) && gameOf.GetValueOrDefault(t.Handle) is null, "game disabled or removed");
        foreach (var (handle, _) in _ignored.Where(pair => !games.ById.ContainsKey(pair.Value.ProfileId)).ToList())
        {
            _ignored.Remove(handle);
        }

        if (games.InOrder.Count == 0)
        {
            HandleOneOffWindows([], current.DefaultPlacement, gameProcesses);
            return gameProcesses;
        }

        // Windows we changed take part with their size from before our change, see FindGameWindows
        var originalAreas = _tracked.Values
            .Where(t => t.WasChanged)
            .ToDictionary(t => t.Handle, t => t.CompetingArea);
        var windows = discovery.FindGameWindows(games.InOrder, originalAreas);
        foreach (var window in windows)
        {
            gameProcesses.Add((uint)window.ProcessId);
        }

        // Before handling the new main windows: a game that has two windows open hands over to the larger one
        ReleaseReplacedWindows(windows, gameProcesses);

        foreach (var window in windows)
        {
            // One misbehaving window must not stop the others
            try
            {
                HandleWindow(window, games.ById[window.ProfileId], current.DefaultPlacement);
            }
            catch (Exception ex)
            {
                LogWindowError(ex, window.ExecutableName);
            }
        }

        HandleOneOffWindows(windows, current.DefaultPlacement, gameProcesses);
        return gameProcesses;
    }

    /// <summary>The one-off windows: like a game window with the default placement, see <see cref="ToggleOneOff"/>.</summary>
    /// <param name="watchedProcesses">
    /// Gets the processes of the one-off windows that stay borderless: their moves are watched like a game's, so a
    /// program that puts its frame back is noticed right away instead of at the next poll. Added only after the
    /// main-window choice (see ReleaseReplacedWindows), which must see the games' processes alone.
    /// </param>
    private void HandleOneOffWindows(IReadOnlyList<GameWindow> gameWindows, PlacementSettings defaultPlacement,
        HashSet<uint> watchedProcesses)
    {
        foreach (var handle in _oneOff.Keys.ToList())
        {
            // A game took the window over (added or turned on since): from now on it's that game's
            if (IsOneOffGone(handle) || gameWindows.Any(w => w.Handle == handle))
            {
                _oneOff.Remove(handle);
                continue;
            }

            var processId = queries.GetProcessId(handle);
            var exe = Path.GetFileName(queries.GetExecutablePath(processId)) ?? "?";
            try
            {
                HandleWindow(new GameWindow(handle, (int)processId, exe, OneOffId),
                    new GameProfile { Id = OneOffId, ExecutableName = exe }, defaultPlacement);
            }
            catch (Exception ex)
            {
                LogWindowError(ex, exe);
            }

            // Left alone (it has no frame anymore, e.g. switched to fullscreen): nothing to keep doing for it
            if (!_tracked.ContainsKey(handle))
            {
                _oneOff.Remove(handle);
                _ignored.Remove(handle);
            }
            else
            {
                watchedProcesses.Add(processId);
            }
        }
    }

    /// <summary>The size of the window's normal (not maximized) rectangle.</summary>
    private static long NormalArea(WindowSnapshot snapshot)
    {
        var (left, top, right, bottom) = snapshot.NormalRect;
        return Math.Max(0, (long)right - left) * Math.Max(0, (long)bottom - top);
    }

    /// <summary>
    /// Only one window per game process is its main window (the largest one, see FindGameWindows). When another
    /// window of the same process takes that place (e.g. the game opens its real window next to a launcher window),
    /// the old one is put back and the new one made borderless. Only while the old one is still a visible candidate
    /// that simply lost: a main window that is hidden for a moment (loading, switching modes) keeps its snapshot,
    /// and a minimized one still counts with its normal size, so it doesn't lose its place.
    /// </summary>
    private void ReleaseReplacedWindows(IReadOnlyList<GameWindow> windows, HashSet<uint> gameProcesses)
    {
        var selected = windows.Select(w => w.Handle).ToHashSet();
        bool NotMainAnymore(nint handle, uint processId) => gameProcesses.Contains(processId) && !selected.Contains(handle);

        RestoreWhere(
            t => !_oneOff.ContainsKey(t.Handle) && NotMainAnymore(t.Handle, t.ProcessId)
                && queries.IsMainWindowCandidate(t.Handle, requireTitle: false),
            "another window of the game is its main window now");

        // A frameless window that isn't the main window anymore: no "no frame" status for it. Hidden ones too: they
        // have no snapshot to keep, and a hidden splash screen would otherwise keep the game at "no frame" after its
        // real window was made borderless. If one becomes the main window again, the next check finds it again.
        foreach (var handle in _ignored.Keys.Where(h => NotMainAnymore(h, queries.GetProcessId(h))).ToList())
        {
            _ignored.Remove(handle);
        }
    }

    private EnabledGames GetEnabledGames(AppSettings current)
    {
        if (!ReferenceEquals(current, _gamesSource))
        {
            // DistinctBy guards against the same game being configured twice (same exe and filters, or same ID)
            var inOrder = current.Games
                .Where(g => g.Enabled && !string.IsNullOrWhiteSpace(g.ExecutableName))
                .DistinctBy(GameMatcher.Key)
                .DistinctBy(g => g.Id)
                .ToList();
            _games = new EnabledGames(
                inOrder.ToDictionary(g => g.Id),
                inOrder,
                inOrder.GroupBy(g => g.ExecutableName.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase));
            _gamesSource = current;
        }

        return _games;
    }

    /// <summary>The enabled game that takes this window (exe name, exe file, title), or null; see GameMatcher.</summary>
    private GameProfile? FindGame(EnabledGames games, nint handle, uint processId)
    {
        var path = queries.GetExecutablePath(processId);
        var exe = Path.GetFileName(path);
        return exe is not null && games.ByExe.TryGetValue(exe, out var candidates)
            ? GameMatcher.FindBest(candidates, exe, path, () => queries.GetWindowTitle(handle))
            : null;
    }

    private void HandleWindow(GameWindow window, GameProfile game, PlacementSettings defaultPlacement)
    {
        if (!_tracked.TryGetValue(window.Handle, out var tracked))
        {
            // Agreed rule: only windows that still have a frame. Frameless ones are probably
            // (exclusive) fullscreen and could crash when resized. Checked again on every check,
            // so switching the game to windowed mode later is picked up.
            if (!manipulator.HasFrame(window.Handle))
            {
                if (!_ignored.ContainsKey(window.Handle))
                {
                    LogIgnoredFrameless(window.ExecutableName);
                }

                _ignored[window.Handle] = (window.ExecutableName, game.Id);
                return;
            }

            // The original state is captured exactly once, before our first change
            if (manipulator.Capture(window.Handle) is not { } snapshot)
            {
                return;   // closed in the meantime
            }

            tracked = new TrackedWindow(window.Handle, window.ExecutableName, (uint)window.ProcessId,
                queries.GetProcessStartTime((uint)window.ProcessId), snapshot);
            _tracked[window.Handle] = tracked;
            _ignored.Remove(window.Handle);
        }
        else if (GameWentFrameless(window.Handle, tracked))
        {
            // Same rule as for a new window: a game that removes its frame itself is left alone. Before the fight
            // limit, so this also ends a "fight" with a game that now insists on its own fullscreen size.
            ForgetGameWentFrameless(window.Handle, tracked, "check");
            return;
        }

        tracked.ProfileId = game.Id;

        var now = UtcNow;
        if (now < tracked.PausedUntil)
        {
            return;   // fight limit active
        }

        // The game's own placement or the default, including which display
        var effective = game.Placement ?? defaultPlacement;
        var (monitor, found) = PlacementCalculator.ResolveDisplay(
            effective, _displays, display.GetMonitorForWindow(window.Handle));

        if (!found && effective.DisplayId is { } missingId && _missingDisplaysLogged.Add(missingId))
        {
            LogDisplayMissing(window.ExecutableName, effective.DisplayName ?? missingId);
        }

        // Handled normally again (e.g. resumed while a restore was still being retried); the snapshot stays the original
        tracked.RestorePending = false;
        tracked.LastRestoreResult = null;

        // The size this window just won the main-window choice with, taken while it is still the game's own: from
        // now on it takes part with that (see MainWindowRanking). Not the snapshot's normal size, which is
        // smaller for a maximized window: it would lose to the game's other window after our change and win again
        // once restored, forever. Taken again until a change actually happened (a minimized window is skipped).
        if (!tracked.WasChanged)
        {
            tracked.CompetingArea = queries.GetWindowArea(window.Handle);
        }

        // The game's own size: read every time, so a resolution changed in the game is followed (not fought)
        var gameSize = effective.Mode == PlacementMode.GameSize ? manipulator.GetClientSize(window.Handle) : null;
        var placement = PlacementCalculator.Resolve(game.Placement, defaultPlacement, monitor, gameSize);
        // The first change of this window: its original state goes to disk *before* the change. Written only after
        // it, a crash in between would leave a frameless window that the next start takes for the game's own mode.
        // A minimized or not responding window isn't changed yet: no record either (that would write the file twice
        // on every check until it can be changed). Skipped right here, not left to the manipulator's own check: a
        // window that responds again (or is opened) in between would be changed without its record on disk.
        // Not on disk (file locked, folder not writable, last run's file not read yet): no change at all, the next
        // check tries again. Shown as "failed" meanwhile, so it doesn't go unnoticed if it never works.
        if (!tracked.WasChanged && queries.IsMinimized(window.Handle))
        {
            tracked.LastResult = WindowChangeResult.Skipped;
            return;
        }

        if (!tracked.WasChanged && queries.IsHung(window.Handle))
        {
            tracked.LastResult = WindowChangeResult.NotResponding;
            return;
        }

        if (!tracked.WasChanged)
        {
            tracked.ChangeStarting = true;
            if (!PersistChangedWindows())
            {
                tracked.ChangeStarting = false;
                if (tracked.LastResult != WindowChangeResult.Failed)
                {
                    LogNotRecorded(window.ExecutableName);
                }

                tracked.LastResult = WindowChangeResult.Failed;
                return;
            }
        }

        var result = manipulator.MakeBorderless(window.Handle, placement, monitor, out var modified, out var error);
        tracked.ChangeStarting = false;   // nothing changed after all: the check's last write drops it again

        // Also when only part of the change worked: that window needs restoring as well
        if (modified)
        {
            tracked.WasChanged = true;
        }

        // How the window looks with our change, to notice later when the game switches its mode itself
        if (result is WindowChangeResult.Applied or WindowChangeResult.NoChange)
        {
            tracked.LastAppliedLook = manipulator.CaptureLook(window.Handle, placement, monitor) ?? tracked.LastAppliedLook;
        }

        // Log changes only, otherwise every check would add a line. A failure with a new cause is a change too.
        var newFailure = tracked.LastResult != result || tracked.LastError != error;
        switch (result)
        {
            case WindowChangeResult.Applied:
                LogApplied(window.ExecutableName, placement.Width, placement.Height,
                    monitor.Left + placement.OffsetX, monitor.Top + placement.OffsetY,
                    monitor.Width, monitor.Height, monitor.Left, monitor.Top);
                CheckFightLimit(tracked, now);
                break;
            case WindowChangeResult.AccessDenied when newFailure:
                LogAccessDenied(window.ExecutableName, error?.Operation, modified);
                break;
            case WindowChangeResult.Failed when newFailure:
                LogFailed(window.ExecutableName, error?.Operation, error?.Win32Error, modified);
                break;
        }

        tracked.LastResult = result;
        tracked.LastError = error;
    }

    private void CheckFightLimit(TrackedWindow tracked, DateTime now)
    {
        // Sliding window: keep only the applies of the last FightWindow
        tracked.RecentApplies.Enqueue(now);
        while (tracked.RecentApplies.Peek() < now - FightWindow)
        {
            tracked.RecentApplies.Dequeue();
        }

        if (tracked.RecentApplies.Count > MaxApplies)
        {
            tracked.PausedUntil = now + FightPause;
            tracked.RecentApplies.Clear();
            LogFightLimit(tracked.ExecutableName, MaxApplies, (int)FightWindow.TotalSeconds, (int)FightPause.TotalSeconds);
        }
    }

    /// <summary>
    /// Puts matching windows back to their original state and stops tracking them. A window that couldn't be
    /// restored (not responding, or Windows refused) keeps its snapshot and is tried again on every check,
    /// until it works or the window is closed. On exit it stays in the file of changed windows for the next start.
    /// </summary>
    private void RestoreWhere(Func<TrackedWindow, bool> predicate, string reason)
    {
        // ToList: _tracked is modified inside the loop
        foreach (var (handle, tracked) in _tracked.Where(pair => predicate(pair.Value)).ToList())
        {
            // Never actually changed (e.g. access denied right away): nothing to put back
            if (!tracked.WasChanged)
            {
                _tracked.Remove(handle);
                continue;
            }

            // Closed, or its handle already belongs to another program: the snapshot must never be put on that one
            if (IsGone(handle, tracked))
            {
                _tracked.Remove(handle);
                continue;
            }

            // The game switched to its own fullscreen/borderless mode since: the snapshot describes the old mode,
            // and putting it back would give that window a frame it shouldn't have
            if (GameWentFrameless(handle, tracked))
            {
                ForgetGameWentFrameless(handle, tracked, reason);
                continue;
            }

            WindowChangeResult result;
            WindowChangeError? error;
            try
            {
                result = manipulator.Restore(handle, tracked.Original, out error);
            }
            catch (Exception ex)
            {
                LogWindowError(ex, tracked.ExecutableName);
                result = WindowChangeResult.Failed;
                error = null;
            }

            var done = result is WindowChangeResult.Applied or WindowChangeResult.NoChange
                || IsGone(handle, tracked);

            if (done)
            {
                _tracked.Remove(handle);
                LogRestored(tracked.ExecutableName, reason, result);
                continue;
            }

            // Log once per kind of problem, not on every retry. Skipped and not responding are expected to pass.
            // On exit, what's left is logged once at the end instead (see LogWindowsLeftForNextStart).
            if (!_stopping && result is not (WindowChangeResult.Skipped or WindowChangeResult.NotResponding)
                && (tracked.LastRestoreResult != result || tracked.LastRestoreError != error))
            {
                LogRestoreRetrying(tracked.ExecutableName, reason, result, error?.Operation, error?.Win32Error);
            }

            tracked.RestorePending = true;
            tracked.LastRestoreResult = result;
            tracked.LastRestoreError = error;
        }
    }

    /// <summary>
    /// The game has switched the window to its own fullscreen or borderless mode since our change (e.g. "windowed
    /// fullscreen" picked in the game while it was tracked). Needs more than a changed style bit, see
    /// <see cref="IWindowManipulator.GameSwitchedMode"/>: forgetting a window we changed means Pause and exit can't
    /// put it back anymore, so that only happens when the game clearly took over.
    /// </summary>
    private bool GameWentFrameless(nint handle, TrackedWindow tracked) =>
        tracked.LastAppliedLook is { } ours && manipulator.GameSwitchedMode(handle, ours);

    /// <summary>
    /// Stops tracking without restoring (the snapshot is outdated) and treats the window like any frameless one:
    /// left alone, status "no frame", and taken over again if it gets a frame back (switched to windowed).
    /// </summary>
    private void ForgetGameWentFrameless(nint handle, TrackedWindow tracked, string when)
    {
        _tracked.Remove(handle);
        _ignored[handle] = (tracked.ExecutableName, tracked.ProfileId);
        LogGameWentFrameless(tracked.ExecutableName, when);
    }

    /// <summary>
    /// Closed, or the handle was reused: Windows hands out the number of a destroyed window again, and with a
    /// missed "destroyed" event the old snapshot would otherwise stick to an unrelated window.
    /// </summary>
    private bool IsGone(nint handle, TrackedWindow tracked) =>
        !manipulator.Exists(handle) || queries.GetProcessId(handle) != tracked.ProcessId;

    private void ForgetClosedWindows()
    {
        // Normally the "destroyed" event already did this; this catches events that were missed.
        // Existence instead of "still found": a game that was just disabled isn't found anymore,
        // but the restore still needs its snapshot.
        // A reused handle: everything known about the old window goes, not only its snapshot (the one-off request
        // would otherwise make the new owner borderless)
        foreach (var (handle, _) in _tracked.Where(pair => IsGone(pair.Key, pair.Value)).ToList())
        {
            _tracked.Remove(handle);
            _ignored.Remove(handle);
            _oneOff.Remove(handle);
        }

        foreach (var handle in _ignored.Keys.Where(h => !manipulator.Exists(h)).ToList())
        {
            _ignored.Remove(handle);
        }

        foreach (var handle in _oneOff.Keys.Where(IsOneOffGone).ToList())
        {
            _oneOff.Remove(handle);
        }
    }

    /// <summary>The one-off window is closed, or its handle belongs to another process now.</summary>
    private bool IsOneOffGone(nint handle) =>
        !_oneOff.TryGetValue(handle, out var processId)
        || !manipulator.Exists(handle) || queries.GetProcessId(handle) != processId;

    /// <summary>A fresh status per game from the tracked and ignored windows. Never modified afterwards.</summary>
    public Dictionary<Guid, GameWindowStatus> BuildStatuses()
    {
        var statuses = new Dictionary<Guid, GameWindowStatus>();

        void Report(Guid game, GameWindowStatus status)
        {
            // A window taken over from the last run whose game isn't known yet, or a one-off window: no game to show it under
            if (game == Guid.Empty || game == OneOffId)
            {
                return;
            }

            // Several windows/processes of one game: the most severe status wins
            if (!statuses.TryGetValue(game, out var existing) || status > existing)
            {
                statuses[game] = status;
            }
        }

        var now = UtcNow;
        foreach (var tracked in _tracked.Values.Where(t => !t.RestorePending))
        {
            GameWindowStatus? status = now < tracked.PausedUntil
                ? GameWindowStatus.FightPaused
                : tracked.LastResult switch
                {
                    WindowChangeResult.Applied or WindowChangeResult.NoChange => GameWindowStatus.Borderless,
                    WindowChangeResult.NotResponding => GameWindowStatus.NotResponding,
                    WindowChangeResult.AccessDenied => GameWindowStatus.AccessDenied,
                    WindowChangeResult.Failed => GameWindowStatus.Failed,
                    _ => null,   // e.g. minimized: nothing to say yet
                };

            if (status is { } s)
            {
                Report(tracked.ProfileId, s);
            }
        }

        foreach (var (_, game) in _ignored.Values)
        {
            Report(game, GameWindowStatus.NoFrame);
        }

        return statuses;
    }

    private sealed class TrackedWindow(nint handle, string executableName, uint processId, long? processStartTime, WindowSnapshot original)
    {
        public nint Handle { get; } = handle;
        public string ExecutableName { get; } = executableName;
        public uint ProcessId { get; } = processId;

        /// <summary>The game it belongs to; Guid.Empty for a window taken over from the last run, until the first check.</summary>
        public Guid ProfileId { get; set; }

        /// <summary>Identifies this run of the game on disk (see ChangedWindowsStore); null = unknown, not stored then.</summary>
        public long? ProcessStartTime { get; } = processStartTime;
        public WindowSnapshot Original { get; } = original;
        public WindowChangeResult? LastResult { get; set; }

        /// <summary>What failed in the last change, so a retry that fails the same way isn't logged again.</summary>
        public WindowChangeError? LastError { get; set; }

        /// <summary>How the window looked after our last successful change; null until then.</summary>
        public WindowLook? LastAppliedLook { get; set; }

        /// <summary>
        /// The window's size right before our first change (a maximized window with its maximized size). Once changed,
        /// it takes part in choosing the game's main window with this, see MainWindowRanking.
        /// </summary>
        public long CompetingArea { get; set; }

        /// <summary>True once we actually changed the window (even partly), i.e. there is something to restore.</summary>
        public bool WasChanged { get; set; }

        /// <summary>The first change is about to happen: already written to disk, see HandleWindow.</summary>
        public bool ChangeStarting { get; set; }

        /// <summary>
        /// Should be restored (paused, game disabled), but that failed so far; retried on every check.
        /// Such a window counts as neither borderless nor for mouse lock / taskbar hiding.
        /// </summary>
        public bool RestorePending { get; set; }

        /// <summary>Result of the last failed restore, so a retry that fails the same way isn't logged again.</summary>
        public WindowChangeResult? LastRestoreResult { get; set; }

        /// <summary>What failed in the last failed restore, see <see cref="LastRestoreResult"/>.</summary>
        public WindowChangeError? LastRestoreError { get; set; }

        /// <summary>Times of our recent changes, for the fight limit.</summary>
        public Queue<DateTime> RecentApplies { get; } = new();

        public DateTime PausedUntil { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: borderless once requested with the hotkey")]
    private partial void LogOneOffAdded(string exe);

    // Screen coordinates in physical pixels, the display by its bounds: names aren't unique, and this is what was used
    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: made borderless, {Width}x{Height} at {X},{Y} on the {DisplayWidth}x{DisplayHeight} display at {DisplayLeft},{DisplayTop}")]
    private partial void LogApplied(string exe, int width, int height, int x, int y, int displayWidth, int displayHeight, int displayLeft, int displayTop);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: selected display \"{Display}\" is not connected, using the primary display")]
    private partial void LogDisplayMissing(string exe, string display);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: restored ({Reason}) -> {Result}")]
    private partial void LogRestored(string exe, string reason, WindowChangeResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: restoring ({Reason}) failed with {Result} at {Operation}, Win32 error {Win32Error}; trying again on the next check")]
    private partial void LogRestoreRetrying(string exe, string reason, WindowChangeResult result, string? operation, int? win32Error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: could not be restored on exit ({Result}); it is put back at the next start if it still runs then and is off or paused")]
    private partial void LogLeftForNextStart(string exe, WindowChangeResult result);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: window changed by the last run found again, tracked as before")]
    private partial void LogRecovered(string exe);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: window has no frame (fullscreen?), not touched")]
    private partial void LogIgnoredFrameless(string exe);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Exe}: the game switched to its own fullscreen or borderless mode, left as it is and not restored ({When})")]
    private partial void LogGameWentFrameless(string exe, string when);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: access denied at {Operation}, the game probably runs as administrator (window partly changed: {PartlyChanged})")]
    private partial void LogAccessDenied(string exe, string? operation, bool partlyChanged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: changing the window failed at {Operation}, Win32 error {Win32Error} (window partly changed: {PartlyChanged})")]
    private partial void LogFailed(string exe, string? operation, int? win32Error, bool partlyChanged);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: not changed yet, its original state can't be saved for a restore after a crash; trying again")]
    private partial void LogNotRecorded(string exe);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Exe}: the game undid our change more than {Max} times in {Window} s, pausing for {Pause} s")]
    private partial void LogFightLimit(string exe, int max, int window, int pause);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Exe}: error while handling the window")]
    private partial void LogWindowError(Exception ex, string exe);
}
