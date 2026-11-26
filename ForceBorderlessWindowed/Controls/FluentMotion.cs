using System.Windows;
using System.Windows.Media.Animation;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// Shared Fluent motion values, so everything that animates in moves the same way
/// (InfoBar entrance, tab content transition). Callers check SystemParameters.ClientAreaAnimation themselves.
/// </summary>
internal static class FluentMotion
{
    // Fluent motion: "Direct entrance", cubic-bezier(0, 0, 0, 1). Fade is shorter than the slide, like WinUI.
    public static readonly KeySpline DirectEntrance = new(0, 0, 0, 1);
    public static readonly Duration SlideDuration = new(TimeSpan.FromMilliseconds(250));
    public static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(167));

    /// <summary>Entrance animation from <paramref name="from"/> to <paramref name="to"/> on the direct entrance curve.</summary>
    public static DoubleAnimationUsingKeyFrames Spline(double from, double to, Duration duration)
    {
        // FillBehavior.Stop + final value = base value: no animation keeps holding the property afterwards
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration, FillBehavior = FillBehavior.Stop };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromTimeSpan(duration.TimeSpan), DirectEntrance));
        animation.Freeze();
        return animation;
    }
}
