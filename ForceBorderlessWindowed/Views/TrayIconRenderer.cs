using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

using DrawingIcon = System.Drawing.Icon;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// Draws the tray icons while the app runs: the normal one, and the paused one (greyed out, with a pause badge in the
/// bottom-right corner). The artwork comes in two files, one per taskbar color: TrayIcon.DarkTaskbar.ico (light
/// artwork) and TrayIcon.LightTaskbar.ico (dark artwork). The paused icon is made from the same file, so new artwork
/// gets the badge without extra files.
/// Drawn at the exact pixel size of the notification area, so the badge's bars stay sharp: Windows would blur them
/// when it scales a bigger image down. If display scaling changes while the app runs, Windows scales the icons
/// until the next start, like any tray icon.
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly Uri DarkTaskbarUri = new("pack://application:,,,/Resources/TrayIcon.DarkTaskbar.ico");
    private static readonly Uri LightTaskbarUri = new("pack://application:,,,/Resources/TrayIcon.LightTaskbar.ico");

    /// <summary>How visible the greyed-out artwork stays behind the badge.</summary>
    private const double PausedOpacity = 0.6;

    // Neutral grey with white bars, readable on light and dark taskbars. Not amber: yellow says "warning".
    private static readonly SolidColorBrush BadgeFill = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x5A, 0x5A)));
    private static readonly SolidColorBrush BadgeBars = Brushes.White;

    /// <param name="lightTaskbar">True for the artwork made for a light taskbar (dark artwork).</param>
    public static (DrawingIcon Normal, DrawingIcon Paused) Create(bool lightTaskbar)
    {
        // The small icon size, as Windows recommends for notification area icons (LoadIconMetric with LIM_SMALL):
        // 16 px at 100 %, 24 px at 150 %
        var size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        var artwork = LoadArtwork(lightTaskbar ? LightTaskbarUri : DarkTaskbarUri, size);
        return (ToIcon(Render(artwork, size, paused: false)), ToIcon(Render(artwork, size, paused: true)));
    }

    /// <summary>
    /// The plain artwork at the small icon size, for a window's title bar (see TitleBarIcon): the app icon is a dark
    /// monitor that almost disappears on a dark title bar, this glyph is made for the background it's drawn on.
    /// </summary>
    /// <param name="lightBackground">True for a light title bar (dark artwork).</param>
    public static DrawingIcon CreatePlain(bool lightBackground)
    {
        var size = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSMICON);
        var artwork = LoadArtwork(lightBackground ? LightTaskbarUri : DarkTaskbarUri, size);
        return ToIcon(Render(artwork, size, paused: false));
    }

    /// <summary>The image of exactly this size if the file has one, otherwise the next bigger one: scaling up would blur.</summary>
    private static BitmapFrame LoadArtwork(Uri file, int size)
    {
        var frames = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
        return frames.Where(f => f.PixelWidth >= size).MinBy(f => f.PixelWidth) ?? frames.MaxBy(f => f.PixelWidth)!;
    }

    private static RenderTargetBitmap Render(BitmapSource artwork, int size, bool paused)
    {
        var visual = new DrawingVisual();
        // Smooth when the artwork has to be scaled down (e.g. 32 px to 28 px at 175 %: the files have no 28 px image)
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var context = visual.RenderOpen())
        {
            if (paused)
            {
                DrawPaused(context, artwork, size);
            }
            else
            {
                context.DrawImage(artwork, new Rect(0, 0, size, size));
            }
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    private static void DrawPaused(DrawingContext context, BitmapSource artwork, int size)
    {
        // Measured in 16ths of the icon (its size at 100 %) and rounded to whole pixels, so the edges stay sharp.
        // At 16 px: a 9 px badge with two 2 × 5 px bars 1 px apart, and 1 px of space around the badge.
        var badge = Pixels(9);
        var barWidth = Pixels(2);
        var barHeight = Pixels(5);
        var barGap = Pixels(1);
        var space = Pixels(1);

        // The artwork is cut away around the badge, so the badge stays readable on any artwork
        var badgeStart = size - badge;   // left and top edge: the badge sits in the bottom-right corner
        var cutout = new Rect(badgeStart - space, badgeStart - space, badge + 2 * space, badge + 2 * space);
        var bounds = new Rect(0, 0, size, size);
        context.PushClip(new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(bounds), new EllipseGeometry(cutout)));
        context.PushOpacity(PausedOpacity);
        context.DrawImage(Greyed(artwork), bounds);
        context.Pop();
        context.Pop();

        var radius = badge / 2.0;
        context.DrawEllipse(BadgeFill, null, new Point(badgeStart + radius, badgeStart + radius), radius, radius);

        // Integer division on purpose: the bars start on a whole pixel
        var barLeft = badgeStart + (badge - (2 * barWidth + barGap)) / 2;
        var barTop = badgeStart + (badge - barHeight) / 2;
        context.DrawRectangle(BadgeBars, null, new Rect(barLeft, barTop, barWidth, barHeight));
        context.DrawRectangle(BadgeBars, null, new Rect(barLeft + barWidth + barGap, barTop, barWidth, barHeight));

        int Pixels(double sixteenths) => Math.Max(1, (int)Math.Round(sixteenths * size / 16, MidpointRounding.AwayFromZero));
    }

    /// <summary>Each pixel turned into its perceived brightness (Rec. 601 weights), keeping its transparency.</summary>
    private static BitmapSource Greyed(BitmapSource artwork)
    {
        var source = new FormatConvertedBitmap(artwork, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            // Bgra32: blue, green, red, alpha
            var grey = (byte)Math.Round(0.114 * pixels[i] + 0.587 * pixels[i + 1] + 0.299 * pixels[i + 2]);
            pixels[i] = pixels[i + 1] = pixels[i + 2] = grey;
        }

        return BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    /// <summary>
    /// Wraps the image as a PNG in an .ico file in memory (Windows reads PNG icons since Vista) and loads it at its
    /// own size: without a size, System.Drawing would stretch it to the large icon size.
    /// </summary>
    private static DrawingIcon ToIcon(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var png = new MemoryStream();
        encoder.Save(png);

        const int HeaderLength = 6 + 16;   // ICONDIR + one ICONDIRENTRY
        using var ico = new MemoryStream(HeaderLength + (int)png.Length);
        using (var writer = new BinaryWriter(ico, Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR: reserved, type 1 = icon, 1 image
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            // ICONDIRENTRY: width, height (0 means 256), no palette, reserved, 1 plane, 32 bits per pixel,
            // then the size of the image data and where it starts (right after this header)
            writer.Write((byte)(image.PixelWidth % 256));
            writer.Write((byte)(image.PixelHeight % 256));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)png.Length);
            writer.Write((uint)HeaderLength);
            writer.Write(png.GetBuffer(), 0, (int)png.Length);
        }

        ico.Position = 0;
        return new DrawingIcon(ico, image.PixelWidth, image.PixelHeight);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
