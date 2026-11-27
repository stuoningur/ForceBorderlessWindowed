using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

using ForceBorderlessWindowed.Controls;
using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// The outline "Show on display" draws on the real monitor: accent-colored border, light fill, the size in the middle.
/// Click-through and never active, so it can't get in the way; it fades out by itself after a few seconds.
/// </summary>
internal sealed class ScreenPreviewOverlay : IScreenPreview
{
    private static readonly TimeSpan VisibleTime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan FadeTime = TimeSpan.FromMilliseconds(400);

    private const int WmDpiChanged = 0x02E0;

    private Window? _current;

    public void Show(MonitorBounds monitor, WindowRect placement, string label)
    {
        _current?.Close();

        var accent = Application.Current.TryFindResource("AccentFillColorDefaultBrush") as SolidColorBrush
            ?? SystemColors.HighlightBrush;
        var fill = new SolidColorBrush(accent.Color) { Opacity = 0.18 };
        fill.Freeze();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            IsHitTestVisible = false,
            Title = "Force Borderless Windowed preview",
            Content = new Border
            {
                BorderBrush = accent,
                BorderThickness = new Thickness(4),
                Background = fill,
                Child = new Border
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(16, 8, 16, 8),
                    CornerRadius = new CornerRadius(8),
                    Background = accent,
                    Child = new TextBlock
                    {
                        Text = label,
                        FontSize = 20,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Application.Current.TryFindResource("TextOnAccentFillColorPrimaryBrush") as Brush ?? Brushes.White,
                    },
                },
            },
        };

        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new HWND(new WindowInteropHelper(window).Handle);

            // Click-through (TRANSPARENT needs LAYERED, which AllowsTransparency already set), not in Alt+Tab, never active
            var exStyle = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)
                | WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE;
            _ = PInvoke.SetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)exStyle);

            // Physical pixels, like the game window will get them. A different scaling on the target monitor would
            // make Windows suggest another size: ignored, the size is exactly what we set.
            HwndSource.FromHwnd(hwnd).AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
            {
                handled = msg == WmDpiChanged;
                return 0;
            });

            var x = monitor.Left + placement.OffsetX;
            var y = monitor.Top + placement.OffsetY;
            // Moved first, then sized: the second call happens with the target monitor's scaling already in place
            // (same approach as WindowManipulator). Topmost already, from the Window property.
            const SET_WINDOW_POS_FLAGS flags = SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER;
            PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, 0, 0, flags | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
            PInvoke.SetWindowPos(hwnd, HWND.Null, x, y, placement.Width, placement.Height, flags);
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

        var timer = new DispatcherTimer { Interval = VisibleTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOutAndClose(window);
        };
        timer.Start();
    }

    private static void FadeOutAndClose(Window window)
    {
        if (!SystemParameters.ClientAreaAnimation)
        {
            window.Close();
            return;
        }

        var fade = new DoubleAnimation(0, FadeTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => window.Close();
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
