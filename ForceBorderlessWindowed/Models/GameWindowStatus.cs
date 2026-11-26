namespace ForceBorderlessWindowed.Models;

/// <summary>
/// What the watcher could do with a running game's window.
/// Ordered by severity: if a game has several windows/processes, the highest value is shown.
/// </summary>
public enum GameWindowStatus
{
    /// <summary>Made borderless, everything fine.</summary>
    Borderless,

    /// <summary>The window has no frame (probably fullscreen), so it's left alone on purpose.</summary>
    NoFrame,

    /// <summary>The game isn't responding (usually loading); its window is changed once it responds again.</summary>
    NotResponding,

    /// <summary>The game kept undoing our change; paused for a while (fight limit).</summary>
    FightPaused,

    /// <summary>A Win32 call failed for another reason; details in the log.</summary>
    Failed,

    /// <summary>The game runs as administrator, Windows doesn't let us change its window.</summary>
    AccessDenied,
}
