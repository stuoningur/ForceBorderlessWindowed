using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

public enum WindowChangeResult
{
    /// <summary>The window was changed.</summary>
    Applied,

    /// <summary>The window was already in the desired state; nothing was touched.</summary>
    NoChange,

    /// <summary>Not touched: the window is gone or minimized. Try again later.</summary>
    Skipped,

    /// <summary>
    /// Not touched: Windows considers the window hung ("not responding"), usually while the game loads. Changing it
    /// would wait for the game until it responds again. Try again later.
    /// </summary>
    NotResponding,

    /// <summary>Windows refused the change, typically because the game runs as administrator and we don't.</summary>
    AccessDenied,

    /// <summary>Any other Win32 error (logged by the caller).</summary>
    Failed,
}

/// <summary>
/// The window's state before we changed it, for <see cref="IWindowManipulator.Restore"/>.
/// Opaque on purpose: only the manipulator knows what's inside.
/// </summary>
public sealed class WindowSnapshot
{
    // Not "required": required members can't be less visible than the (public) class
    internal uint Style { get; init; }
    internal uint ExStyle { get; init; }

    /// <summary>Size and position the window has when not maximized, in "workspace" coordinates (see GetWindowPlacement).</summary>
    internal (int Left, int Top, int Right, int Bottom) NormalRect { get; init; }

    internal bool WasMaximized { get; init; }
}

/// <summary>
/// How a window looked right after our change, for <see cref="IWindowManipulator.GameSwitchedMode"/>.
/// Opaque like <see cref="WindowSnapshot"/>.
/// </summary>
public sealed class WindowLook
{
    internal uint Style { get; init; }

    /// <summary>The rectangle we put the window in (screen coordinates).</summary>
    internal (int Left, int Top, int Right, int Bottom) Bounds { get; init; }
}

/// <summary>
/// Stateless Win32 operations on another process's window. Remembering originals, deciding when to act
/// and retrying is the caller's job. Windows that Windows considers hung are skipped, but a window that is
/// only busy for a moment can still block the style changes, so never call these on the UI thread.
/// </summary>
public interface IWindowManipulator
{
    /// <summary>False once the window has been closed.</summary>
    bool Exists(nint windowHandle);

    /// <summary>True if the window still has a title bar or sizing border.</summary>
    bool HasFrame(nint windowHandle);

    /// <summary>The current state, to be passed to <see cref="Restore"/> later. null if the window is gone.</summary>
    WindowSnapshot? Capture(nint windowHandle);

    /// <summary>
    /// The size of the window's inner area (without title bar and borders) in physical pixels: what the game draws in.
    /// null if the window is gone. Doesn't wait for the game.
    /// </summary>
    (int Width, int Height)? GetClientSize(nint windowHandle);

    /// <summary>
    /// Removes title bar and borders and moves the window to <paramref name="placement"/> on
    /// <paramref name="monitor"/>. Does nothing if it's already there.
    /// </summary>
    /// <param name="modified">
    /// True if the window was changed at all, even when a later step failed (e.g. un-maximized and styles
    /// changed, but moving it was refused). Such a half-changed window still needs restoring.
    /// </param>
    WindowChangeResult MakeBorderless(nint windowHandle, WindowRect placement, MonitorBounds monitor, out bool modified);

    /// <summary>
    /// The window's look now (right after our change to <paramref name="placement"/> on <paramref name="monitor"/>),
    /// to check later whether the game switched it to a mode of its own. null if gone.
    /// </summary>
    WindowLook? CaptureLook(nint windowHandle, WindowRect placement, MonitorBounds monitor);

    /// <summary>
    /// True if the game has switched the window to its own fullscreen or borderless mode since <paramref name="look"/>:
    /// it has no frame, it was restyled, and there is a sign that the restyle was a mode switch: the popup style
    /// was turned on or off, or the window now fills its monitor at a size we didn't set.
    /// A restyle without such a sign (some other style bit) is not a switch. Neither are minimized, maximized,
    /// hidden or disabled: those are states the game or the user change all the time, not the window's mode.
    /// </summary>
    bool GameSwitchedMode(nint windowHandle, WindowLook look);

    /// <summary>Puts back styles, size, position and maximized state from <paramref name="snapshot"/>.</summary>
    WindowChangeResult Restore(nint windowHandle, WindowSnapshot snapshot);
}
