using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

public static class PlacementCalculator
{
    /// <summary>
    /// The aspect ratio at full monitor height, centered horizontally.
    /// Monitors that aren't wider than that ratio (e.g. 16:10 for 16:9) get the full monitor, or with
    /// <paramref name="keepAspectRatio"/> the ratio at full width (centered vertically by <see cref="GetTop"/>).
    /// </summary>
    public static WindowPlacement CalculateAutomatic(MonitorBounds monitor, AspectRatio ratio, bool keepAspectRatio = false)
    {
        var (ratioWidth, ratioHeight) = GetRatio(ratio);
        var width = monitor.Height * ratioWidth / ratioHeight;
        if (width >= monitor.Width)
        {
            var height = keepAspectRatio ? Math.Min(monitor.Height, monitor.Width * ratioHeight / ratioWidth) : monitor.Height;
            return new WindowPlacement(monitor.Width, height, 0);
        }

        return new WindowPlacement(width, monitor.Height, (monitor.Width - width) / 2);
    }

    public static (int Width, int Height) GetRatio(AspectRatio ratio) => ratio switch
    {
        AspectRatio.Standard4x3 => (4, 3),
        AspectRatio.Standard5x4 => (5, 4),
        AspectRatio.Widescreen16x10 => (16, 10),
        // "21:9" is a marketing name: real ultrawide sizes are 43:18 (3440 × 1440) or 64:27 (2560 × 1080).
        // 43:18 gives the usual 3440 at 1440p; at 1080p 2580 instead of 2560, close enough to scale cleanly.
        AspectRatio.Ultrawide21x9 => (43, 18),
        _ => (16, 9),
    };

    /// <summary>
    /// The monitor a placement targets. Used by the watcher and by the settings preview.
    /// A specific display that isn't connected falls back to the primary one (Found = false, so the
    /// caller can log it); the setting itself is kept, so plugging the monitor back in just works.
    /// </summary>
    /// <param name="currentMonitor">
    /// The monitor of the game window (watcher) or of the settings window (preview). Used for
    /// <see cref="DisplayTarget.Current"/> and as last resort if Windows reports no displays at all.
    /// </param>
    public static (MonitorBounds Bounds, bool Found) ResolveDisplay(
        PlacementSettings effective, IReadOnlyList<DisplayInfo> displays, MonitorBounds currentMonitor)
    {
        if (effective.Display == DisplayTarget.Current)
        {
            return (currentMonitor, true);
        }

        if (effective.Display == DisplayTarget.Specific
            && displays.FirstOrDefault(d => string.Equals(d.Id, effective.DisplayId, StringComparison.OrdinalIgnoreCase)) is { } specific)
        {
            return (specific.Bounds, true);
        }

        var primary = displays.FirstOrDefault(d => d.IsPrimary)?.Bounds ?? currentMonitor;
        return (primary, effective.Display == DisplayTarget.Primary);
    }

    /// <summary>Final placement for a game: its own placement if set, otherwise the default.</summary>
    /// <param name="gameSize">
    /// For <see cref="PlacementMode.GameSize"/>: the inner size of the game's window right now. Without it (unknown),
    /// that mode falls back to the automatic size.
    /// </param>
    public static WindowPlacement Resolve(
        PlacementSettings? gamePlacement, PlacementSettings defaultPlacement, MonitorBounds monitor,
        (int Width, int Height)? gameSize = null)
    {
        var effective = gamePlacement ?? defaultPlacement;
        return effective switch
        {
            { Mode: PlacementMode.Custom, Custom: { } custom } => FitToMonitor(custom, monitor),
            { Mode: PlacementMode.GameSize } when gameSize is { Width: > 0, Height: > 0 } size => CenterOnMonitor(size, monitor),
            _ => CalculateAutomatic(monitor, effective.AspectRatio, effective.KeepAspectRatio),
        };
    }

    /// <summary>A window of this size, centered (horizontally here, vertically by <see cref="GetTop"/>), shrunk to fit.</summary>
    public static WindowPlacement CenterOnMonitor((int Width, int Height) size, MonitorBounds monitor)
    {
        var width = Math.Min(size.Width, monitor.Width);
        var height = Math.Min(size.Height, monitor.Height);
        return new WindowPlacement(width, height, (monitor.Width - width) / 2);
    }

    /// <summary>
    /// Top edge of the window: centered vertically. A window as tall as the monitor (the usual case) starts at its top.
    /// Never above the monitor, even for a placement that wasn't passed through <see cref="FitToMonitor"/>.
    /// </summary>
    public static int GetTop(WindowPlacement placement, MonitorBounds monitor) =>
        monitor.Top + (Math.Max(0, monitor.Height - placement.Height) / 2);

    /// <summary>
    /// Shrinks and moves a custom placement so it stays on the monitor. The editor already refuses values that
    /// don't fit, but it can only check the display it knows: the game may be on a smaller one ("the game's
    /// display"), a specific display may be unplugged, the resolution may have changed since, or settings.json
    /// may have been edited by hand.
    /// </summary>
    public static WindowPlacement FitToMonitor(WindowPlacement placement, MonitorBounds monitor)
    {
        var width = Math.Min(placement.Width, monitor.Width);
        var height = Math.Min(placement.Height, monitor.Height);
        var offsetX = Math.Min(placement.OffsetX, monitor.Width - width);

        return (width, height, offsetX) == (placement.Width, placement.Height, placement.OffsetX)
            ? placement
            : new WindowPlacement(width, height, offsetX);
    }
}
