using System.Globalization;
using System.IO;

using Microsoft.Win32;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The optional per-game effects that only apply while the game is in the foreground:
/// confining the mouse to the game window, hiding the taskbar on the game's display, and a black background
/// behind the game on its display (see <see cref="GameBackdrop"/>; nothing to undo after a crash, the window
/// goes with the process).
///
/// Normally used by the watcher thread only. Locked anyway, because exiting releases everything from another
/// thread: the watcher thread may be stuck waiting for a hung game then. None of the calls in here wait for
/// another program (ShowWindowAsync only posts), so the lock is never held for long.
///
/// What is applied is also recorded in a volatile registry key (Windows deletes it at sign-out/restart).
/// If the app crashes or is killed, the next start undoes exactly that, and nothing that someone else did.
/// </summary>
internal sealed class ForegroundEffects
{
    private const string StateKeyPath = @"Software\ForceBorderlessWindowed\ActiveEffects";
    private const string ClipValue = "Clip";
    private const string TaskbarsValue = "HiddenTaskbars";

    private readonly Lock _lock = new();

    /// <summary>The clip rectangle we set, null if we don't confine the mouse right now.</summary>
    private RECT? _clip;

    private readonly List<HWND> _hiddenTaskbars = [];

    /// <summary>Created the first time a game wants it; its own thread, see <see cref="GameBackdrop"/>.</summary>
    private GameBackdrop? _backdrop;

    /// <summary>Which game and monitor the backdrop covers now; null = hidden. Repeated updates then post nothing.</summary>
    private (HWND Game, RECT Monitor)? _backdropShown;

    /// <summary>Set on exit: from then on nothing is applied anymore.</summary>
    private bool _stopped;

    /// <summary>What was last written to the registry, so it's only written when something changed.</summary>
    private string? _recordedState;

    /// <summary>True while anything is applied. The cursor clip needs regular re-checking then.</summary>
    public bool IsActive
    {
        get
        {
            lock (_lock)
            {
                return _clip is not null || _hiddenTaskbars.Count > 0 || _backdropShown is not null;
            }
        }
    }

    /// <param name="gameWindow">The foreground game window, or 0 if no (configured) game is in the foreground.</param>
    public void Update(nint gameWindow, bool lockCursor, bool hideTaskbar, bool blackBackground)
    {
        lock (_lock)
        {
            var hwnd = _stopped ? HWND.Null : new HWND(gameWindow);

            if (lockCursor && !hwnd.IsNull)
            {
                LockCursor(hwnd);
            }
            else
            {
                ReleaseCursor();
            }

            if (hideTaskbar && !hwnd.IsNull)
            {
                HideTaskbars(hwnd);
            }
            else
            {
                ShowHiddenTaskbars();
            }

            if (blackBackground && !hwnd.IsNull)
            {
                ShowBackdrop(hwnd);
            }
            else
            {
                HideBackdrop();
            }

            RecordState();
        }
    }

    /// <summary>Releases everything for good (app exit). Any thread.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            _stopped = true;
            Update(0, lockCursor: false, hideTaskbar: false, blackBackground: false);
            _backdrop?.Dispose();
            _backdrop = null;
        }
    }

    /// <summary>Covers the game's monitor behind the game; follows the game to another monitor.</summary>
    private void ShowBackdrop(HWND game)
    {
        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        var monitor = PInvoke.MonitorFromWindow(game, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            HideBackdrop();
            return;
        }

        if (_backdropShown is { } shown && shown.Game == game && Same(shown.Monitor, info.rcMonitor))
        {
            return;
        }

        (_backdrop ??= new GameBackdrop()).Show(game, info.rcMonitor);
        _backdropShown = (game, info.rcMonitor);
    }

    private void HideBackdrop()
    {
        if (_backdropShown is not null)
        {
            _backdrop?.Hide();
            _backdropShown = null;
        }
    }

    /// <summary>
    /// Clean-up after a crash: on startup, and from the crash handler. Undoes only what the registry says we
    /// applied, and only if it is still as we left it: a taskbar that's visible again or a cursor clip that
    /// someone else changed is left alone. Doesn't need an instance, so it works from any thread.
    /// </summary>
    public static void UndoRecorded()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(StateKeyPath))
            {
                if (key is null)
                {
                    return;
                }

                if (key.GetValue(TaskbarsValue) is string[] taskbars)
                {
                    var current = GetTaskbars();
                    foreach (var value in taskbars)
                    {
                        // Explorer restarted meanwhile = new handles, nothing of ours to undo
                        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var handle)
                            && current.Contains(new HWND((nint)handle))
                            && !PInvoke.IsWindowVisible(new HWND((nint)handle)))
                        {
                            PInvoke.ShowWindowAsync(new HWND((nint)handle), SHOW_WINDOW_CMD.SW_SHOWNA);
                        }
                    }
                }

                if (key.GetValue(ClipValue) is string clipText && TryParseRect(clipText, out var clip)
                    && PInvoke.GetClipCursor(out var currentClip) && Same(currentClip, clip))
                {
                    PInvoke.ClipCursor((RECT?)null);
                }
            }

            Registry.CurrentUser.DeleteSubKeyTree(StateKeyPath, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Best effort: never let the clean-up itself break startup or the crash handler
        }
    }

    private void LockCursor(HWND hwnd)
    {
        if (!PInvoke.GetWindowRect(hwnd, out var target) || !PInvoke.GetClipCursor(out var current))
        {
            ReleaseCursor();
            return;
        }

        if (Same(current, target))
        {
            // Already in place. Ownership stays as it is: if the game confined the mouse to its window
            // itself, that clip isn't ours, and releasing it on alt-tab is the game's business, not ours.
            return;
        }

        // Only take over an unconfined mouse (= clip is the whole virtual screen) or our own old rectangle.
        // Anything else was set by someone else, most likely the game itself (e.g. locked to the center
        // for mouse look): overriding it would break the game. Windows also resets the clip on its own
        // at times (e.g. UAC prompt); the regular re-check puts it back then.
        if (Same(current, VirtualScreen()) || (_clip is { } old && Same(current, old)))
        {
            // Only ours if it actually took effect; otherwise ReleaseCursor would later remove someone else's
            if (PInvoke.ClipCursor(target))
            {
                _clip = target;
            }
        }
    }

    private void ReleaseCursor()
    {
        if (_clip is not { } ours)
        {
            return;
        }

        // Couldn't even ask: unknown whether our clip is still active, so it stays recorded (crash clean-up)
        // and the next update tries again
        if (!PInvoke.GetClipCursor(out var current))
        {
            return;
        }

        // Only remove our own clip, never one the game has set in the meantime
        if (Same(current, ours) && !PInvoke.ClipCursor((RECT?)null))
        {
            // Refused (e.g. while another desktop like the UAC prompt is active): still ours, so it stays
            // recorded for the crash clean-up, and the next update (the re-check timer keeps running) tries again
            return;
        }

        _clip = null;
    }

    // The taskbar belongs to Explorer: ShowWindowAsync only posts the request, so a hanging Explorer
    // can't block the watcher thread.
    private void HideTaskbars(HWND game)
    {
        // Only the taskbar(s) on the game's display; other displays keep theirs (Discord, video...)
        var gameMonitor = PInvoke.MonitorFromWindow(game, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        foreach (var taskbar in GetTaskbars())
        {
            var onGameMonitor = PInvoke.MonitorFromWindow(taskbar, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST) == gameMonitor;
            var hiddenByUs = _hiddenTaskbars.Contains(taskbar);

            if (onGameMonitor && !hiddenByUs && PInvoke.IsWindowVisible(taskbar))
            {
                PInvoke.ShowWindowAsync(taskbar, SHOW_WINDOW_CMD.SW_HIDE);
                _hiddenTaskbars.Add(taskbar);
            }
            else if (!onGameMonitor && hiddenByUs)
            {
                // The game moved to another display
                PInvoke.ShowWindowAsync(taskbar, SHOW_WINDOW_CMD.SW_SHOWNA);
                _hiddenTaskbars.Remove(taskbar);
            }
        }
    }

    private void ShowHiddenTaskbars()
    {
        foreach (var taskbar in _hiddenTaskbars)
        {
            // SHOWNA: show without activating, so the window the user switched to keeps the focus.
            // Harmless if Explorer was restarted meanwhile and the handle is gone.
            PInvoke.ShowWindowAsync(taskbar, SHOW_WINDOW_CMD.SW_SHOWNA);
        }

        _hiddenTaskbars.Clear();
    }

    /// <summary>Writes what is applied to the registry, or removes the key when nothing is.</summary>
    private void RecordState()
    {
        var clip = _clip is { } c ? string.Create(CultureInfo.InvariantCulture, $"{c.left},{c.top},{c.right},{c.bottom}") : null;
        var taskbars = _hiddenTaskbars.Select(t => ((nint)t).ToString(CultureInfo.InvariantCulture)).ToArray();
        var state = clip is null && taskbars.Length == 0 ? null : $"{clip}|{string.Join(',', taskbars)}";

        // Nothing recorded and nothing applied: also true at startup, when UndoRecorded just removed the key
        if (state == _recordedState)
        {
            return;
        }

        try
        {
            if (state is null)
            {
                Registry.CurrentUser.DeleteSubKeyTree(StateKeyPath, throwOnMissingSubKey: false);
            }
            else
            {
                // Volatile: kept in memory only, gone after sign-out or restart (as are the effects themselves)
                using var key = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true, RegistryOptions.Volatile);
                if (clip is null)
                {
                    key.DeleteValue(ClipValue, throwOnMissingValue: false);
                }
                else
                {
                    key.SetValue(ClipValue, clip);
                }

                key.SetValue(TaskbarsValue, taskbars, RegistryValueKind.MultiString);
            }

            _recordedState = state;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Only the crash clean-up depends on it; the effects themselves work without
        }
    }

    private static bool TryParseRect(string text, out RECT rect)
    {
        rect = default;
        var parts = text.Split(',');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var left)
            || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var top)
            || !int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var right)
            || !int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var bottom))
        {
            return false;
        }

        rect = new RECT(left, top, right, bottom);
        return true;
    }

    /// <summary>The main taskbar plus one "secondary" taskbar per additional display (if enabled).</summary>
    private static List<HWND> GetTaskbars()
    {
        var result = new List<HWND>();

        var main = PInvoke.FindWindow("Shell_TrayWnd", null);
        if (!main.IsNull)
        {
            result.Add(main);
        }

        var secondary = HWND.Null;
        while (!(secondary = PInvoke.FindWindowEx(HWND.Null, secondary, "Shell_SecondaryTrayWnd", null)).IsNull)
        {
            result.Add(secondary);
        }

        return result;
    }

    private static RECT VirtualScreen()
    {
        var x = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_XVIRTUALSCREEN);
        var y = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_YVIRTUALSCREEN);
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXVIRTUALSCREEN);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYVIRTUALSCREEN);
        return new RECT(x, y, x + width, y + height);
    }

    private static bool Same(RECT a, RECT b) =>
        a.left == b.left && a.top == b.top && a.right == b.right && a.bottom == b.bottom;
}
