namespace ForceBorderlessWindowed.ViewModels;

/// <summary>Input for the placement preview drawing, all in physical pixels.</summary>
/// <param name="X">Window offset from the monitor's left edge (the window is centered vertically).</param>
/// <param name="Exceeds">The window doesn't fit on the monitor.</param>
/// <param name="Label">
/// Shown in the window instead of its size, which is then only an example: the game decides it ("Game's size").
/// </param>
public sealed record PlacementPreviewData(
    int MonitorWidth, int MonitorHeight, int X, int Width, int Height, bool Exceeds, string? Label = null);
