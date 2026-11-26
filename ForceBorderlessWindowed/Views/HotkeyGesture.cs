using System.Windows.Input;

using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// Matches a <see cref="Hotkey"/> for an InputBinding. Not KeyGesture: that one refuses letters without
/// Ctrl/Alt, which the "remove game" shortcut of the games list may use.
/// </summary>
internal sealed class HotkeyGesture(Hotkey hotkey) : InputGesture
{
    public override bool Matches(object targetElement, InputEventArgs inputEventArgs) =>
        inputEventArgs is KeyEventArgs args
        && (args.Key == Key.System ? args.SystemKey : args.Key) == hotkey.Key
        && Keyboard.Modifiers == hotkey.Modifiers;
}
