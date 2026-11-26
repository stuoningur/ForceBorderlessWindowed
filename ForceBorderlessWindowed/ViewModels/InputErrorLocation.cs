namespace ForceBorderlessWindowed.ViewModels;

/// <summary>The tabs of the settings window, in their order.</summary>
public enum SettingsTab
{
    General,
    Games,
    Hotkeys,
}

/// <summary>Input fields that can hold something that can't be saved.</summary>
public enum SettingsField
{
    ExecutableName,
    Width,
    Height,
    OffsetX,
    Hotkey,
}

/// <summary>
/// Where an invalid input is: the view switches to that tab (and game) and puts the focus into the field,
/// so a Save that couldn't happen shows right away what to fix.
/// </summary>
/// <param name="Profile">The game, for fields on the Games tab; null on the other tabs.</param>
/// <param name="Hotkey">Which hotkey, for <see cref="SettingsField.Hotkey"/>.</param>
public sealed record InputErrorLocation(
    SettingsTab Tab, GameProfileViewModel? Profile, SettingsField Field, Models.HotkeyAction? Hotkey = null);
