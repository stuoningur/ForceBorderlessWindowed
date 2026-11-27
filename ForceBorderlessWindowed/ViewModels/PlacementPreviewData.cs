namespace ForceBorderlessWindowed.ViewModels;

/// <summary>Input for the placement preview drawing, all in physical pixels.</summary>
/// <param name="X">Window offset from the monitor's left edge.</param>
/// <param name="Y">Window offset from the monitor's top edge.</param>
/// <param name="Exceeds">The window doesn't fit on the monitor.</param>
/// <param name="Label">
/// Shown in the window instead of its size, which is then only an example: the game decides it ("Game's size").
/// </param>
public sealed record PlacementPreviewData(
    int MonitorWidth, int MonitorHeight, int X, int Y, int Width, int Height, bool Exceeds, string? Label = null);
