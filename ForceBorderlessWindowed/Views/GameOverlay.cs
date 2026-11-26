using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using ForceBorderlessWindowed.Controls;
using ForceBorderlessWindowed.Services;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// The hotkey messages over the game (see <see cref="IGameOverlay"/>), styled like a Windows 11 flyout: the system's
/// acrylic backdrop (drawn by DWM, so it blurs whatever is behind, also the game), rounded corners with the system
/// border and shadow, the app name as a small caption and the message below, with a colored status badge.
/// Shown centered horizontally, a quarter of the way down the display the foreground window is on. Topmost, click-through and never active, so
/// the game keeps the focus and the mouse; fades in, stays a moment, fades out.
///
/// Not shown over a game in exclusive fullscreen (Windows reports a Direct3D fullscreen app then): a window on top
/// can make such a game drop out of fullscreen. The sound still plays.
/// </summary>
internal sealed class GameOverlay(ISettingsService settings) : IGameOverlay
{
    private static readonly TimeSpan VisibleTime = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan FadeInTime = TimeSpan.FromMilliseconds(167);
    private static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FadeStep = TimeSpan.FromMilliseconds(16);

    /// <summary>
    /// Where the message's middle sits, as a fraction of the display's height from its top: a quarter of the way down
    /// ("three quarters up"). Relative, so it lands in the same spot at any resolution and scaling; measured on the
    /// display the foreground window is on each time a message shows.
    /// </summary>
    private const double VerticalPosition = 1.0 / 4;

    // DWM attributes by number: Windows 11 22H2 and later. Older systems ignore them and show the solid fallback.
    private const DWMWINDOWATTRIBUTE UseImmersiveDarkMode = (DWMWINDOWATTRIBUTE)20;
    private const DWMWINDOWATTRIBUTE WindowCornerPreference = (DWMWINDOWATTRIBUTE)33;
    private const DWMWINDOWATTRIBUTE SystemBackdropType = (DWMWINDOWATTRIBUTE)38;
    private const int CornerRound = 2;            // DWMWCP_ROUND: 8 px, like flyouts and windows
    private const int BackdropTransient = 3;

    private const uint WmNcActivate = 0x0086;      // DWMSBT_TRANSIENTWINDOW: acrylic, the material of flyouts and menus

    private Window? _current;

    public void Show(string message, OverlayKind kind)
    {
        if (!settings.Current.Hotkeys.ShowMessages)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => ShowCore(message, kind));
    }

    private void ShowCore(string message, OverlayKind kind)
    {
        if (IsExclusiveFullscreenRunning())
        {
            return;
        }

        _current?.Close();

        var foreground = PInvoke.GetForegroundWindow();
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(foreground, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY), ref info))
        {
            return;
        }

        var monitor = info.rcMonitor;
        var window = CreateWindow(message, kind);
        var hwnd = HWND.Null;

        window.SourceInitialized += (_, _) =>
        {
            hwnd = new HWND(new WindowInteropHelper(window).Handle);
            ApplyBackdrop(window, hwnd);

            // Windows draws the acrylic only for an active window, and this one never is (it mustn't take the focus
            // from the game): a plain gray instead. Telling DWM that its frame is active keeps the material without
            // activating anything; keyboard focus and the foreground window stay with the game.
            var captured = hwnd;
            HwndSource.FromHwnd(hwnd).AddHook((nint _, int msg, nint _, nint lParam, ref bool handled) =>
            {
                if (msg != WmNcActivate)
                {
                    return 0;
                }

                handled = true;
                return PInvoke.DefWindowProc(captured, (uint)msg, new WPARAM(1), new LPARAM(lParam));
            });
            PInvoke.SendMessage(hwnd, WmNcActivate, new WPARAM(1), new LPARAM(0));

            // Click-through needs TRANSPARENT + LAYERED; LAYERED also gives the fade (the window's alpha).
            // TOOLWINDOW: not in Alt+Tab. NOACTIVATE: never takes the focus from the game.
            var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
                | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT
                | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);
            SetAlpha(hwnd, 0);

            // Onto the target display first: WPF then lays the text out at that display's scaling
            PInvoke.SetWindowPos(hwnd, HWND.Null, monitor.left + ((monitor.right - monitor.left) / 2), monitor.top, 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
        };

        window.Closed += (_, _) =>
        {
            if (_current == window)
            {
                _current = null;
            }
        };

        _current = window;
        window.Show();

        // Centered once its size at the display's scaling is known (after the layout pass)
        window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (hwnd.IsNull || !PInvoke.GetWindowRect(hwnd, out var rect))
            {
                return;
            }

            var width = rect.right - rect.left;
            var height = rect.bottom - rect.top;
            var middle = monitor.top + (int)((monitor.bottom - monitor.top) * VerticalPosition);
            PInvoke.SetWindowPos(hwnd, HWND.Null,
                monitor.left + ((monitor.right - monitor.left - width) / 2), middle - (height / 2), 0, 0,
                SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);

            Fade(window, hwnd, from: 0, to: 1, FadeInTime, then: () =>
            {
                var timer = new DispatcherTimer { Interval = VisibleTime };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    Fade(window, hwnd, from: 1, to: 0, FadeOutTime, then: window.Close);
                };
                timer.Start();
            });
        });
    }

    private static Window CreateWindow(string message, OverlayKind kind)
    {
        var (glyph, colorKey) = kind switch
        {
            OverlayKind.Success => ("", "SystemFillColorSuccessBrush"),
            OverlayKind.Warning => ("", "SystemFillColorCautionBrush"),
            _ => ("", "AccentFillColorDefaultBrush"),
        };

        // Sizes: bigger than a Windows notification (14 px body text), so it reads at a glance over a game.
        // Badge: colored circle with the glyph cut into it, like the status badges in the Games list
        var circle = new FontIcon { Text = "", FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center };
        circle.SetResourceReference(FrameworkElement.StyleProperty, "IconTextStyle");
        circle.SetResourceReference(TextBlock.ForegroundProperty, colorKey);
        var mark = new FontIcon { Text = glyph, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center };
        mark.SetResourceReference(FrameworkElement.StyleProperty, "IconTextStyle");
        mark.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorInverseBrush");
        var badge = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        badge.Children.Add(circle);
        badge.Children.Add(mark);

        // Like a Windows notification: the app as a small caption, the message in body text
        var caption = new TextBlock { Text = Resources.Strings.AppName, FontSize = 14 };
        caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var text = new TextBlock
        {
            Text = message,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 0),
            MaxWidth = 720,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        lines.Children.Add(caption);
        lines.Children.Add(text);

        var content = new DockPanel { Margin = new Thickness(20, 16, 26, 16), MinWidth = 340 };
        content.Children.Add(badge);
        content.Children.Add(lines);

        return new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Title = "Force Borderless Windowed message",
            Content = content,
        };
    }

    /// <summary>
    /// The acrylic backdrop behind a transparent WPF surface, rounded corners, and the system's dark or light variant of
    /// the material (it follows the window's dark-mode flag, set here from the app's theme). Without support (older
    /// Windows) the window keeps the theme's solid background color.
    /// </summary>
    private static void ApplyBackdrop(Window window, HWND hwnd)
    {
        var solid = Application.Current.TryFindResource("SolidBackgroundFillColorBaseBrush") as SolidColorBrush;
        var dark = solid is not null && (solid.Color.R + solid.Color.G + solid.Color.B) / 3 < 128;

        int value = dark ? 1 : 0;
        SetAttribute(hwnd, UseImmersiveDarkMode, value);
        value = CornerRound;
        SetAttribute(hwnd, WindowCornerPreference, value);
        value = BackdropTransient;
        if (SetAttribute(hwnd, SystemBackdropType, value))
        {
            // The backdrop shows through where WPF draws nothing: transparent window and render target,
            // and DWM's frame extended over the whole window
            window.Background = Brushes.Transparent;
            HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);
        }
        else
        {
            window.SetResourceReference(Control.BackgroundProperty, "SolidBackgroundFillColorBaseBrush");
        }
    }

    private static unsafe bool SetAttribute(HWND hwnd, DWMWINDOWATTRIBUTE attribute, int value) =>
        PInvoke.DwmSetWindowAttribute(hwnd, attribute, &value, sizeof(int)).Succeeded;

    private static void SetAlpha(HWND hwnd, double opacity) =>
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255),
            LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);

    /// <summary>
    /// Fades the whole window, backdrop included, through its layered alpha (WPF's Opacity only reaches its own
    /// content). Ease-out on the way in, ease-in on the way out; instant without animations in Windows.
    /// </summary>
    private static void Fade(Window window, HWND hwnd, double from, double to, TimeSpan duration, Action? then)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            SetAlpha(hwnd, to);
            then?.Invoke();
            return;
        }

        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer(DispatcherPriority.Render, window.Dispatcher) { Interval = FadeStep };
        timer.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started) / duration);
            var eased = to > from ? 1 - ((1 - t) * (1 - t)) : t * t;
            SetAlpha(hwnd, from + ((to - from) * eased));
            if (t >= 1)
            {
                timer.Stop();
                then?.Invoke();
            }
        };
        timer.Start();
    }

    /// <summary>A Direct3D app in exclusive fullscreen is running (Windows' own "don't disturb" check).</summary>
    private static bool IsExclusiveFullscreenRunning() =>
        PInvoke.SHQueryUserNotificationState(out var state).Succeeded && state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
}
