using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>Read-only Win32 helpers shared by the "Add" menu and the game window finder.</summary>
internal static class WindowQueries
{
    /// <summary>All top-level windows, in z-order (topmost first).</summary>
    public static List<HWND> GetTopLevelWindows()
    {
        // Collect first, inspect afterwards: keeps the EnumWindows callback short
        var windows = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, default);
        return windows;
    }

    /// <summary>
    /// Roughly "would this window show up in the taskbar / Alt+Tab": visible, not owned by another window,
    /// not a tool window, not cloaked. Without <paramref name="requireTitle"/> untitled windows count too,
    /// because some games never set a window title.
    /// </summary>
    public static unsafe bool IsMainWindowCandidate(HWND hwnd, bool requireTitle)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || (requireTitle && PInvoke.GetWindowTextLength(hwnd) == 0))
        {
            return false;
        }

        // Dialogs and popups belong to another window
        if (!PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER).IsNull)
        {
            return false;
        }

        // Floating tool palettes etc.
        var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if (exStyle.HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW))
        {
            return false;
        }

        // "Cloaked": technically visible but not shown, e.g. suspended Store apps or windows on another virtual desktop
        int cloaked = 0;
        var result = PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int));
        return result.Failed || cloaked == 0;
    }

    /// <summary>True for top-level windows, false for child windows (controls inside a window).</summary>
    public static bool IsTopLevel(HWND hwnd) => PInvoke.GetAncestor(hwnd, GET_ANCESTOR_FLAGS.GA_ROOT) == hwnd;

    public static unsafe uint GetProcessId(HWND hwnd)
    {
        uint processId = 0;
        _ = PInvoke.GetWindowThreadProcessId(hwnd, &processId);
        return processId;
    }

    public static unsafe string GetWindowTitle(HWND hwnd)
    {
        var length = PInvoke.GetWindowTextLength(hwnd);
        var buffer = new char[length + 1];
        fixed (char* p = buffer)
        {
            var copied = PInvoke.GetWindowText(hwnd, p, buffer.Length);
            return new string(p, 0, copied);
        }
    }

    /// <summary>
    /// Window area in pixels, 0 if the window is gone. A minimized window counts with its normal size: its current
    /// rectangle is a tiny one off-screen, and a minimized game must stay its own main window, not lose that to
    /// a small launcher or splash window next to it.
    /// </summary>
    public static long GetWindowArea(HWND hwnd)
    {
        RECT rect;
        if (PInvoke.IsIconic(hwnd))
        {
            var placement = new WINDOWPLACEMENT { length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!PInvoke.GetWindowPlacement(hwnd, ref placement))
            {
                return 0;
            }

            rect = placement.rcNormalPosition;
        }
        else if (!PInvoke.GetWindowRect(hwnd, out rect))
        {
            return 0;
        }

        return (long)(rect.right - rect.left) * (rect.bottom - rect.top);
    }

    /// <summary>
    /// When the process started (FILETIME ticks), or null if it is gone or can't be queried. Together with the
    /// process ID it identifies one run of a program: IDs are reused, start times aren't.
    /// </summary>
    public static unsafe long? GetProcessStartTime(uint processId)
    {
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            System.Runtime.InteropServices.ComTypes.FILETIME created, exited, kernel, user;
            return PInvoke.GetProcessTimes(process, &created, &exited, &kernel, &user)
                ? ((long)(uint)created.dwHighDateTime << 32) | (uint)created.dwLowDateTime
                : null;
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    /// <summary>Full exe path, or null if the process is gone or protected.</summary>
    public static unsafe string? GetExecutablePath(uint processId)
    {
        // "Limited information" is enough for the path and is also granted for most elevated processes
        var process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull)
        {
            return null;
        }

        try
        {
            var buffer = new char[1024];
            fixed (char* p = buffer)
            {
                var size = (uint)buffer.Length;
                return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, p, &size)
                    ? new string(p, 0, (int)size)
                    : null;
            }
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }
}
