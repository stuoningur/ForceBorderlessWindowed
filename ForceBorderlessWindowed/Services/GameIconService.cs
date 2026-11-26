using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using Microsoft.Extensions.Logging;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Program icons for the games list and the "Add" menu, read from the exe file and kept in memory only.
/// A game has an icon once its file was seen this session: running, picked with "Browse", or from the exe path saved
/// with the game (games added with an older version have none until they're browsed for again).
/// Until then the list shows the generic icon.
/// Thread-safe: the running programs are refreshed on a background thread. The icons are frozen,
/// so the UI thread can use them.
/// </summary>
internal sealed partial class GameIconService(ILogger<GameIconService> logger) : IGameIconService
{
    /// <summary>Big enough for the 32 px list icon at 200 % scaling.</summary>
    private const int IconSize = 64;

    private readonly ILogger<GameIconService> _logger = logger;
    private readonly Lock _lock = new();

    /// <summary>Per exe name, for configured games whose file was seen this session.</summary>
    private readonly Dictionary<string, ImageSource> _byExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per full path; null = looked at the file, it has no icon.</summary>
    private readonly Dictionary<string, ImageSource?> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public ImageSource? GetIcon(string executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return null;
        }

        lock (_lock)
        {
            return _byExe.GetValueOrDefault(executableName.Trim());
        }
    }

    public ImageSource? Remember(string executableName, string executablePath)
    {
        var icon = GetIconForFile(executablePath);
        if (icon is null)
        {
            return GetIcon(executableName);
        }

        lock (_lock)
        {
            _byExe[executableName.Trim()] = icon;
        }

        return icon;
    }

    public ImageSource? GetIconForFile(string path)
    {
        lock (_lock)
        {
            if (_byPath.TryGetValue(path, out var cached))
            {
                return cached;
            }
        }

        // Outside the lock: reading the file can be slow (sleeping disk, network drive)
        var icon = Extract(path);

        lock (_lock)
        {
            _byPath[path] = icon;
        }

        return icon;
    }

    private unsafe BitmapSource? Extract(string path)
    {
        HICON large = default;
        try
        {
            // Low word: size of the "large" icon we want; Windows picks the best frame and scales if needed.
            // S_FALSE means "file has no icon".
            HRESULT result;
            fixed (char* pathPointer = path)
            {
                result = PInvoke.SHDefExtractIcon(pathPointer, 0, 0, &large, null, IconSize);
            }

            if (result.Value != 0 || large.IsNull)
            {
                return null;
            }

            var bitmap = Imaging.CreateBitmapSourceFromHIcon(large, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            LogExtractFailed(ex, path);
            return null;
        }
        finally
        {
            if (!large.IsNull)
            {
                PInvoke.DestroyIcon(large);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not extract the icon of {Path}")]
    private partial void LogExtractFailed(Exception ex, string path);
}
