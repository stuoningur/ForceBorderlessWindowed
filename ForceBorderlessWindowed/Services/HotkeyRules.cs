using System.Windows.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;

using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace ForceBorderlessWindowed.Services;

/// <summary>Why a key combination can't be used for an action.</summary>
public enum HotkeyProblem
{
    None,

    /// <summary>No key, only modifiers, or values that don't exist (hand-edited settings file).</summary>
    Invalid,

    /// <summary>Without Ctrl, Alt or Win it would block typing (a global hotkey even in every other program).</summary>
    NeedsModifier,

    /// <summary>A key the games list needs for moving around (arrows, Home, Space...).</summary>
    NavigationKey,
}

/// <summary>Which key combinations are allowed, and how they're shown ("Ctrl + Alt + P").</summary>
public static class HotkeyRules
{
    private const ModifierKeys AllModifiers =
        ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows;

    /// <summary>Modifiers that make a combination "not typing". Shift alone doesn't: Shift+A is just "A".</summary>
    private const ModifierKeys CommandModifiers = ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows;

    private static readonly HashSet<Key> NavigationKeys =
    [
        Key.Up, Key.Down, Key.Left, Key.Right, Key.Home, Key.End, Key.PageUp, Key.PageDown,
        Key.Space, Key.Enter, Key.Tab, Key.Escape,
    ];

    /// <summary>
    /// Shortcuts that programs and Windows itself commonly use. Windows gives them out as global hotkeys anyway,
    /// and then no program gets them anymore (Ctrl+C stops copying everywhere).
    /// </summary>
    private static readonly HashSet<Hotkey> CommonShortcuts =
    [
        .. new[] { Key.A, Key.C, Key.F, Key.N, Key.O, Key.P, Key.S, Key.T, Key.V, Key.W, Key.X, Key.Y, Key.Z }
            .Select(key => new Hotkey(ModifierKeys.Control, key)),
        new(ModifierKeys.Control, Key.Tab),
        new(ModifierKeys.Control, Key.Escape),
        new(ModifierKeys.Control | ModifierKeys.Shift, Key.Escape),
        new(ModifierKeys.Alt, Key.F4),
        new(ModifierKeys.Alt, Key.Tab),
        new(ModifierKeys.Alt | ModifierKeys.Shift, Key.Tab),
        new(ModifierKeys.Alt, Key.Escape),
        new(ModifierKeys.Alt, Key.Space),
    ];

    /// <summary>Whether programs commonly use the combination themselves, see <see cref="CommonShortcuts"/>.</summary>
    public static bool IsCommonShortcut(Hotkey hotkey) => CommonShortcuts.Contains(hotkey);

    /// <summary>Keys that are only pressed together with another one; a hotkey needs a "real" key.</summary>
    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None;

    public static HotkeyProblem Check(HotkeyAction action, Hotkey hotkey)
    {
        if (!Enum.IsDefined(hotkey.Key) || IsModifierKey(hotkey.Key) || (hotkey.Modifiers & ~AllModifiers) != 0)
        {
            return HotkeyProblem.Invalid;
        }

        var hasCommandModifier = (hotkey.Modifiers & CommandModifiers) != 0;

        // Only works while the games list has the focus, where letters and Delete type nothing
        if (action == HotkeyAction.RemoveGame)
        {
            return hasCommandModifier || !NavigationKeys.Contains(hotkey.Key)
                ? HotkeyProblem.None
                : HotkeyProblem.NavigationKey;
        }

        // F keys type nothing, so they're fine alone
        return hasCommandModifier || hotkey.Key is >= Key.F1 and <= Key.F24
            ? HotkeyProblem.None
            : HotkeyProblem.NeedsModifier;
    }

    /// <summary>"Ctrl + Alt + P" or "Ctrl + Num +"; in German "Strg + Alt + Entf". Key names follow the UI language.</summary>
    public static string Format(Hotkey hotkey)
    {
        var parts = new List<string>(5);
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control))
        {
            parts.Add(Strings.Key_Ctrl);
        }

        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            parts.Add(Strings.Key_Alt);
        }

        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            parts.Add(Strings.Key_Shift);
        }

        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows))
        {
            parts.Add(Strings.Key_Win);
        }

        parts.Add(GetKeyName(hotkey.Key));
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Keys with a name instead of a symbol on them: named in the UI language, like Ctrl/Alt/Shift.
    /// Windows' own names (GetKeyNameText) follow the keyboard layout instead, which gave "Ctrl + Entf" with an
    /// English UI on a German keyboard.
    /// </summary>
    private static string? GetNamedKey(Key key) => key switch
    {
        Key.Delete => Strings.Key_Delete,
        Key.Insert => Strings.Key_Insert,
        Key.Home => Strings.Key_Home,
        Key.End => Strings.Key_End,
        Key.PageUp => Strings.Key_PageUp,
        Key.PageDown => Strings.Key_PageDown,
        Key.Up => Strings.Key_Up,
        Key.Down => Strings.Key_Down,
        Key.Left => Strings.Key_Left,
        Key.Right => Strings.Key_Right,
        Key.Back => Strings.Key_Backspace,
        Key.Enter => Strings.Key_Enter,
        Key.Escape => Strings.Key_Escape,
        Key.Space => Strings.Key_Space,
        Key.Tab => Strings.Key_Tab,
        Key.Pause => Strings.Key_Pause,
        Key.PrintScreen => Strings.Key_PrintScreen,
        Key.Scroll => Strings.Key_ScrollLock,
        >= Key.F1 and <= Key.F24 => key.ToString(),
        _ => null,
    };

    /// <summary>
    /// Number pad keys as "Num 1", "Num +": the same in every language, and clearly not the other "+" key.
    /// The decimal key types "," or "." depending on the layout, so its symbol is asked from Windows.
    /// </summary>
    private static string? GetNumPadKey(Key key)
    {
        var symbol = key switch
        {
            >= Key.NumPad0 and <= Key.NumPad9 => ((char)('0' + (key - Key.NumPad0))).ToString(),
            Key.Add => "+",
            Key.Subtract => "-",
            Key.Multiply => "*",
            Key.Divide => "/",
            Key.Decimal => PInvoke.MapVirtualKey((uint)KeyInterop.VirtualKeyFromKey(key), MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_CHAR)
                is var c and > 0 and < 0x8000 ? ((char)c).ToString() : ".",
            _ => null,
        };

        return symbol is null ? null : $"Num {symbol}";
    }

    /// <summary>
    /// Named keys (Del, arrows, F5...) and number pad keys from our own names, see <see cref="GetNamedKey"/>.
    /// Letters and symbols as Windows names them for the current keyboard layout ("ß", "#"), so they match
    /// what's printed on the key. Falls back to the WPF name for keys Windows has no name for.
    /// </summary>
    private static unsafe string GetKeyName(Key key)
    {
        if ((GetNamedKey(key) ?? GetNumPadKey(key)) is { } ownName)
        {
            return ownName;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var scanCode = PInvoke.MapVirtualKey(virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);

        // Bit 16-23: scan code. Bit 24: "extended key", without it Delete would be named like Num-Decimal etc.
        var lParam = (int)(scanCode << 16);
        if (IsExtendedKey(key))
        {
            lParam |= 1 << 24;
        }

        if (scanCode != 0)
        {
            var buffer = stackalloc char[64];
            var length = PInvoke.GetKeyNameText(lParam, buffer, 64);
            if (length > 0)
            {
                var name = new string(buffer, 0, length);
                var culture = System.Globalization.CultureInfo.CurrentCulture;

                // Some layouts name letters in lower case; on the key it's upper case.
                // Others name keys in capitals ("ENTF" on a German keyboard): shown as "Entf", like the other names.
                return name.Length == 1 ? name.ToUpper(culture)
                    : !name.Any(char.IsLower) ? culture.TextInfo.ToTitleCase(name.ToLower(culture))
                    : name;
            }
        }

        return key switch
        {
            >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
            _ => key.ToString(),
        };
    }

    // Keys that share a scan code with a num pad key and are told apart by the "extended" flag
    private static bool IsExtendedKey(Key key) => key is Key.Insert or Key.Delete or Key.Home or Key.End
        or Key.PageUp or Key.PageDown or Key.Left or Key.Right or Key.Up or Key.Down
        or Key.Divide or Key.NumLock or Key.Apps or Key.PrintScreen;
}
