using System.IO;
using System.Text.Json;

using Microsoft.Extensions.Logging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// One game window we changed, with what is needed to put it back. A window handle and a process ID alone can
/// belong to a different window after a restart of the game; the process start time makes it this exact run.
/// </summary>
/// <param name="ProcessStartTime">
/// null if Windows wouldn't tell (an anti-cheat that blocks opening the game's process): the window is still
/// recorded, and recognized by handle, process ID and exe name alone.
/// </param>
/// <param name="Area">
/// The window's size before our first change, for choosing the game's main window (see WindowTracker).
/// 0 in files of older versions: the normal size from Left/Top/Right/Bottom is used then.
/// </param>
/// <param name="Look">
/// How the window looked after our last change, to notice that the game switched to its own mode while the app
/// wasn't running. null in files of older versions, or if the change didn't go through.
/// </param>
public sealed record ChangedWindowRecord(
    long Handle,
    uint ProcessId,
    long? ProcessStartTime,
    string ExecutableName,
    uint Style,
    uint ExStyle,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool WasMaximized,
    long Area = 0,
    ChangedWindowLook? Look = null);

/// <summary>See <see cref="WindowLook"/>: the style and the rectangle we put the window in (screen coordinates).</summary>
public sealed record ChangedWindowLook(uint Style, int Left, int Top, int Right, int Bottom);

/// <summary>Where the changed windows are kept between runs (see <see cref="ChangedWindowsStore"/>). An interface for tests.</summary>
internal interface IChangedWindowsStore
{
    /// <inheritdoc cref="ChangedWindowsStore.Load"/>
    IReadOnlyList<ChangedWindowRecord>? Load();

    /// <inheritdoc cref="ChangedWindowsStore.Save"/>
    bool Save(IReadOnlyList<ChangedWindowRecord> records);
}

/// <summary>
/// The original state of every game window the watcher has changed and not put back yet, kept on disk next to the
/// logs. Without it that state exists only in memory: after a crash, a forced exit, or a restore that failed on
/// exit (game not responding), a game that still runs would stay frameless, and the next start would take it for
/// a game in its own borderless mode and never put it back. Written whenever any of the records changes, so it is
/// also right after a crash; read once at startup.
/// </summary>
internal sealed partial class ChangedWindowsStore : IChangedWindowsStore
{
    private readonly ILogger<ChangedWindowsStore> _logger;
    private readonly string _filePath;

    public ChangedWindowsStore(ILogger<ChangedWindowsStore> logger)
        : this(logger, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ForceBorderlessWindowed",
            "changed-windows.json"))
    {
    }

    /// <summary>With another file: for tests. Not public, so the DI container never picks it.</summary>
    internal ChangedWindowsStore(ILogger<ChangedWindowsStore> logger, string filePath)
    {
        _logger = logger;
        _filePath = filePath;
    }

    private bool _readFailureLogged;
    private bool _writeFailureLogged;

    /// <summary>
    /// The records of the last run. null if the file exists but can't be read right now (locked by a virus scanner
    /// or backup tool, access denied): the caller must not write the file then, or the records in it would be lost,
    /// and tries again later. A file with broken content can never be read; it counts as empty.
    /// </summary>
    public IReadOnlyList<ChangedWindowRecord>? Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return [];
            }

            using var stream = File.OpenRead(_filePath);
            var records = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.ListChangedWindowRecord) ?? [];

            // Hand-edited or broken entries are simply dropped
            return [.. records.Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ExecutableName))];
        }
        catch (JsonException ex)
        {
            LogContentBroken(ex, _filePath);
            return [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Once per run of failures: the watcher tries again on every check
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                LogLoadFailed(ex, _filePath);
            }

            return null;
        }
    }

    /// <summary>
    /// Replaces the file's content. An empty list is written as such (not deleted: see the comment inside).
    /// Returns false if it couldn't be written, so the caller tries again next time.
    /// </summary>
    public bool Save(IReadOnlyList<ChangedWindowRecord> records)
    {
        // Built in memory first: opening the file empties it, so the time between that and the complete
        // content being on disk is kept to a single write
        var content = JsonSerializer.SerializeToUtf8Bytes(records.ToList(), SettingsJsonContext.Default.ListChangedWindowRecord);

        try
        {
            if (Environment.IsPrivilegedProcess)
            {
                // Never File.Delete or move: as administrator anything by path would follow a junction, the guard only
                // opens files. The guard creates the folder too, and refuses any junction or link on the way.
                // Trade-off: a crash during this single write leaves a broken file (counted as empty at the next start).
                using var stream = ElevatedWriteGuard.OpenForWrite(_filePath);
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            else
            {
                // Temp file + move: the file is always either the old or the new list, never half written
                // (same as settings.json). CreateNew: a fresh file, never one someone else has prepared under that name.
                Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
                var tempPath = _filePath + ".tmp";
                File.Delete(tempPath);
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }

                File.Move(tempPath, _filePath, overwrite: true);
            }

            _writeFailureLogged = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Once per run of failures: this is written often
            if (!_writeFailureLogged)
            {
                _writeFailureLogged = true;
                LogSaveFailed(ex, _filePath);
            }

            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the changed game windows from {Path}; trying again, the file is left as it is until then")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The changed game windows in {Path} are unreadable (broken content); starting without them")]
    private partial void LogContentBroken(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the changed game windows to {Path}; after a crash they can't be put back")]
    private partial void LogSaveFailed(Exception ex, string path);
}
