using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;

using ForceBorderlessWindowed.Models;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// While a hotkey field records: catches combinations that another program has registered as its hotkey.
/// Windows hands those straight to that program, so the field would never see them: it stayed unchanged and the
/// other program's action ran. A low-level keyboard hook sees each key before that happens. Only combinations
/// Windows wouldn't give us are taken out of the input and recorded; everything else reaches the field as usual.
/// The hook runs in this process (nothing is injected anywhere) and only while a hotkey field in the settings
/// window has the focus, so the keys are meant for this app anyway. Create and dispose on the UI thread.
/// </summary>
internal sealed unsafe class HotkeyRecordingHook : IDisposable
{
    /// <summary>
    /// A virtual key no keyboard has. Pressed and released by us after taking a Win or Alt combination, like
    /// AutoHotkey's "menu mask key": otherwise releasing Win alone would open Start, and Alt this window's menu mode.
    /// </summary>
    private const ushort MaskKey = 0xE8;

    private readonly Func<Hotkey, bool> _isAvailable;
    private readonly Action<Hotkey> _record;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    // Kept in a field: Windows only holds a native pointer to the delegate (see WindowEventHooks)
    private readonly HOOKPROC _proc;
    private HHOOK _hook;

    /// <summary>The key whose key-down was taken: its repeats and its key-up are taken too; 0 = none.</summary>
    private uint _takenKey;

    /// <param name="isAvailable">Whether Windows would give us the combination (<see cref="IHotkeyService.IsAvailable"/>).</param>
    /// <param name="record">Called with a taken combination, queued on the UI thread.</param>
    public HotkeyRecordingHook(Func<Hotkey, bool> isAvailable, Action<Hotkey> record)
    {
        _isAvailable = isAvailable;
        _record = record;
        _proc = OnKey;

        // The raw overloads on purpose: GetModuleHandle doesn't add a reference, so nothing may free this handle
        var module = PInvoke.GetModuleHandle(default(PCWSTR));
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_KEYBOARD_LL, _proc, (HINSTANCE)module.Value, 0);

        // Null is not fatal: recording works as before, only combinations of other programs can't be recorded
    }

    private LRESULT OnKey(int code, WPARAM wParam, LPARAM lParam)
    {
        // Must return quickly: Windows skips a hook that keeps the keyboard waiting. Nothing may escape either.
        try
        {
            if (code >= 0 && ShouldTake((uint)wParam.Value, *(KBDLLHOOKSTRUCT*)lParam.Value))
            {
                return (LRESULT)1;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Let the key through as if there were no hook
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }

    private bool ShouldTake(uint message, in KBDLLHOOKSTRUCT info)
    {
        var isDown = message is PInvoke.WM_KEYDOWN or PInvoke.WM_SYSKEYDOWN;
        if (_takenKey != 0 && info.vkCode == _takenKey)
        {
            if (!isDown)
            {
                _takenKey = 0;
            }

            return true;
        }

        if (!isDown || info.vkCode == MaskKey)
        {
            return false;
        }

        var key = KeyInterop.KeyFromVirtualKey((int)info.vkCode);

        // Modifiers wait for the real key; Tab always keeps moving the focus on
        if (HotkeyRules.IsModifierKey(key) || key == Key.Tab)
        {
            return false;
        }

        var hotkey = new Hotkey(GetModifiers(), key);
        if (_isAvailable(hotkey))
        {
            return false;   // reaches the field normally
        }

        _takenKey = info.vkCode;
        if ((hotkey.Modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0)
        {
            PressMaskKey();
        }

        _dispatcher.BeginInvoke(() => _record(hotkey));
        return true;
    }

    /// <summary>
    /// The modifiers held right now. Keyboard.Modifiers can lag behind here: the hook runs before this thread
    /// has read the earlier key messages. The key being pressed isn't in the async state yet, it's not a modifier.
    /// </summary>
    private static ModifierKeys GetModifiers()
    {
        static bool IsDown(VIRTUAL_KEY key) => PInvoke.GetAsyncKeyState((int)key) < 0;

        var modifiers = ModifierKeys.None;
        if (IsDown(VIRTUAL_KEY.VK_CONTROL))
        {
            modifiers |= ModifierKeys.Control;
        }

        if (IsDown(VIRTUAL_KEY.VK_MENU))
        {
            modifiers |= ModifierKeys.Alt;
        }

        if (IsDown(VIRTUAL_KEY.VK_SHIFT))
        {
            modifiers |= ModifierKeys.Shift;
        }

        if (IsDown(VIRTUAL_KEY.VK_LWIN) || IsDown(VIRTUAL_KEY.VK_RWIN))
        {
            modifiers |= ModifierKeys.Windows;
        }

        return modifiers;
    }

    private static void PressMaskKey()
    {
        Span<INPUT> inputs =
        [
            new() { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = { wVk = (VIRTUAL_KEY)MaskKey } } },
            new() { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = { wVk = (VIRTUAL_KEY)MaskKey, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP } } },
        ];
        PInvoke.SendInput(inputs, Marshal.SizeOf<INPUT>());
    }

    public void Dispose()
    {
        if (!_hook.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(_hook);
            _hook = HHOOK.Null;
        }
    }
}
