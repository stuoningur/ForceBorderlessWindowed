using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// Records a key combination: focus it, press e.g. Ctrl+Alt+P, and it shows "Ctrl + Alt + P".
/// A read-only TextBox underneath, so it looks like the other fields and screen readers read the
/// combination as its value. Tab and Shift+Tab still move the focus on; Esc does nothing here
/// (instead of closing the window), so a stray Esc doesn't throw the window's edits away.
/// Needs an explicit style (BasedOn the TextBox style): implicit styles only apply to their exact type.
/// </summary>
public class HotkeyBox : TextBox
{
    public static readonly DependencyProperty HotkeyProperty = DependencyProperty.Register(
        nameof(Hotkey), typeof(Hotkey), typeof(HotkeyBox),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHotkeyChanged));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        IsUndoEnabled = false;
        // No IME composition window popping up while recording
        InputMethod.SetIsInputMethodEnabled(this, false);
        UpdateText();
    }

    /// <summary>null = no hotkey set.</summary>
    public Hotkey? Hotkey
    {
        get => (Hotkey?)GetValue(HotkeyProperty);
        set => SetValue(HotkeyProperty, value);
    }

    private static void OnHotkeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HotkeyBox)d).UpdateText();

    private void UpdateText() => Text = Hotkey is { } hotkey ? HotkeyRules.Format(hotkey) : Strings.Hotkeys_NotSet;

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // With Alt held, WPF reports Key.System and the real key separately
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;

        // Keyboard navigation keeps working
        if (key == Key.Tab && (modifiers & ~ModifierKeys.Shift) == 0)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        // Everything else is ours: no window shortcuts, access keys or Alt menu while recording
        e.Handled = true;

        // Esc: nothing (see summary). Modifiers alone: wait for the actual key.
        if ((key == Key.Escape && modifiers == ModifierKeys.None) || HotkeyRules.IsModifierKey(key))
        {
            return;
        }

        Record(new Hotkey(modifiers, key));
    }

    /// <summary>
    /// Sets a recorded combination, also one that never reached this field as a key press because another program
    /// has it as its hotkey (see HotkeyRecordingHook).
    /// </summary>
    public void Record(Hotkey hotkey)
    {
        Hotkey = hotkey;
        SelectAll();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        SelectAll();   // "press a combination to replace this", like a selected text field
    }
}
