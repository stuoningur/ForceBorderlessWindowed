using System.Globalization;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// Small drawing of a monitor with the game window on it, scaled to fit. Changes glide to the new
/// position (Fluent "existing elements" motion) unless animations are turned off in Windows.
/// Colors are brush properties so the implicit style in Styles.xaml can set them as DynamicResource
/// theme brushes: they then follow light/dark/high contrast like everything else.
/// </summary>
public class PlacementPreview : FrameworkElement
{
    private const double Bezel = 5;
    private const double NeckHeight = 8;
    private const double BaseHeight = 4;
    private const double LabelFontSize = 12;   // Fluent "Caption", the smallest recommended size

    // Fluent motion: "Existing elements", cubic-bezier(0.55, 0.55, 0, 1), 250 ms
    private static readonly KeySpline ExistingElements = new(0.55, 0.55, 0, 1);
    private static readonly TimeSpan AnimationDuration = TimeSpan.FromMilliseconds(250);

    /// <summary>Monitor and window size in physical pixels: what is drawn, interpolated during an animation.</summary>
    private readonly record struct Layout(double MonitorWidth, double MonitorHeight, double X, double Width, double Height)
    {
        public static Layout Lerp(Layout a, Layout b, double t) => new(
            a.MonitorWidth + ((b.MonitorWidth - a.MonitorWidth) * t),
            a.MonitorHeight + ((b.MonitorHeight - a.MonitorHeight) * t),
            a.X + ((b.X - a.X) * t),
            a.Width + ((b.Width - a.Width) * t),
            a.Height + ((b.Height - a.Height) * t));
    }

    private Layout _from;
    private Layout _to;

    public PlacementPreview()
    {
        ClipToBounds = true;
    }

    #region Values

    public static readonly DependencyProperty MonitorWidthProperty = RegisterValue(nameof(MonitorWidth));
    public static readonly DependencyProperty MonitorHeightProperty = RegisterValue(nameof(MonitorHeight));
    public static readonly DependencyProperty WindowXProperty = RegisterValue(nameof(WindowX));
    public static readonly DependencyProperty WindowWidthProperty = RegisterValue(nameof(WindowWidth));
    public static readonly DependencyProperty WindowHeightProperty = RegisterValue(nameof(WindowHeight));

    public int MonitorWidth { get => (int)GetValue(MonitorWidthProperty); set => SetValue(MonitorWidthProperty, value); }
    public int MonitorHeight { get => (int)GetValue(MonitorHeightProperty); set => SetValue(MonitorHeightProperty, value); }
    public int WindowX { get => (int)GetValue(WindowXProperty); set => SetValue(WindowXProperty, value); }
    public int WindowWidth { get => (int)GetValue(WindowWidthProperty); set => SetValue(WindowWidthProperty, value); }
    public int WindowHeight { get => (int)GetValue(WindowHeightProperty); set => SetValue(WindowHeightProperty, value); }

    public static readonly DependencyProperty IsOverflowProperty = DependencyProperty.Register(
        nameof(IsOverflow), typeof(bool), typeof(PlacementPreview),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>The window doesn't fit on the monitor: drawn in the caution color.</summary>
    public bool IsOverflow { get => (bool)GetValue(IsOverflowProperty); set => SetValue(IsOverflowProperty, value); }

    public static readonly DependencyProperty SizeLabelProperty = DependencyProperty.Register(
        nameof(SizeLabel), typeof(string), typeof(PlacementPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Shown in the window instead of its size: the size is only an example then (the game decides it), and the
    /// outline is dashed to say so.
    /// </summary>
    public string? SizeLabel { get => (string?)GetValue(SizeLabelProperty); set => SetValue(SizeLabelProperty, value); }

    private static DependencyProperty RegisterValue(string name) => DependencyProperty.Register(
        name, typeof(int), typeof(PlacementPreview), new PropertyMetadata(0, OnValueChanged));

    #endregion

    #region Brushes

    public static readonly DependencyProperty BezelBrushProperty = RegisterBrush(nameof(BezelBrush));
    public static readonly DependencyProperty ScreenBrushProperty = RegisterBrush(nameof(ScreenBrush));
    public static readonly DependencyProperty TaskbarBrushProperty = RegisterBrush(nameof(TaskbarBrush));
    public static readonly DependencyProperty WindowBrushProperty = RegisterBrush(nameof(WindowBrush));
    public static readonly DependencyProperty OverflowBrushProperty = RegisterBrush(nameof(OverflowBrush));
    public static readonly DependencyProperty TextBrushProperty = RegisterBrush(nameof(TextBrush));

    public Brush? BezelBrush { get => (Brush?)GetValue(BezelBrushProperty); set => SetValue(BezelBrushProperty, value); }
    public Brush? ScreenBrush { get => (Brush?)GetValue(ScreenBrushProperty); set => SetValue(ScreenBrushProperty, value); }
    public Brush? TaskbarBrush { get => (Brush?)GetValue(TaskbarBrushProperty); set => SetValue(TaskbarBrushProperty, value); }

    /// <summary>Outline of the game window; the fill is a translucent version of it.</summary>
    public Brush? WindowBrush { get => (Brush?)GetValue(WindowBrushProperty); set => SetValue(WindowBrushProperty, value); }

    public Brush? OverflowBrush { get => (Brush?)GetValue(OverflowBrushProperty); set => SetValue(OverflowBrushProperty, value); }
    public Brush? TextBrush { get => (Brush?)GetValue(TextBrushProperty); set => SetValue(TextBrushProperty, value); }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(PlacementPreview),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    #endregion

    /// <summary>0 → 1 while gliding from the old to the new geometry.</summary>
    private static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        "Progress", typeof(double), typeof(PlacementPreview),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private double Progress => (double)GetValue(ProgressProperty);

    private Layout Target => new(MonitorWidth, MonitorHeight, WindowX, WindowWidth, WindowHeight);

    private Layout Current => Layout.Lerp(_from, _to, Progress);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PlacementPreview)d).ScheduleTransition();

    /// <summary>
    /// Width, height and offset usually change together (one after the other): collect them and
    /// start one transition, from where the drawing was before the first of them changed.
    /// </summary>
    private void ScheduleTransition()
    {
        if (_transitionPending)
        {
            return;
        }

        _transitionPending = true;
        _pendingFrom = Current;
        Dispatcher.BeginInvoke(StartTransition, DispatcherPriority.Render);
    }

    private bool _transitionPending;
    private Layout _pendingFrom;

    private void StartTransition()
    {
        _transitionPending = false;

        // First values, or nothing visible yet: jump, there is nothing to glide from
        var animate = IsLoaded && SystemParameters.ClientAreaAnimation && _to.MonitorWidth > 0;
        _from = animate ? _pendingFrom : Target;
        _to = Target;

        if (!animate)
        {
            BeginAnimation(ProgressProperty, null);
            InvalidateVisual();
            return;
        }

        var animation = new DoubleAnimationUsingKeyFrames { Duration = AnimationDuration };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(1, KeyTime.FromTimeSpan(AnimationDuration), ExistingElements));
        animation.Freeze();
        BeginAnimation(ProgressProperty, animation);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var g = Current;
        if (g.MonitorWidth <= 0 || g.MonitorHeight <= 0 || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        // Scale so that monitor AND window fit (an oversized window sticks out and stays visible)
        var worldWidth = Math.Max(g.MonitorWidth, g.X + g.Width);
        var worldHeight = Math.Max(g.MonitorHeight, g.Height);
        var availableHeight = ActualHeight - NeckHeight - BaseHeight - (2 * Bezel);
        var scale = Math.Min((ActualWidth - (2 * Bezel)) / worldWidth, availableHeight / worldHeight);
        if (scale <= 0)
        {
            return;
        }

        // Left-aligned like the rest of the page
        var screen = new Rect(Bezel, Bezel, g.MonitorWidth * scale, g.MonitorHeight * scale);
        var bezel = Rect.Inflate(screen, Bezel, Bezel);

        // Stand: neck and foot, centered under the monitor
        var centerX = bezel.Left + (bezel.Width / 2);
        var neckWidth = Math.Max(6, bezel.Width * 0.1);
        var baseWidth = Math.Max(16, bezel.Width * 0.3);
        dc.DrawRectangle(BezelBrush, null, new Rect(centerX - (neckWidth / 2), bezel.Bottom, neckWidth, NeckHeight));
        dc.DrawRoundedRectangle(BezelBrush, null,
            new Rect(centerX - (baseWidth / 2), bezel.Bottom + NeckHeight, baseWidth, BaseHeight), 2, 2);

        dc.DrawRoundedRectangle(BezelBrush, null, bezel, 6, 6);
        dc.DrawRectangle(ScreenBrush, null, screen);

        // Hint of a taskbar, so it's clear where "top" is and that the window covers it (or not)
        var taskbarHeight = Math.Max(3, screen.Height * 0.04);
        dc.DrawRectangle(TaskbarBrush, null, new Rect(screen.Left, screen.Bottom - taskbarHeight, screen.Width, taskbarHeight));

        if (g.Width <= 0 || g.Height <= 0)
        {
            return;
        }

        var outline = IsOverflow ? OverflowBrush : WindowBrush;
        // Centered vertically like the real window; one that is too tall (still being typed) hangs from the top
        var top = Math.Max(0, (g.MonitorHeight - g.Height) / 2);
        var window = new Rect(screen.Left + (g.X * scale), screen.Top + (top * scale), g.Width * scale, g.Height * scale);
        var pen = new Pen(outline, 1.5);
        if (SizeLabel is not null)
        {
            pen.DashStyle = new DashStyle([3, 2], 0);
        }

        dc.DrawRectangle(Translucent(outline), pen, Inset(window, 0.75));

        DrawSizeLabel(dc, window, _to);
    }

    /// <summary>"2560 × 1440" in the window, only if it fits. Shows the target size, not the animated one.</summary>
    private void DrawSizeLabel(DrawingContext dc, Rect window, Layout target)
    {
        var text = new FormattedText(
            SizeLabel ?? string.Create(CultureInfo.CurrentCulture, $"{target.Width:0} × {target.Height:0}"),
            CultureInfo.CurrentCulture,
            FlowDirection,
            new Typeface(TextElementFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            LabelFontSize,
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        if (text.Width + 8 <= window.Width && text.Height + 4 <= window.Height)
        {
            dc.DrawText(text, new Point(
                window.Left + ((window.Width - text.Width) / 2),
                window.Top + ((window.Height - text.Height) / 2)));
        }
    }

    private FontFamily TextElementFontFamily => (FontFamily)GetValue(TextElement.FontFamilyProperty);

    private static Rect Inset(Rect rect, double amount) =>
        rect.Width > 2 * amount && rect.Height > 2 * amount ? Rect.Inflate(rect, -amount, -amount) : rect;

    private static SolidColorBrush? Translucent(Brush? brush)
    {
        if (brush is not SolidColorBrush solid)
        {
            return null;
        }

        var fill = new SolidColorBrush(solid.Color) { Opacity = 0.3 };
        fill.Freeze();
        return fill;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PlacementPreviewAutomationPeer(this);

    /// <summary>Exposed as an image; name and help text (the summary) come from AutomationProperties in XAML.</summary>
    private sealed class PlacementPreviewAutomationPeer(PlacementPreview owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PlacementPreview);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;
    }
}
