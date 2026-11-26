using System.Text.Json.Serialization;
using System.Windows.Input;

namespace ForceBorderlessWindowed.Models;

/// <summary>What a hotkey does. Global ones work in any program, the others only in the settings window.</summary>
public enum HotkeyAction
{
    TogglePause,
    AddForegroundGame,
    Save,
    RemoveGame,

    /// <summary>Makes the window in the foreground borderless just once, without adding it to the games.</summary>
    ApplyToForeground,
}

/// <summary>A key combination such as Ctrl+Alt+P. Stored as e.g. { "modifiers": "Control, Alt", "key": "P" }.</summary>
public sealed record Hotkey(ModifierKeys Modifiers, Key Key);

public sealed record HotkeySettings
{
    public static readonly Hotkey DefaultSave = new(ModifierKeys.Control, Key.S);
    public static readonly Hotkey DefaultRemoveGame = new(ModifierKeys.None, Key.Delete);

    // Global hotkeys are off unless the user sets them: any combination could clash with a game's own keys.
    // null = off.

    public Hotkey? TogglePause { get; init; }

    public Hotkey? AddForegroundGame { get; init; }

    public Hotkey? ApplyToForeground { get; init; }

    // Settings window shortcuts can't be turned off, only changed. Same null handling as in AppSettings:
    // a missing key in an older settings.json must give the default, not null.

    public Hotkey Save { get; init => field = value ?? DefaultSave; } = DefaultSave;

    public Hotkey RemoveGame { get; init => field = value ?? DefaultRemoveGame; } = DefaultRemoveGame;

    /// <summary>A sound when a global hotkey was pressed: "it worked" or "not possible" (see HotkeySounds). On by default.</summary>
    [JsonIgnore]
    public bool PlaySounds { get; init; } = true;

    /// <summary>How <see cref="PlaySounds"/> is stored: missing or null means on, like <see cref="ShowMessagesForJson"/>.</summary>
    [JsonInclude]
    [JsonPropertyName("playSounds")]
    internal bool? PlaySoundsForJson
    {
        get => PlaySounds;
        init => PlaySounds = value ?? true;
    }

    /// <summary>A short message over the game when a global hotkey was pressed (see IGameOverlay). On by default.</summary>
    [JsonIgnore]
    public bool ShowMessages { get; init; } = true;

    /// <summary>
    /// How <see cref="ShowMessages"/> is stored: missing (older settings) or null means the default, on. A plain bool
    /// would read a missing key as false, see GameProfile.EnabledForJson.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("showMessages")]
    internal bool? ShowMessagesForJson
    {
        get => ShowMessages;
        init => ShowMessages = value ?? true;
    }

    public static bool IsGlobal(HotkeyAction action) =>
        action is HotkeyAction.TogglePause or HotkeyAction.AddForegroundGame or HotkeyAction.ApplyToForeground;

    public static Hotkey? GetDefault(HotkeyAction action) => action switch
    {
        HotkeyAction.Save => DefaultSave,
        HotkeyAction.RemoveGame => DefaultRemoveGame,
        _ => null,
    };

    public Hotkey? Get(HotkeyAction action) => action switch
    {
        HotkeyAction.TogglePause => TogglePause,
        HotkeyAction.AddForegroundGame => AddForegroundGame,
        HotkeyAction.ApplyToForeground => ApplyToForeground,
        HotkeyAction.Save => Save,
        _ => RemoveGame,
    };

    /// <summary>A copy with one hotkey changed. null for Save/RemoveGame means their default.</summary>
    public HotkeySettings With(HotkeyAction action, Hotkey? hotkey) => action switch
    {
        HotkeyAction.TogglePause => this with { TogglePause = hotkey },
        HotkeyAction.AddForegroundGame => this with { AddForegroundGame = hotkey },
        HotkeyAction.ApplyToForeground => this with { ApplyToForeground = hotkey },
        HotkeyAction.Save => this with { Save = hotkey ?? DefaultSave },
        _ => this with { RemoveGame = hotkey ?? DefaultRemoveGame },
    };
}
