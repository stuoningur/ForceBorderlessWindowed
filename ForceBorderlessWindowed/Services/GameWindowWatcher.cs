using System.Windows.Threading;

using ForceBorderlessWindowed.Models;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

using Windows.Win32;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Runs <see cref="WindowTracker"/>, which finds the windows of configured games and makes them borderless.
/// This class only decides *when*: it reacts to window events (shown / moved / destroyed) within a fraction of a
/// second, and a slow poll catches anything the events missed. It also applies the mouse lock and taskbar hiding.
/// Runs on its own thread: Win32 calls on another process's window wait for that process,
/// so a game that hangs while loading must not freeze the tray or the settings window.
/// Everything below (and the tracker) runs on that thread only, so the fields need no locking.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The hooks must be removed on the watcher thread itself; that happens when its message loop ends (StopAsync).")]
internal sealed partial class GameWindowWatcher(
    ISettingsService settings,
    WindowTracker tracker,
    ILogger<GameWindowWatcher> logger) : IHostedService, IGameStatusProvider, IOneOffWindows
{
    /// <summary>Safety net only: the window events normally trigger a check much earlier.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Wait for this much quiet after the last relevant event before checking. Games often resize
    /// several times while starting up; this way we act once, after they're done.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    /// <summary>How long exiting waits for the game windows to be restored, in case a game hangs.</summary>
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// On exit, a game that isn't responding is skipped by the restore. It often is again a moment later (busy
    /// loading or saving): retried for this long, within <see cref="RestoreTimeout"/>.
    /// </summary>
    private static readonly TimeSpan ExitRetryTime = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ExitRetryInterval = TimeSpan.FromMilliseconds(200);

    private readonly ILogger<GameWindowWatcher> _logger = logger;

    /// <summary>
    /// Latest status per game for the UI. Replaced as a whole (never modified) so other threads can read it
    /// without locking; volatile makes sure they see the newest reference.
    /// </summary>
    private volatile Dictionary<Guid, GameWindowStatus> _statuses = [];

    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _debounceTimer;

    /// <summary>The window events; null if they couldn't be set up. Created, used and disposed on the watcher thread.</summary>
    private WindowEventHooks? _hooks;

    // Mouse lock and taskbar hiding; the timer re-checks them while they're active
    private ForegroundEffects? _effects;
    private DispatcherTimer? _effectsTimer;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _thread = new Thread(() =>
        {
            // The Dispatcher gives this thread a message loop (the window events arrive through it),
            // timers, and a way for other threads to queue work (BeginInvoke)
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Safety net: an exception here would otherwise terminate the whole app. Only covers work that runs
            // inside Dispatcher.Run, hence the try/catch around the setup below.
            _dispatcher.UnhandledException += (_, e) =>
            {
                LogUnexpectedError(e.Exception);
                e.Handled = true;
            };

            try
            {
                SetUpOnWatcherThread();
            }
            catch (Exception ex)
            {
                // Not thrown on: on a background thread that would end the process without a word. The app's
                // startup awaits this task and reports the failure instead.
                LogStartFailed(ex);
                _effects?.Stop();
                _hooks?.Dispose();
                _hooks = null;
                started.TrySetException(ex);
                return;
            }

            started.SetResult();
            Dispatcher.Run();   // runs until StopAsync calls InvokeShutdown

            // Still on the same thread, as UnhookWinEvent requires
            _hooks?.Dispose();
        })
        {
            Name = "Game window watcher",
            IsBackground = true,   // never keeps the process alive on its own
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        settings.SettingsChanged += OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        LogStarted();
        return started.Task;
    }

    /// <summary>Everything the watcher thread needs before its message loop runs. On the watcher thread.</summary>
    private void SetUpOnWatcherThread()
    {
        try
        {
            _hooks = new WindowEventHooks(OnWindowEvent);
        }
        catch (InvalidOperationException ex)
        {
            // Not fatal: the poll alone still works, just slower
            LogHooksFailed(ex);
        }

        _debounceTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = Debounce };
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            ReconcileGameWindows();
        };

        // A taskbar can only still be hidden here if the app crashed or was killed last time.
        // Undoes only what we had recorded as applied, nothing another program did.
        ForegroundEffects.UndoRecorded();
        _effects = new ForegroundEffects();
        _effectsTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = TimeSpan.FromMilliseconds(500) };
        _effectsTimer.Tick += (_, _) => UpdateForegroundEffects();

        // Windows we changed in an earlier run that ended without putting them back (crash, or a game that
        // didn't respond on exit). Queued before the first check (same priority, so it runs first), which then
        // treats them like any tracked window. Queued instead of called here: inside the message loop, an
        // unexpected error is caught by the safety net and the watcher keeps running.
        _dispatcher!.BeginInvoke(tracker.RecoverFromLastRun);

        _pollTimer = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Poll(), _dispatcher);
        _dispatcher.BeginInvoke(Poll);   // first check right away
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        settings.SettingsChanged -= OnSettingsChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        // Mouse and taskbar first, from this thread: they must never depend on the watcher thread,
        // which may be stuck waiting for a hung game right now
        _effects?.Stop();

        if (_dispatcher is null || _thread is not { IsAlive: true })
        {
            return Task.CompletedTask;
        }

        // Agreed: exiting the app puts every game window back the way it was.
        // Not Dispatcher.Invoke with a timeout: that timeout only limits the wait for the work to *start*;
        // once it runs, Invoke waits for it without a limit. Waiting on the operation's task is really bounded.
        var restore = _dispatcher.InvokeAsync(() =>
        {
            tracker.BeginExit();
            tracker.RestoreAll("app exit");

            // Not responding right now (skipped) or refused: a few more tries while there's time
            var deadline = DateTime.UtcNow + ExitRetryTime;
            while (tracker.HasTrackedWindows && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(ExitRetryInterval);
                tracker.RestoreAll("app exit");
            }

            // Still in the file: the next start puts them back (or takes them over again)
            tracker.LogWindowsLeftForNextStart();
            tracker.PersistChangedWindows();
        }, DispatcherPriority.Send, CancellationToken.None);   // own time limit below, not the host's token

        var restored = false;
        try
        {
            restored = restore.Task.Wait((int)RestoreTimeout.TotalMilliseconds, CancellationToken.None);
        }
        catch (AggregateException ex)
        {
            LogUnexpectedError(ex.InnerException ?? ex);
        }

        if (!restored)
        {
            LogRestoreTimeout();
        }

        // Ends Dispatcher.Run() on the watcher thread, which then removes the hooks. Begin..., because
        // InvokeShutdown waits for the watcher thread, and a hung game may be blocking it.
        _dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);

        // Bounded: if a hung game blocks the thread, the app still exits (background thread).
        // After a timed-out restore the thread is stuck anyway, so no point waiting long.
        _thread.Join(restored ? TimeSpan.FromSeconds(3) : TimeSpan.FromMilliseconds(500));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saved in the settings window or Pause toggled in the tray. Raised on the UI thread,
    /// so the work is handed over to the watcher thread. The check then applies new placements,
    /// restores windows of games that were disabled/removed, or restores everything when paused.
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher?.BeginInvoke(() =>
    {
        tracker.ResetFightLimits();
        ReconcileGameWindows();
    });

    /// <summary>Called for every window event on the system: must stay cheap.</summary>
    private void OnWindowEvent(WindowEventKind kind, nint handle)
    {
        if (kind == WindowEventKind.Destroyed)
        {
            if (tracker.ForgetDestroyed(handle))
            {
                PublishStatuses();
                UpdateForegroundEffects();
                tracker.PersistChangedWindows();
            }
            return;
        }

        var isTracked = tracker.IsTracked(handle);

        // Mouse lock / taskbar react immediately (no debounce): alt-tab must release the mouse at once.
        // Moves of a tracked window too, so the mouse area follows the window.
        if (kind is WindowEventKind.Foreground or WindowEventKind.MinimizeChanged
            || (kind == WindowEventKind.Moved && isTracked))
        {
            UpdateForegroundEffects();
        }

        // Our own SetWindowPos also ends up here; the check then finds nothing to do (NoChange)
        if (isTracked || tracker.IsGameWindow(handle, settings.Current))
        {
            // Restart the timer: the check runs once things have been quiet for the debounce time
            _debounceTimer?.Stop();
            _debounceTimer?.Start();
        }
    }

    /// <summary>
    /// Monitor plugged in/out, resolution or arrangement changed: games may need to move right away.
    /// Raised on a SystemEvents thread, so the work is handed over to the watcher thread.
    /// </summary>
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _dispatcher?.BeginInvoke(() =>
    {
        tracker.RefreshDisplays();
        ReconcileGameWindows();
    });

    private void Poll()
    {
        tracker.ForgetProcessCache();
        tracker.RefreshDisplays();
        ReconcileGameWindows();
    }

    public IReadOnlyDictionary<Guid, GameWindowStatus> GetStatuses() => _statuses;

    public void ToggleOneOff(nint windowHandle, Action<OneOffResult> done) => _dispatcher?.BeginInvoke(() =>
    {
        var result = tracker.ToggleOneOff(windowHandle, settings.Current);
        ReconcileGameWindows();   // right away, not after the debounce: the user is waiting for it
        done(tracker.ConfirmOneOff(windowHandle, result));   // what actually happened, not only what was asked
    });

    public event EventHandler? StatusesChanged;

    private void ReconcileGameWindows()
    {
        // Current is an immutable record that Save swaps as a whole, so reading it here is safe
        var gameProcesses = tracker.Reconcile(settings.Current);
        _hooks?.WatchMovesIn(gameProcesses);
        PublishStatuses();
        UpdateForegroundEffects();   // settings, pause or the window state may have changed
        tracker.PersistChangedWindows();
    }

    /// <summary>Applies or releases mouse lock and taskbar hiding for whatever window is in the foreground now.</summary>
    private void UpdateForegroundEffects()
    {
        if (_effects is null)
        {
            return;
        }

        var foreground = (nint)PInvoke.GetForegroundWindow();
        var game = tracker.GetForegroundGame(foreground, settings.Current);

        _effects.Update(game is null ? 0 : foreground, game?.LockCursor == true, game?.HideTaskbar == true,
            game?.BlackBackground == true);

        // Windows resets the mouse clip now and then (e.g. UAC prompt, some games): re-check while active
        if (_effectsTimer is not null)
        {
            _effectsTimer.IsEnabled = _effects.IsActive;
        }
    }

    /// <summary>Takes a fresh status snapshot from the tracker; tells the UI only if something changed.</summary>
    private void PublishStatuses()
    {
        var statuses = tracker.BuildStatuses();
        var changed = statuses.Count != _statuses.Count
            || statuses.Any(pair => !_statuses.TryGetValue(pair.Key, out var old) || old != pair.Value);
        _statuses = statuses;

        if (changed)
        {
            StatusesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watcher started")]
    private partial void LogStarted();

    [LoggerMessage(Level = LogLevel.Critical, Message = "The watcher could not start")]
    private partial void LogStartFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Window events unavailable, falling back to polling only")]
    private partial void LogHooksFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Restoring windows on exit timed out (a game is not responding)")]
    private partial void LogRestoreTimeout();

    [LoggerMessage(Level = LogLevel.Error, Message = "Unexpected error in the watcher")]
    private partial void LogUnexpectedError(Exception ex);
}
