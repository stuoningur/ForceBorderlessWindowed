using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;

using Microsoft.Extensions.Logging;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>Normal (not maximized) bounds in physical pixels, workspace coordinates, as Get/SetWindowPlacement use them.</summary>
public sealed record SavedWindowPlacement(int Left, int Top, int Right, int Bottom, bool IsMaximized);

/// <summary>
/// Remembers size, position and maximized state of our windows between sessions.
/// Stored next to the logs (local app data), not in settings.json: it's UI state, not a setting,
/// and saving it must not raise SettingsChanged.
/// Uses Get/SetWindowPlacement: they know the normal size of a maximized window, and Windows moves a window
/// back onto the screen if its saved monitor is gone.
/// </summary>
internal sealed partial class WindowPlacementStore : IWindowPlacementStore
{
    private readonly ILogger<WindowPlacementStore> _logger;
    private readonly string _filePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ForceBorderlessWindowed",
        "window-placement.json");

    private readonly Dictionary<string, SavedWindowPlacement> _placements;

    public WindowPlacementStore(ILogger<WindowPlacementStore> logger)
    {
        _logger = logger;
        _placements = Load();
    }

    public void Track(Window window, string name)
    {
        if (_placements.ContainsKey(name))
        {
            // Otherwise WPF would center the window and override the restored position
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        // SourceInitialized: the window handle exists, but the window isn't visible yet
        window.SourceInitialized += (_, _) => Restore(window, name);

        // Subscribed last, so it runs after the "unsaved changes?" handler and sees its decision
        window.Closing += (_, e) =>
        {
            if (!e.Cancel)
            {
                Save(window, name);
            }
        };
    }

    private void Restore(Window window, string name)
    {
        if (!_placements.TryGetValue(name, out var saved))
        {
            return;
        }

        var hwnd = new HWND(new WindowInteropHelper(window).Handle);
        var placement = new WINDOWPLACEMENT
        {
            length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = SHOW_WINDOW_CMD.SW_HIDE,   // only set the bounds; WPF shows the window itself
            rcNormalPosition = new RECT(saved.Left, saved.Top, saved.Right, saved.Bottom),
        };

        // Twice on purpose: if the saved position is on a monitor with a different scaling, the first call
        // moves the window there and Windows rescales it for the new DPI. The second call then sets the exact
        // saved size, which was measured at that monitor's DPI. (Recommended by Raymond Chen for per-monitor DPI.)
        PInvoke.SetWindowPlacement(hwnd, in placement);
        PInvoke.SetWindowPlacement(hwnd, in placement);

        if (saved.IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    private void Save(Window window, string name)
    {
        var hwnd = new HWND(new WindowInteropHelper(window).Handle);
        var placement = new WINDOWPLACEMENT { length = (uint)Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (hwnd.IsNull || !PInvoke.GetWindowPlacement(hwnd, ref placement))
        {
            return;
        }

        var normal = placement.rcNormalPosition;
        _placements[name] = new SavedWindowPlacement(normal.left, normal.top, normal.right, normal.bottom,
            IsMaximized: placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED);

        var folder = Path.GetDirectoryName(_filePath)!;
        if (!ElevatedWriteGuard.IsSafeFolder(folder))
        {
            LogSaveSkipped(folder);
            return;
        }

        try
        {
            // Creates the folder too; as administrator through no junction, even one created after the check above
            using var stream = ElevatedWriteGuard.OpenForWrite(_filePath);
            JsonSerializer.Serialize(stream, _placements, SettingsJsonContext.Default.DictionaryStringSavedWindowPlacement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex, _filePath);
        }
    }

    private Dictionary<string, SavedWindowPlacement> Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                using var stream = File.OpenRead(_filePath);
                var loaded = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.DictionaryStringSavedWindowPlacement);
                if (loaded is not null)
                {
                    // Valid JSON can still hold a null entry or an empty rectangle (edited by hand, or broken).
                    // Dropped: that window then simply opens at its default position.
                    return loaded
                        .Where(pair => pair.Value is { } p && p.Right > p.Left && p.Bottom > p.Top)
                        .ToDictionary();
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Not worth more than a log line: the window simply opens centered
            LogLoadFailed(ex, _filePath);
        }

        return [];
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read window positions from {Path}")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save window positions to {Path}")]
    private partial void LogSaveFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Window positions not saved: {Folder} contains a junction or symbolic link, which isn't safe while running as administrator")]
    private partial void LogSaveSkipped(string folder);
}
