using System.Runtime.InteropServices;

using ForceBorderlessWindowed.Models;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

internal sealed class WindowManipulator : IWindowManipulator
{
    // Everything that draws a title bar or border. WS_CAPTION = WS_BORDER | WS_DLGFRAME.
    // The min/max boxes and the system menu only make sense with a title bar, so they go too.
    private const WINDOW_STYLE FrameStyles =
        WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME | WINDOW_STYLE.WS_SYSMENU
        | WINDOW_STYLE.WS_MINIMIZEBOX | WINDOW_STYLE.WS_MAXIMIZEBOX;

    // Only the extended styles that draw edges. Unlike Borderless Gaming we keep WS_EX_LAYERED
    // (removing it can make a window invisible) and WS_EX_APPWINDOW (removes the taskbar button).
    private const WINDOW_EX_STYLE EdgeExStyles =
        WINDOW_EX_STYLE.WS_EX_DLGMODALFRAME | WINDOW_EX_STYLE.WS_EX_WINDOWEDGE
        | WINDOW_EX_STYLE.WS_EX_CLIENTEDGE | WINDOW_EX_STYLE.WS_EX_STATICEDGE;

    // State bits that describe the current situation instead of the look. Restoring them
    // from a snapshot would contradict the actual state (e.g. WS_MAXIMIZE on a normal window).
    private const WINDOW_STYLE StateStyles =
        WINDOW_STYLE.WS_MAXIMIZE | WINDOW_STYLE.WS_MINIMIZE | WINDOW_STYLE.WS_VISIBLE;

    // FRAMECHANGED: Windows recalculates the frame. Without it the old border stays until the next redraw.
    // NOACTIVATE / NOZORDER / NOOWNERZORDER: don't steal focus or reorder windows.
    // ASYNCWINDOWPOS: hand the move to the game's thread instead of waiting for it, so a game that is
    // busy loading doesn't block the watcher (and with it alt-tab handling for the mouse lock).
    private const SET_WINDOW_POS_FLAGS PositionFlags =
        SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE
        | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER
        | SET_WINDOW_POS_FLAGS.SWP_ASYNCWINDOWPOS;

    private const int ErrorAccessDenied = 5;

    public bool Exists(nint windowHandle) => PInvoke.IsWindow(new HWND(windowHandle));

    public bool HasFrame(nint windowHandle)
    {
        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(new HWND(windowHandle), WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        return (style & (WINDOW_STYLE.WS_CAPTION | WINDOW_STYLE.WS_THICKFRAME)) != 0;
    }

    public WindowSnapshot? Capture(nint windowHandle)
    {
        var hwnd = new HWND(windowHandle);
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!PInvoke.IsWindow(hwnd) || !PInvoke.GetWindowPlacement(hwnd, ref placement))
        {
            return null;
        }

        // GetWindowPlacement instead of GetWindowRect: it also knows the normal size of a maximized window
        var normal = placement.rcNormalPosition;
        return new WindowSnapshot
        {
            Style = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE),
            ExStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE),
            NormalRect = (normal.left, normal.top, normal.right, normal.bottom),
            // A minimized window is never "zoomed", even one that comes back maximized: the placement knows that
            WasMaximized = PInvoke.IsZoomed(hwnd)
                || (PInvoke.IsIconic(hwnd) && (placement.flags & WINDOWPLACEMENT_FLAGS.WPF_RESTORETOMAXIMIZED) != 0),
        };
    }

    public (int Width, int Height)? GetClientSize(nint windowHandle) =>
        PInvoke.GetClientRect(new HWND(windowHandle), out var rect) ? (rect.right - rect.left, rect.bottom - rect.top) : null;

    // State bits plus "disabled" (a game disables its window while it shows a dialog): none of them is a change of mode
    private const uint LookIgnoredStyles = (uint)(StateStyles | WINDOW_STYLE.WS_DISABLED);

    public WindowLook? CaptureLook(nint windowHandle, WindowRect placement, MonitorBounds monitor)
    {
        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return null;
        }

        // What we asked for, not GetWindowRect: the move is asynchronous and may not have happened yet.
        // Same range check as MakeBorderless: a rectangle out of range never got applied, so there's no look of ours
        if (ScreenBounds(placement, monitor) is not { } bounds)
        {
            return null;
        }

        return new WindowLook
        {
            Style = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & ~LookIgnoredStyles,
            Bounds = bounds,
        };
    }

    /// <summary>
    /// The placement in screen coordinates, or null if any edge is outside the int range Win32 takes. Computed as
    /// long: settings are checked on load, but an int sum must never wrap around to a wrong position.
    /// </summary>
    internal static (int Left, int Top, int Right, int Bottom)? ScreenBounds(WindowRect placement, MonitorBounds monitor)
    {
        var left = (long)monitor.Left + placement.OffsetX;
        var top = (long)monitor.Top + placement.OffsetY;
        var right = left + placement.Width;
        var bottom = top + placement.Height;

        static bool InRange(long value) => value is >= int.MinValue and <= int.MaxValue;
        return InRange(left) && InRange(top) && InRange(right) && InRange(bottom)
            ? ((int)left, (int)top, (int)right, (int)bottom)
            : null;
    }

    public bool GameSwitchedMode(nint windowHandle, WindowLook look)
    {
        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd) || HasFrame(windowHandle))
        {
            return false;   // gone, or windowed again: then it's simply made borderless again
        }

        var style = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE) & ~LookIgnoredStyles;
        if (style == look.Style)
        {
            return false;   // not restyled since our change
        }

        // Popup (no title bar at all, the usual style of fullscreen and "windowed fullscreen") turned on or off
        if (((style ^ look.Style) & (uint)WINDOW_STYLE.WS_POPUP) != 0)
        {
            return true;
        }

        // Some other bit changed. Only a switch if the game also took over the size: the window fills its whole
        // monitor, in a rectangle we didn't set (a fullscreen placement of ours looks the same, but is ours)
        if (!PInvoke.GetWindowRect(hwnd, out var rect) || (rect.left, rect.top, rect.right, rect.bottom) == look.Bounds)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        var monitor = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetMonitorInfo(monitor, ref info)
            && rect.left <= info.rcMonitor.left && rect.top <= info.rcMonitor.top
            && rect.right >= info.rcMonitor.right && rect.bottom >= info.rcMonitor.bottom;
    }

    public WindowChangeResult MakeBorderless(nint windowHandle, WindowRect placement, MonitorBounds monitor, out bool modified, out WindowChangeError? error)
    {
        modified = false;
        error = null;
        var hwnd = new HWND(windowHandle);

        if (!PInvoke.IsWindow(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return WindowChangeResult.Skipped;
        }

        var style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var newStyle = style & ~FrameStyles;
        var newExStyle = exStyle & ~EdgeExStyles;

        // Physical pixels: this app is PerMonitorV2 DPI aware, like MonitorBounds and WindowPlacement
        if (ScreenBounds(placement, monitor) is not { } bounds)
        {
            error = new WindowChangeError("position out of range", 0);
            return WindowChangeResult.Failed;   // nothing touched yet
        }

        var (x, y, _, _) = bounds;
        var isMaximized = PInvoke.IsZoomed(hwnd);

        // Idempotent: our own SetWindowPos below triggers a "location changed" event too. Ending up here
        // with nothing to do is what stops that from becoming a loop.
        if (!isMaximized && style == newStyle && exStyle == newExStyle
            && PInvoke.GetWindowRect(hwnd, out var rect)
            && rect.left == x && rect.top == y
            && rect.right - rect.left == placement.Width && rect.bottom - rect.top == placement.Height)
        {
            return WindowChangeResult.NoChange;
        }

        // Hung: changing the styles would wait for the game until it responds again. Try again later.
        // Only after the "nothing to do" check above, which only reads (sends the game no messages): a borderless
        // game that is busy loading stays "borderless" instead of turning into "not responding" for a while.
        if (PInvoke.IsHungAppWindow(hwnd))
        {
            return WindowChangeResult.NotResponding;
        }

        // A maximized window ignores size changes. SHOWNOACTIVATE un-maximizes without stealing focus
        // (SW_RESTORE would activate it).
        if (isMaximized)
        {
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            modified = true;   // ShowWindow's return value says nothing about success, so assume it worked

            // Read again: the styles above still have WS_MAXIMIZE, and writing them back would mark the
            // window as maximized again (it would then count as "changed" on every check and trip the fight limit)
            style = (WINDOW_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
            exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            newStyle = style & ~FrameStyles;
            newExStyle = exStyle & ~EdgeExStyles;
        }

        // Only moved or resized (the frame is gone already): the styles are left alone, so the game isn't sent
        // style-change messages for nothing (each one waits for the game to answer)
        if (style != newStyle || exStyle != newExStyle)
        {
            var result = SetStyles(hwnd, (uint)newStyle, (uint)newExStyle, ref modified, out error);
            if (result != WindowChangeResult.Applied)
            {
                return result;
            }
        }

        // Onto another monitor: first only move it there, then set the final size. Moved and resized in one go,
        // apps that scale per monitor (e.g. Paint) can miss or misapply the new display's scaling (WM_DPICHANGED)
        // until the user drags them once. The first move lets Windows switch the scaling and the app resize
        // itself; the second, queued behind it on the app's thread (both async), then sets our size.
        // Same approach as PowerToys FancyZones. Monitors are compared instead of DPI values: an app that
        // ignores scaling reports misleading ones, and an extra move between equally scaled monitors is harmless.
        if (!IsOnMonitorAt(hwnd, monitor.Left, monitor.Top))
        {
            if (!PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, 0, 0, PositionFlags | SET_WINDOW_POS_FLAGS.SWP_NOSIZE))
            {
                return LastErrorResult("SetWindowPos (move to display)", out error);
            }

            modified = true;   // moved: its old place is part of what a restore puts back
        }

        if (!PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, placement.Width, placement.Height, PositionFlags))
        {
            return LastErrorResult("SetWindowPos", out error);
        }

        modified = true;
        return WindowChangeResult.Applied;
    }

    public WindowChangeResult Restore(nint windowHandle, WindowSnapshot snapshot, out WindowChangeError? error)
    {
        error = null;
        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return WindowChangeResult.Skipped;
        }

        if (PInvoke.IsHungAppWindow(hwnd))
        {
            return WindowChangeResult.NotResponding;
        }

        // Only the frame and edge bits come from the snapshot: those are the ones MakeBorderless removed.
        // Everything else belongs to the game and stays as it is now: visible/minimized, disabled while it
        // shows a dialog, topmost, layered...
        var currentStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        var currentExStyle = (uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var style = (currentStyle & ~(uint)FrameStyles) | (snapshot.Style & (uint)FrameStyles);
        var exStyle = (currentExStyle & ~(uint)EdgeExStyles) | (snapshot.ExStyle & (uint)EdgeExStyles);

        var restoredAny = false;   // not needed here: a failed restore is simply retried as a whole
        var result = SetStyles(hwnd, style, exStyle, ref restoredAny, out error);
        if (result != WindowChangeResult.Applied)
        {
            return result;
        }

        // Make the frame visible again before moving, then let SetWindowPlacement put back
        // the normal size/position and the maximized state in one go
        if (!PInvoke.SetWindowPos(hwnd, HWND.Null, 0, 0, 0, 0,
                PositionFlags | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE))
        {
            return LastErrorResult("SetWindowPos (frame)", out error);
        }

        var (left, top, right, bottom) = snapshot.NormalRect;
        var placement = new WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>(),
            rcNormalPosition = new RECT(left, top, right, bottom),
        };

        if (!PInvoke.IsWindowVisible(hwnd))
        {
            // Hidden by the game since we changed it (e.g. it switched to another window): only put the
            // bounds back. Every other show command would make it visible again. A hidden window can't be
            // set to maximized, so that part of the original state is left to the game.
            placement.showCmd = SHOW_WINDOW_CMD.SW_HIDE;
        }
        else if (PInvoke.IsIconic(hwnd))
        {
            // Minimized by the user (e.g. paused while alt-tabbed): stay minimized, but come back
            // in the original size/position, or maximized, once the user opens it again
            placement.showCmd = SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE;
            if (snapshot.WasMaximized)
            {
                placement.flags = WINDOWPLACEMENT_FLAGS.WPF_RESTORETOMAXIMIZED;
            }
        }
        else
        {
            // Maximizing always activates the window; that's acceptable for a rare restore
            placement.showCmd = snapshot.WasMaximized ? SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED : SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE;
        }

        return PInvoke.SetWindowPlacement(hwnd, in placement)
            ? WindowChangeResult.Applied
            : LastErrorResult("SetWindowPlacement", out error);
    }

    /// <summary>Whether the window is (mostly) on the monitor whose top-left corner is at the given point.</summary>
    private static bool IsOnMonitorAt(HWND hwnd, int left, int top) =>
        PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST)
        == PInvoke.MonitorFromPoint(new System.Drawing.Point(left, top), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);

    /// <param name="modified">Set to true as soon as one of the two styles was changed.</param>
    private static WindowChangeResult SetStyles(HWND hwnd, uint style, uint exStyle, ref bool modified, out WindowChangeError? error)
    {
        error = null;
        // SetWindowLong returns the previous value, which can legitimately be 0.
        // Only "0 and last error set" means failure, so the last error has to be cleared first.
        Marshal.SetLastPInvokeError(0);
        if (PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_STYLE, (int)style) == 0
            && Marshal.GetLastPInvokeError() != 0)
        {
            return LastErrorResult("SetWindowLong (style)", out error);
        }

        modified = true;

        Marshal.SetLastPInvokeError(0);
        if (PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle) == 0
            && Marshal.GetLastPInvokeError() != 0)
        {
            return LastErrorResult("SetWindowLong (extended style)", out error);
        }

        return WindowChangeResult.Applied;
    }

    // Windows blocks changes to windows of elevated processes (UIPI) with "access denied"
    private static WindowChangeResult LastErrorResult(string operation, out WindowChangeError? error)
    {
        var code = Marshal.GetLastPInvokeError();
        error = new WindowChangeError(operation, code);
        return code == ErrorAccessDenied ? WindowChangeResult.AccessDenied : WindowChangeResult.Failed;
    }
}
