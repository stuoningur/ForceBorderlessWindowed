using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests;

public class PlacementCalculatorTests
{
    private static readonly MonitorBounds Ultrawide = new(0, 0, 3440, 1440);
    private static readonly MonitorBounds FullHd = new(1920, 0, 1920, 1080);

    [Theory]
    [InlineData(AspectRatio.Widescreen16x9, 2560, 440)]
    [InlineData(AspectRatio.Standard4x3, 1920, 760)]
    [InlineData(AspectRatio.Standard5x4, 1800, 820)]
    [InlineData(AspectRatio.Widescreen16x10, 2304, 568)]
    [InlineData(AspectRatio.Ultrawide21x9, 3440, 0)]
    public void Automatic_UsesFullHeight_AndCentersHorizontally(AspectRatio ratio, int width, int offsetX)
    {
        Assert.Equal(new WindowPlacement(width, 1440, offsetX), PlacementCalculator.CalculateAutomatic(Ultrawide, ratio));
    }

    [Fact]
    public void Automatic_OnANarrowerMonitor_GetsTheWholeMonitor()
    {
        var monitor = new MonitorBounds(0, 0, 1920, 1200);   // 16:10, narrower than 16:9 at full height

        Assert.Equal(new WindowPlacement(1920, 1200, 0), PlacementCalculator.CalculateAutomatic(monitor, AspectRatio.Widescreen16x9));
    }

    [Fact]
    public void Automatic_KeepAspectRatio_OnANarrowerMonitor_KeepsTheShape()
    {
        var monitor = new MonitorBounds(0, 0, 1920, 1200);

        Assert.Equal(new WindowPlacement(1920, 1080, 0), PlacementCalculator.CalculateAutomatic(monitor, AspectRatio.Widescreen16x9, keepAspectRatio: true));
        Assert.Equal(60, PlacementCalculator.GetTop(new WindowPlacement(1920, 1080, 0), monitor));

        // A wider monitor isn't affected: full height as always
        Assert.Equal(new WindowPlacement(2560, 1440, 440), PlacementCalculator.CalculateAutomatic(Ultrawide, AspectRatio.Widescreen16x9, keepAspectRatio: true));
    }

    [Fact]
    public void Resolve_UsesTheGamesOwnPlacement_BeforeTheDefault()
    {
        var game = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100) };

        Assert.Equal(new WindowPlacement(1280, 720, 100), PlacementCalculator.Resolve(game, new PlacementSettings(), FullHd));
        Assert.Equal(new WindowPlacement(1920, 1080, 0), PlacementCalculator.Resolve(null, new PlacementSettings(), FullHd));
    }

    [Fact]
    public void Resolve_CustomWithoutValues_FallsBackToAutomatic()
    {
        var game = new PlacementSettings { Mode = PlacementMode.Custom, Custom = null };

        Assert.Equal(new WindowPlacement(1920, 1080, 0), PlacementCalculator.Resolve(game, new PlacementSettings(), FullHd));
    }

    [Fact]
    public void Resolve_GameSize_CentersTheGamesSize_AndShrinksItToFit()
    {
        var gameSize = new PlacementSettings { Mode = PlacementMode.GameSize };

        Assert.Equal(new WindowPlacement(1280, 720, 320), PlacementCalculator.Resolve(gameSize, new PlacementSettings(), FullHd, (1280, 720)));
        Assert.Equal(new WindowPlacement(1920, 1080, 0), PlacementCalculator.Resolve(gameSize, new PlacementSettings(), FullHd, (2560, 1440)));
    }

    [Fact]
    public void Resolve_GameSize_WithoutASize_FallsBackToAutomatic()
    {
        var gameSize = new PlacementSettings { Mode = PlacementMode.GameSize };

        Assert.Equal(new WindowPlacement(2560, 1440, 440), PlacementCalculator.Resolve(null, gameSize, Ultrawide));
    }

    [Fact]
    public void FitToMonitor_ShrinksAndMovesTooLargePlacements()
    {
        var placement = new WindowPlacement(2560, 1440, 500);

        Assert.Equal(new WindowPlacement(1920, 1080, 0), PlacementCalculator.FitToMonitor(placement, FullHd));
        Assert.Equal(new WindowPlacement(1000, 800, 920), PlacementCalculator.FitToMonitor(new WindowPlacement(1000, 800, 2000), FullHd));
    }

    [Fact]
    public void FitToMonitor_KeepsAFittingPlacement_AsItIs()
    {
        var placement = new WindowPlacement(1280, 720, 10);

        Assert.Same(placement, PlacementCalculator.FitToMonitor(placement, FullHd));
    }

    [Fact]
    public void GetTop_CentersVertically_NeverAboveTheMonitor()
    {
        var monitor = new MonitorBounds(0, 100, 1920, 1080);

        Assert.Equal(100 + 180, PlacementCalculator.GetTop(new WindowPlacement(1280, 720, 0), monitor));
        Assert.Equal(100, PlacementCalculator.GetTop(new WindowPlacement(1280, 2000, 0), monitor));
    }

    [Fact]
    public void ResolveDisplay_FindsTheSpecificDisplay()
    {
        DisplayInfo[] displays =
        [
            new("a", "A", 1, Ultrawide, IsPrimary: true),
            new("b", "B", 2, FullHd, IsPrimary: false),
        ];
        var placement = new PlacementSettings { Display = DisplayTarget.Specific, DisplayId = "B" };   // case doesn't matter

        Assert.Equal((FullHd, true), PlacementCalculator.ResolveDisplay(placement, displays, Ultrawide));
    }

    [Fact]
    public void ResolveDisplay_MissingDisplay_FallsBackToPrimary_AndSaysSo()
    {
        DisplayInfo[] displays = [new("a", "A", 1, Ultrawide, IsPrimary: true)];
        var placement = new PlacementSettings { Display = DisplayTarget.Specific, DisplayId = "unplugged" };

        Assert.Equal((Ultrawide, false), PlacementCalculator.ResolveDisplay(placement, displays, FullHd));
    }

    [Fact]
    public void ResolveDisplay_Current_UsesTheWindowsMonitor()
    {
        DisplayInfo[] displays = [new("a", "A", 1, Ultrawide, IsPrimary: true)];
        var placement = new PlacementSettings { Display = DisplayTarget.Current };

        Assert.Equal((FullHd, true), PlacementCalculator.ResolveDisplay(placement, displays, FullHd));
    }

    [Theory]
    [InlineData(1, 1, 0, true)]
    [InlineData(32767, 32767, 32767, true)]
    [InlineData(0, 100, 0, false)]
    [InlineData(100, 0, 0, false)]
    [InlineData(100, 100, -1, false)]
    [InlineData(32768, 100, 0, false)]
    public void IsValidSize_LimitsTheValues(int width, int height, int offsetX, bool valid)
    {
        Assert.Equal(valid, WindowPlacement.IsValidSize(width, height, offsetX));
    }
}
