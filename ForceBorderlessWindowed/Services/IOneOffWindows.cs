namespace ForceBorderlessWindowed.Services;

/// <summary>What the "make the window in the foreground borderless" hotkey did.</summary>
public enum OneOffResult
{
    /// <summary>Made borderless (with the default placement) until it closes, Pause, exit, or the hotkey again.</summary>
    Added,

    /// <summary>It was one already: put back the way it was.</summary>
    Restored,

    /// <summary>Not possible: detection is paused.</summary>
    Paused,

    /// <summary>Not possible: the window has no frame (fullscreen or its own borderless mode), or is gone.</summary>
    NoFrame,

    /// <summary>Not needed: a configured game's window, which the watcher handles anyway.</summary>
    AlreadyAGame,

    /// <summary>Windows refused the change or the restore (the program probably runs as administrator).</summary>
    AccessDenied,

    /// <summary>The change or the restore failed for another reason; details in the log.</summary>
    Failed,
}

/// <summary>
/// Windows made borderless just once with a hotkey, without adding their program to the games (see
/// <see cref="Models.HotkeyAction.ApplyToForeground"/>). Implemented by the watcher.
/// </summary>
public interface IOneOffWindows
{
    /// <summary>
    /// Makes the window borderless, or puts it back if it already is one. Any thread; the work happens on the watcher
    /// thread, and <paramref name="done"/> is called there with the result.
    /// </summary>
    void ToggleOneOff(nint windowHandle, Action<OneOffResult> done);
}
