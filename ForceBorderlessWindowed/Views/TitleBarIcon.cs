using System.Windows;
using System.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;

using DrawingIcon = System.Drawing.Icon;

namespace ForceBorderlessWindowed.Views;

/// <summary>Light, dark or a contrast theme: what the windows show, see App.ShownTheme.</summary>
internal enum ShownTheme
{
    Light,
    Dark,
    HighContrast,
}

/// <summary>
/// The small icon in a window's title bar: the tray glyph made for the title bar's color, instead of the app icon
/// (a dark monitor that almost disappears on the dark title bar). Only the small icon (ICON_SMALL): the taskbar
/// button and Alt+Tab keep the real app icon. In a contrast theme the window keeps the app icon.
/// </summary>
internal static class TitleBarIcon
{
    private const uint WM_SETICON = 0x0080;
    private const nuint ICON_SMALL = 0;

    // Created once per color and never destroyed: windows only borrow the handle, and the app needs at most two
    private static DrawingIcon? _forLight;
    private static DrawingIcon? _forDark;

    public static void Attach(Window window)
    {
        if (Application.Current is not App app)
        {
            return;
        }

        EventHandler onThemeChanged = (_, _) => Apply(window, app.ShownTheme);
        window.SourceInitialized += (_, _) =>
        {
            Apply(window, app.ShownTheme);
            app.ShownThemeChanged += onThemeChanged;
        };
        window.Closed += (_, _) => app.ShownThemeChanged -= onThemeChanged;
    }

    private static void Apply(Window window, ShownTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0)
        {
            return;
        }

        // 0 = no own small icon: Windows falls back to the window class icon, the app icon
        nint icon = theme switch
        {
            ShownTheme.Light => (_forLight ??= TrayIconRenderer.CreatePlain(lightBackground: true)).Handle,
            ShownTheme.Dark => (_forDark ??= TrayIconRenderer.CreatePlain(lightBackground: false)).Handle,
            _ => 0,
        };
        PInvoke.SendMessage(new HWND(handle), WM_SETICON, ICON_SMALL, icon);
    }
}
