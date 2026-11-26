namespace ForceBorderlessWindowed.Models;

/// <summary>A connected display.</summary>
/// <param name="Id">Stable device path of the monitor (survives reboots, unlike "\\.\DISPLAY1").</param>
/// <param name="Name">Monitor name such as "DELL U3423WE"; a generic "Display 2" if Windows has none (e.g. laptop panels).</param>
/// <param name="Number">The number from Windows' device name (\\.\DISPLAY2 → 2), for telling identical monitors apart.</param>
public sealed record DisplayInfo(string Id, string Name, int Number, MonitorBounds Bounds, bool IsPrimary);
