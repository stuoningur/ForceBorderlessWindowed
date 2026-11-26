using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The black window behind a game that doesn't fill its monitor ("black background", see ForegroundEffects).
/// Sits directly below the game in the z-order and covers the game's monitor. Clicks on it do nothing (it never
/// becomes active), and it isn't in Alt+Tab or the taskbar.
///
/// It has its own thread with a message loop. Not the watcher thread: that one waits for a hung game now and then,
/// and a window whose thread doesn't answer for 5 s is replaced by Windows with a "not responding" ghost window.
/// The methods only post to that thread, so they never block the caller.
/// </summary>
internal sealed class GameBackdrop : IDisposable
{
    private const int WmDpiChanged = 0x02E0;

    // POPUP: no frame. TOOLWINDOW: not in Alt+Tab / taskbar. NOACTIVATE: a click doesn't take the focus from the game.
    private const int Style = unchecked((int)WINDOW_STYLE.WS_POPUP);
    private const int ExStyle = (int)(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE);

    private readonly Dispatcher _dispatcher;
    private HwndSource? _window;

    public GameBackdrop()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;

        var thread = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
            _window?.Dispose();
        })
        {
            Name = "Game backdrop",
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
        _dispatcher = dispatcher!;
    }

    /// <summary>Covers <paramref name="monitor"/> (screen coordinates, physical pixels), right below the game window.</summary>
    public void Show(nint gameWindow, RECT monitor) => _dispatcher.BeginInvoke(() =>
    {
        var window = _window ??= Create();
        PInvoke.SetWindowPos((HWND)window.Handle, new HWND(gameWindow),
            monitor.left, monitor.top, monitor.right - monitor.left, monitor.bottom - monitor.top,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER | SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW);
    });

    public void Hide() => _dispatcher.BeginInvoke(() =>
    {
        if (_window is not null)
        {
            PInvoke.ShowWindow((HWND)_window.Handle, SHOW_WINDOW_CMD.SW_HIDE);
        }
    });

    private static HwndSource Create()
    {
        var window = new HwndSource(new HwndSourceParameters("Force Borderless Windowed backdrop")
        {
            WindowStyle = Style,
            ExtendedWindowStyle = ExStyle,
            Width = 1,
            Height = 1,
        })
        {
            RootVisual = new Border { Background = Brushes.Black },
        };
        window.CompositionTarget.BackgroundColor = Colors.Black;

        // Windows suggests a new size when the window lands on a monitor with other scaling. We set the size in
        // physical pixels ourselves, so that suggestion would only make it too small or too large.
        window.AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
        {
            handled = msg == WmDpiChanged;
            return 0;
        });
        return window;
    }

    public void Dispose() => _dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);
}
