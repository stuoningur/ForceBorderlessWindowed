using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

using Windows.Win32;
using Windows.Win32.Foundation;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// Access key underlines only while Alt is held, like newer Windows apps. WPF shows them on Alt down by itself, but
/// then keeps them for the window's lifetime, like a classic dialog. Two things are changed for that:
/// <list type="bullet">
/// <item>Alt up (or leaving the window) hides them again, through the same Windows switch that tracks them.</item>
/// <item>A lone Alt tap doesn't enter Windows' menu mode. This window has no menu bar, so that mode is invisible, but
/// it swallows the next key: the next Alt press would only leave it, and the underlines wouldn't come back. Alt+Space
/// (the window menu) is a different request and still works.</item>
/// </list>
/// Focus outlines aren't touched.
/// </summary>
public static class AccessKeyCues
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const uint WM_CHANGEUISTATE = 0x0127;
    private const int UIS_SET = 1;
    private const int UISF_HIDEACCEL = 0x2;

    public static void ShowOnlyWhileAltHeld(Window window)
    {
        window.SourceInitialized += (_, _) =>
            ((HwndSource)PresentationSource.FromVisual(window)!).AddHook(BlockAltMenuMode);

        window.PreviewKeyUp += (_, e) =>
        {
            // With Alt involved, WPF reports Key.System and the real key separately
            if ((e.Key == Key.System ? e.SystemKey : e.Key) is Key.LeftAlt or Key.RightAlt)
            {
                Hide(window);
            }
        };

        // Alt released elsewhere (Alt+Tab): this window never sees the key going up
        window.Deactivated += (_, _) => Hide(window);
    }

    /// <summary>SC_KEYMENU with no character = Alt on its own (with a character it's Alt+Space or a menu mnemonic).</summary>
    private static nint BlockAltMenuMode(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_SYSCOMMAND && (wParam & 0xFFF0) == SC_KEYMENU && lParam == 0)
        {
            handled = true;
        }

        return 0;
    }

    private static void Hide(Window window) =>
        // Queued: Windows and WPF update the underlines themselves while handling Alt, after this event
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != 0)
            {
                PInvoke.SendMessage(new HWND(hwnd), WM_CHANGEUISTATE, (nuint)((UISF_HIDEACCEL << 16) | UIS_SET), 0);
            }
        });
}
