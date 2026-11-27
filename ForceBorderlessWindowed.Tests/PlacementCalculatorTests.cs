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
        Assert.Equal(new WindowRect(1920, 1080, 0, 60), PlacementCalculator.Resolve(null, new PlacementSettings { KeepAspectRatio = true }, monitor));

        // A wider monitor isn't affected: full height as always
        Assert.Equal(new WindowPlacement(2560, 1440, 440), PlacementCalculator.CalculateAutomatic(Ultrawide, AspectRatio.Widescreen16x9, keepAspectRatio: true));
    }

    [Fact]
    public void Resolve_UsesTheGamesOwnPlacement_BeforeTheDefault()
    {
        var game = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100) };

        Assert.Equal(new WindowRect(1280, 720, 100, 180), PlacementCalculator.Resolve(game, new PlacementSettings(), FullHd));
        Assert.Equal(new WindowRect(1920, 1080, 0, 0), PlacementCalculator.Resolve(null, new PlacementSettings(), FullHd));
    }

    [Fact]
    public void Resolve_CustomWithoutValues_FallsBackToAutomatic()
    {
        var game = new PlacementSettings { Mode = PlacementMode.Custom, Custom = null };

        Assert.Equal(new WindowRect(1920, 1080, 0, 0), PlacementCalculator.Resolve(game, new PlacementSettings(), FullHd));
    }

    [Fact]
    public void Resolve_GameSize_CentersTheGamesSize_AndShrinksItToFit()
    {
        var gameSize = new PlacementSettings { Mode = PlacementMode.GameSize };

        Assert.Equal(new WindowRect(1280, 720, 320, 180), PlacementCalculator.Resolve(gameSize, new PlacementSettings(), FullHd, (1280, 720)));
        Assert.Equal(new WindowRect(1920, 1080, 0, 0), PlacementCalculator.Resolve(gameSize, new PlacementSettings(), FullHd, (2560, 1440)));
    }

    [Fact]
    public void Resolve_GameSize_WithoutASize_FallsBackToAutomatic()
    {
        var gameSize = new PlacementSettings { Mode = PlacementMode.GameSize };

        Assert.Equal(new WindowRect(2560, 1440, 440, 0), PlacementCalculator.Resolve(null, gameSize, Ultrawide));
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

    [Theory]
    [InlineData(HorizontalAnchor.Left, VerticalAnchor.Top, 0, 0)]
    [InlineData(HorizontalAnchor.Center, VerticalAnchor.Center, 320, 180)]
    [InlineData(HorizontalAnchor.Right, VerticalAnchor.Bottom, 640, 360)]
    [InlineData(HorizontalAnchor.Offset, VerticalAnchor.Center, 100, 180)]
    public void Position_PutsTheWindowAtItsAnchors(HorizontalAnchor horizontal, VerticalAnchor vertical, int x, int y)
    {
        Assert.Equal(new WindowRect(1280, 720, x, y),
            PlacementCalculator.Position(new WindowPlacement(1280, 720, 100), horizontal, vertical, FullHd));
    }

    [Fact]
    public void Position_NeverAboveOrLeftOfTheMonitor()
    {
        Assert.Equal(new WindowRect(2000, 2000, 0, 0),
            PlacementCalculator.Position(new WindowPlacement(2000, 2000, 0), HorizontalAnchor.Right, VerticalAnchor.Bottom, FullHd));
    }

    [Fact]
    public void Resolve_AnchorsFollowTheDisplay_InEveryMode()
    {
        var left = new PlacementSettings { Horizontal = HorizontalAnchor.Left, Vertical = VerticalAnchor.Top };

        Assert.Equal(new WindowRect(2560, 1440, 0, 0), PlacementCalculator.Resolve(left, new PlacementSettings(), Ultrawide));
        Assert.Equal(new WindowRect(1280, 720, 0, 0),
            PlacementCalculator.Resolve(left with { Mode = PlacementMode.GameSize }, new PlacementSettings(), FullHd, (1280, 720)));

        var right = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100), Horizontal = HorizontalAnchor.Right };
        Assert.Equal(new WindowRect(1280, 720, 640, 180), PlacementCalculator.Resolve(right, new PlacementSettings(), FullHd));
        Assert.Equal(new WindowRect(1280, 720, 2160, 360), PlacementCalculator.Resolve(right, new PlacementSettings(), Ultrawide));
    }

    [Fact]
    public void Resolve_SettingsFromOlderVersions_KeepTheirPositions()
    {
        // No "horizontal" in the file: Custom used its offset, the other modes were centered
        var custom = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100) };
        Assert.Equal(HorizontalAnchor.Offset, custom.EffectiveHorizontal());
        Assert.Equal(HorizontalAnchor.Center, new PlacementSettings().EffectiveHorizontal());

        // The offset only exists for Custom
        Assert.Equal(HorizontalAnchor.Center, new PlacementSettings { Horizontal = HorizontalAnchor.Offset }.EffectiveHorizontal());
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
