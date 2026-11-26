using System.Windows.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests;

public class HotkeyRulesTests
{
    [Theory]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, Key.P, HotkeyProblem.None)]
    [InlineData(ModifierKeys.None, Key.F9, HotkeyProblem.None)]
    [InlineData(ModifierKeys.Shift, Key.P, HotkeyProblem.NeedsModifier)]
    [InlineData(ModifierKeys.None, Key.P, HotkeyProblem.NeedsModifier)]
    [InlineData(ModifierKeys.Control, Key.LeftShift, HotkeyProblem.Invalid)]
    [InlineData(ModifierKeys.Control, (Key)9999, HotkeyProblem.Invalid)]
    public void Check_GlobalHotkey(ModifierKeys modifiers, Key key, HotkeyProblem expected)
    {
        Assert.Equal(expected, HotkeyRules.Check(HotkeyAction.TogglePause, new Hotkey(modifiers, key)));
    }

    [Theory]
    [InlineData(ModifierKeys.None, Key.Delete, HotkeyProblem.None)]
    [InlineData(ModifierKeys.None, Key.X, HotkeyProblem.None)]
    [InlineData(ModifierKeys.None, Key.Up, HotkeyProblem.NavigationKey)]
    [InlineData(ModifierKeys.None, Key.Space, HotkeyProblem.NavigationKey)]
    [InlineData(ModifierKeys.Control, Key.Up, HotkeyProblem.None)]
    public void Check_RemoveGame_OnlyRefusesNavigationKeys(ModifierKeys modifiers, Key key, HotkeyProblem expected)
    {
        Assert.Equal(expected, HotkeyRules.Check(HotkeyAction.RemoveGame, new Hotkey(modifiers, key)));
    }

    [Fact]
    public void CommonShortcuts_AreRecognized()
    {
        Assert.True(HotkeyRules.IsCommonShortcut(new Hotkey(ModifierKeys.Control, Key.C)));
        Assert.True(HotkeyRules.IsCommonShortcut(new Hotkey(ModifierKeys.Alt, Key.F4)));
        Assert.False(HotkeyRules.IsCommonShortcut(new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.C)));
    }

    [Fact]
    public void HotkeySettings_With_ResetsWindowShortcutsToTheirDefault()
    {
        var settings = new HotkeySettings { Save = new Hotkey(ModifierKeys.Control, Key.F5) };

        Assert.Equal(HotkeySettings.DefaultSave, settings.With(HotkeyAction.Save, null).Save);
        Assert.Null(settings.With(HotkeyAction.TogglePause, null).TogglePause);
    }
}
