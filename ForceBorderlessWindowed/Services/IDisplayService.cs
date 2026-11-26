using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

public interface IDisplayService
{
    /// <summary>The monitor that contains most of the given window (or the nearest one).</summary>
    MonitorBounds GetMonitorForWindow(nint windowHandle);

    /// <summary>All connected displays, ordered by Windows' display number. Never throws; empty only if Windows reports none.</summary>
    IReadOnlyList<DisplayInfo> GetDisplays();
}
