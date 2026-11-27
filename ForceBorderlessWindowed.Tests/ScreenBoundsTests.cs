using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests;

/// <summary>Where a placement lands in screen coordinates, and the range check before it reaches Win32.</summary>
public class ScreenBoundsTests
{
    [Fact]
    public void Placement_IsOffsetByTheMonitorPosition()
    {
        var bounds = WindowManipulator.ScreenBounds(new WindowRect(1920, 1080, 100, 50), new MonitorBounds(-2560, 200, 2560, 1440));

        Assert.Equal((-2460, 250, -540, 1330), bounds);
    }

    [Theory]
    [InlineData(int.MaxValue, 0, 0, 0)]          // left
    [InlineData(0, int.MaxValue, 0, 0)]          // top
    [InlineData(int.MaxValue - 10, 0, 100, 0)]   // right edge only
    [InlineData(0, int.MaxValue - 10, 0, 100)]   // bottom edge only
    public void AnyEdgeOutOfRange_GivesNoBounds(int monitorLeft, int monitorTop, int extraWidth, int extraHeight)
    {
        var placement = new WindowRect(10 + extraWidth, 10 + extraHeight, monitorLeft == int.MaxValue ? 1 : 0, monitorTop == int.MaxValue ? 1 : 0);

        Assert.Null(WindowManipulator.ScreenBounds(placement, new MonitorBounds(monitorLeft, monitorTop, 100, 100)));
    }
}
