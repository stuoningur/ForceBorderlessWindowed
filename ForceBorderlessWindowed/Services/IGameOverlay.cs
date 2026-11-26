namespace ForceBorderlessWindowed.Services;

/// <summary>The look of an overlay message: the badge in front of the text.</summary>
public enum OverlayKind
{
    /// <summary>Green check: it worked.</summary>
    Success,

    /// <summary>Neutral "i": nothing went wrong, but nothing was done either (already there, paused...).</summary>
    Info,

    /// <summary>Caution: not possible, and why.</summary>
    Warning,
}

/// <summary>
/// A short message shown directly over the game (centered, a quarter of the way down the display the foreground window
/// is on), for the global hotkeys: in a game the tray icon and Windows notifications are out of sight. Never takes the
/// focus and lets clicks through; fades out by itself. Implemented by the view (Views/GameOverlay).
/// </summary>
public interface IGameOverlay
{
    /// <summary>Shows the message, replacing one still on screen. Any thread. Does nothing if turned off in the settings.</summary>
    void Show(string message, OverlayKind kind);
}
