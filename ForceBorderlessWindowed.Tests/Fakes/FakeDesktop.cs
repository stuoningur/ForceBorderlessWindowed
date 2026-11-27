using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests.Fakes;

/// <summary>A game window on the <see cref="FakeDesktop"/>. Tests change its fields to play the game's part.</summary>
internal sealed class FakeWindow
{
    public required nint Handle { get; init; }
    public required uint ProcessId { get; set; }
    public required string ExecutableName { get; init; }
    /// <summary>null: Windows won't tell (e.g. an anti-cheat that blocks opening the game's process).</summary>
    public long? ProcessStartTime { get; set; } = 1000;
    public string Title { get; set; } = "";

    /// <summary>Folder of the exe: two programs with the same exe name in different folders.</summary>
    public string Folder { get; set; } = FakeDesktop.GamesFolder;

    public string ExecutablePath => Folder + ExecutableName;

    public bool Exists { get; set; } = true;
    public bool Visible { get; set; } = true;
    public bool HasFrame { get; set; } = true;
    public bool Minimized { get; set; }

    /// <summary>"Not responding": the manipulator skips it, like the real one does for hung windows.</summary>
    public bool Hung { get; set; }

    /// <summary>
    /// Stops being hung or minimized right after the first query that reported it: the game responds again, or the
    /// user opens it, between the tracker's check and the manipulator's own one.
    /// </summary>
    public bool WakesUpAfterQuery { get; set; }

    /// <summary>The game runs as administrator: every change is refused.</summary>
    public bool AccessDenied { get; set; }

    /// <summary>The game switched to its own fullscreen/borderless mode since our change.</summary>
    public bool SwitchedOwnMode { get; set; }

    public (int Left, int Top, int Right, int Bottom) Rect { get; set; } = (100, 100, 1380, 820);

    public long Area => (long)(Rect.Right - Rect.Left) * (Rect.Bottom - Rect.Top);
}

/// <summary>
/// A pretend desktop with game windows: plays the part of Win32 for <see cref="WindowTracker"/> (manipulator,
/// queries, the window finder and the displays). Keeps a count of the changes, so tests can tell whether the
/// tracker acted.
/// </summary>
internal sealed class FakeDesktop : IWindowManipulator, IWindowQueries, IProcessDiscoveryService, IDisplayService
{
    public const string GamesFolder = @"C:\Games\";

    private readonly List<FakeWindow> _windows = [];

    public List<DisplayInfo> Displays { get; } =
    [
        new DisplayInfo("monitor-1", "Monitor 1", 1, new MonitorBounds(0, 0, 3440, 1440), IsPrimary: true),
    ];

    public int MakeBorderlessCalls { get; private set; }

    /// <summary>Called right before a window is actually changed: lets a test look at the state at that moment.</summary>
    public Action<nint>? BeforeChange { get; set; }
    public int RestoreCalls { get; private set; }

    public FakeWindow AddWindow(nint handle, uint processId, string exe, bool hasFrame = true)
    {
        var window = new FakeWindow { Handle = handle, ProcessId = processId, ExecutableName = exe, HasFrame = hasFrame };
        _windows.Add(window);
        return window;
    }

    private FakeWindow? Find(nint handle) => _windows.FirstOrDefault(w => w.Handle == handle && w.Exists);

    private FakeWindow? FindProcess(uint processId) => _windows.FirstOrDefault(w => w.ProcessId == processId && w.Exists);

    // ---- IWindowManipulator ----

    public bool Exists(nint windowHandle) => Find(windowHandle) is not null;

    public bool HasFrame(nint windowHandle) => Find(windowHandle)?.HasFrame == true;

    /// <summary>Title bar 31 px, borders 8 px, like a default Windows 11 frame at 100 %.</summary>
    public (int Width, int Height)? GetClientSize(nint windowHandle) => Find(windowHandle) is { } w
        ? w.HasFrame
            ? (w.Rect.Right - w.Rect.Left - 16, w.Rect.Bottom - w.Rect.Top - 39)
            : (w.Rect.Right - w.Rect.Left, w.Rect.Bottom - w.Rect.Top)
        : null;

    public WindowSnapshot? Capture(nint windowHandle) => Find(windowHandle) is { } w
        ? new WindowSnapshot { Style = w.HasFrame ? 0x00CF0000u : 0, NormalRect = w.Rect }
        : null;

    public WindowChangeResult MakeBorderless(nint windowHandle, WindowRect placement, MonitorBounds monitor, out bool modified)
    {
        modified = false;
        if (Find(windowHandle) is not { } w || w.Minimized)
        {
            return WindowChangeResult.Skipped;
        }

        // Same order as the real one: "nothing to do" only reads, so it works on a hung window too
        var target = Target(placement, monitor);
        if (!w.HasFrame && w.Rect == target)
        {
            return WindowChangeResult.NoChange;
        }

        if (w.Hung)
        {
            return WindowChangeResult.NotResponding;
        }

        if (w.AccessDenied)
        {
            return WindowChangeResult.AccessDenied;
        }

        BeforeChange?.Invoke(windowHandle);
        MakeBorderlessCalls++;
        w.HasFrame = false;
        w.Rect = target;
        modified = true;
        return WindowChangeResult.Applied;
    }

    public WindowLook? CaptureLook(nint windowHandle, WindowRect placement, MonitorBounds monitor) =>
        Exists(windowHandle) ? new WindowLook { Style = 0, Bounds = Target(placement, monitor) } : null;

    public bool GameSwitchedMode(nint windowHandle, WindowLook look) => Find(windowHandle) is { SwitchedOwnMode: true, HasFrame: false };

    public WindowChangeResult Restore(nint windowHandle, WindowSnapshot snapshot)
    {
        if (Find(windowHandle) is not { } w)
        {
            return WindowChangeResult.Skipped;
        }

        if (w.Hung)
        {
            return WindowChangeResult.NotResponding;
        }

        RestoreCalls++;
        w.HasFrame = true;
        w.Rect = snapshot.NormalRect;
        return WindowChangeResult.Applied;
    }

    private static (int, int, int, int) Target(WindowRect placement, MonitorBounds monitor)
    {
        var left = monitor.Left + placement.OffsetX;
        var top = monitor.Top + placement.OffsetY;
        return (left, top, left + placement.Width, top + placement.Height);
    }

    // ---- IWindowQueries ----

    public uint GetProcessId(nint windowHandle) => Find(windowHandle)?.ProcessId ?? 0;

    public long? GetProcessStartTime(uint processId) => FindProcess(processId)?.ProcessStartTime;

    public string? GetExecutablePath(uint processId) => FindProcess(processId)?.ExecutablePath;

    public string GetWindowTitle(nint windowHandle) => Find(windowHandle)?.Title ?? "";

    public bool IsMainWindowCandidate(nint windowHandle, bool requireTitle) => Find(windowHandle) is { Visible: true };

    public long GetWindowArea(nint windowHandle) => Find(windowHandle)?.Area ?? 0;

    public bool IsTopLevel(nint windowHandle) => true;

    public bool IsMinimized(nint windowHandle)
    {
        if (Find(windowHandle) is not { Minimized: true } w)
        {
            return false;
        }

        w.Minimized = !w.WakesUpAfterQuery;
        return true;
    }

    public bool IsHung(nint windowHandle)
    {
        if (Find(windowHandle) is not { Hung: true } w)
        {
            return false;
        }

        w.Hung = !w.WakesUpAfterQuery;
        return true;
    }

    // ---- IProcessDiscoveryService: same choice as the real one (largest visible window per process) ----

    public IReadOnlyList<RunningProgram> GetRunningPrograms() => [];

    public IReadOnlyList<GameWindow> FindGameWindows(
        IReadOnlyCollection<GameProfile> games, IReadOnlyDictionary<nint, long>? originalAreas = null)
    {
        var best = new Dictionary<uint, (FakeWindow Window, long Area, bool Resized, Guid ProfileId)>();

        foreach (var w in _windows.Where(w => w.Exists && w.Visible))
        {
            if (GameMatcher.FindBest(games, w.ExecutableName, w.ExecutablePath, () => w.Title) is not { } game)
            {
                continue;
            }

            var (area, resized) = MainWindowRanking.Measure(w.Handle, w.Area, originalAreas);
            if (!best.TryGetValue(w.ProcessId, out var current)
                || MainWindowRanking.Beats((area, resized), (current.Area, current.Resized)))
            {
                best[w.ProcessId] = (w, area, resized, game.Id);
            }
        }

        return [.. best.Values.Select(b => new GameWindow(b.Window.Handle, (int)b.Window.ProcessId, b.Window.ExecutableName, b.ProfileId))];
    }

    // ---- IDisplayService ----

    public MonitorBounds GetMonitorForWindow(nint windowHandle) => Displays[0].Bounds;

    public IReadOnlyList<DisplayInfo> GetDisplays() => Displays;
}

/// <summary>The file of changed windows, in memory. <see cref="Unreadable"/> plays a locked file.</summary>
internal sealed class FakeChangedWindowsStore : IChangedWindowsStore
{
    public List<ChangedWindowRecord> Records { get; set; } = [];
    public bool Unreadable { get; set; }

    /// <summary>Plays a file that can't be written (locked, disk full): Save fails and keeps the old records.</summary>
    public bool Unwritable { get; set; }

    public int SaveCalls { get; private set; }

    public IReadOnlyList<ChangedWindowRecord>? Load() => Unreadable ? null : [.. Records];

    public bool Save(IReadOnlyList<ChangedWindowRecord> records)
    {
        SaveCalls++;
        if (Unwritable)
        {
            return false;
        }

        Records = [.. records];
        return true;
    }
}

/// <summary>A clock that only moves when the test says so.</summary>
internal sealed class FakeTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
