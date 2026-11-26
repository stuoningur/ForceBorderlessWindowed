using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// "Show on display" in the placement editor: briefly outlines on the real monitor where a game window will be.
/// Implemented by the view (Views/ScreenPreviewOverlay), an interface so the ViewModels stay free of windows.
/// </summary>
public interface IScreenPreview
{
    /// <summary>
    /// Shows the outline of <paramref name="placement"/> on <paramref name="monitor"/> for a few seconds, replacing one
    /// that is still showing. UI thread only.
    /// </summary>
    /// <param name="label">Shown inside the outline, e.g. "2560 × 1440".</param>
    void Show(MonitorBounds monitor, WindowPlacement placement, string label);
}
