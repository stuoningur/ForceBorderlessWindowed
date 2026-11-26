namespace ForceBorderlessWindowed.Models;

/// <summary>Position and size of a monitor in physical pixels (virtual desktop coordinates).</summary>
public readonly record struct MonitorBounds(int Left, int Top, int Width, int Height);
