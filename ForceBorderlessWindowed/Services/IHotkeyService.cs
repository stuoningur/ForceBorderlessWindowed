using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>The global hotkeys (work in any program). Used on the UI thread only.</summary>
public interface IHotkeyService
{
    /// <summary>Global hotkeys from the saved settings that Windows refused, usually because another program has them.</summary>
    IReadOnlySet<HotkeyAction> FailedHotkeys { get; }

    /// <summary>Raised when <see cref="FailedHotkeys"/> changed.</summary>
    event EventHandler? FailedHotkeysChanged;

    /// <summary>
    /// Turns the global hotkeys off until the result is disposed, e.g. while a hotkey field has the focus:
    /// pressing the current combination there should record it, not pause detection.
    /// </summary>
    IDisposable Suspend();

    /// <summary>
    /// Whether Windows would accept the combination right now: false if another program registered it.
    /// Tried by registering it for a moment. Can't see programs that read the keyboard directly (keyboard hooks,
    /// games), and one of this app's own registered hotkeys counts as available.
    /// </summary>
    bool IsAvailable(Hotkey hotkey);

    /// <summary>Tries the refused hotkeys again, e.g. when the settings window opens: the other program may be gone.</summary>
    void RetryFailed();
}
