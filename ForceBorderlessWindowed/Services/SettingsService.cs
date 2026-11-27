using System.Globalization;
using System.IO;
using System.Text.Json;

using ForceBorderlessWindowed.Models;

using Microsoft.Extensions.Logging;

namespace ForceBorderlessWindowed.Services;



internal sealed partial class SettingsService : ISettingsService
{
    private readonly ILogger<SettingsService> _logger;
    private readonly string _filePath;
    public string? ActiveLanguage { get; }
    public SettingsService(ILogger<SettingsService> logger)
        : this(logger, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ForceBorderlessWindowed"))
    {
    }

    /// <summary>With another folder than %APPDATA%: for tests. Not public, so the DI container never picks it.</summary>
    internal SettingsService(ILogger<SettingsService> logger, string settingsFolder)
    {
        _logger = logger;
        SettingsFolder = settingsFolder;
        _filePath = Path.Combine(SettingsFolder, "settings.json");
        Current = Load();
        ActiveLanguage = Current.Language;
    }

    public AppSettings Current { get; private set; }

    public string SettingsFolder { get; }

    public void Save(AppSettings settings)
    {
        // As administrator, a junction in the path could redirect the write into a protected folder.
        // Thrown, not skipped: the user should know the settings weren't saved (the error dialog shows this).
        if (!ElevatedWriteGuard.IsSafeFolder(SettingsFolder))
        {
            throw new IOException(
                $"Settings not saved: {SettingsFolder} contains a junction or symbolic link, which isn't safe while running as administrator.");
        }

        // The file couldn't be read at startup and no copy of it could be made: it may still hold every game (only
        // locked for a moment, by a virus scanner or backup tool). Never replaced before a copy exists.
        if (_backupMissing && File.Exists(_filePath))
        {
            if (BackUpUnreadableFile() is not { } fileName)
            {
                throw new IOException(
                    $"Settings not saved: {_filePath} couldn't be read at startup and still can't be copied, so it is left as it is. " +
                    "Close the program that uses it, or move it out of the settings folder, then save again.");
            }

            LoadProblem = new SettingsLoadProblem(fileName);
        }

        _backupMissing = false;

        if (Environment.IsPrivilegedProcess)
        {
            // As administrator: written in place, the folder and file created without following any junction
            // (see ElevatedWriteGuard). The temp file + move below can't be done that way (the move looks up the
            // target path again). Trade-off: a crash in the middle of this write leaves a broken file, and the next
            // start uses the defaults.
            using var stream = ElevatedWriteGuard.OpenForWrite(_filePath);
            JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            stream.Flush(flushToDisk: true);
        }
        else
        {
            // Temp file + move: settings.json is always either the old or the new version, never half written.
            // CreateNew: a fresh file, never one someone else has prepared under that name
            Directory.CreateDirectory(SettingsFolder);
            var tempPath = _filePath + ".tmp";
            File.Delete(tempPath);
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);

                // On disk before the move: otherwise a power cut right after it can leave an empty settings.json
                // under the new name (the rename is journaled, the content may not be written yet)
                stream.Flush(flushToDisk: true);
            }

            File.Move(tempPath, _filePath, overwrite: true);
        }

        Current = settings;
        LogSaved();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SettingsChanged;

    private AppSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new AppSettings();
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stream = File.OpenRead(_filePath);
                return Repair(JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings) ?? new AppSettings());
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < ReadAttempts)
            {
                // Locked for a moment (virus scanner, backup tool): worth waiting for, the defaults would hide every game
                LogReadRetry(ex, attempt);
                Thread.Sleep(ReadRetryDelay);
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // The next save would overwrite the file with the defaults, and every game in it would be lost.
                // So keep a copy first, and let the settings window say what happened. No copy: Save tries again.
                LogLoadFailed(ex, _filePath);
                var backup = BackUpUnreadableFile();
                _backupMissing = backup is null;
                LoadProblem = new SettingsLoadProblem(backup);
                return new AppSettings();
            }
        }
    }

    private const int ReadAttempts = 5;
    private static readonly TimeSpan ReadRetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>settings.json couldn't be read at startup and not copied either: Save must not replace it yet.</summary>
    private bool _backupMissing;

    public SettingsLoadProblem? LoadProblem { get; private set; }

    /// <summary>Copies settings.json under a new name next to it. Returns that name, or null if it failed.</summary>
    private string? BackUpUnreadableFile()
    {
        // Same rule as Save: as administrator, never write through a junction
        if (!ElevatedWriteGuard.IsSafeFolder(SettingsFolder))
        {
            return null;
        }

        try
        {
            // As administrator, read only from a handle that is checked to really be settings.json: a junction,
            // link or hard link there would otherwise copy a protected file into a backup the user can read
            byte[] content;
            using (var source = new FileStream(_filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (Environment.IsPrivilegedProcess)
                {
                    ElevatedWriteGuard.VerifyHandle(source.SafeFileHandle, Path.GetFullPath(_filePath));
                }

                content = new byte[source.Length];
                source.ReadExactly(content);
            }

            // A new name each time: a copy from an earlier failure is never overwritten
            var fileName = $"settings.broken-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json";
            using var stream = ElevatedWriteGuard.OpenForWrite(Path.Combine(SettingsFolder, fileName));
            stream.Write(content);
            LogBackedUp(fileName);
            return fileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogBackupFailed(ex);
            return null;
        }
    }

    /// <summary>
    /// Valid JSON can still hold values the settings window would never save (edited by hand, or broken):
    /// a game entry that is null or has no exe name, a custom size of 0, an unknown display choice...
    /// Those would otherwise reach the watcher and Win32. Each one is replaced by what the window would use
    /// and logged; the file itself is fixed with the next save.
    /// </summary>
    private AppSettings Repair(AppSettings settings)
    {
        var games = new List<GameProfile>();
        var seen = new HashSet<(string, string?, string?)>();
        var ids = new HashSet<Guid>();

        // The deserializer doesn't enforce nullability: a null entry or a missing exe name is possible
        foreach (var game in settings.Games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.ExecutableName))
            {
                LogRepaired("a game entry without exe name", "removed");
                continue;
            }

            var repaired = game.Placement is null ? game : game with { Placement = Repair(game.Placement, game.ExecutableName) };

            // Settings of older versions have no IDs (empty), and a hand-copied entry may repeat one. Not logged for
            // the missing ones: that's the normal upgrade, and it's written with the next save.
            if (repaired.Id == Guid.Empty)
            {
                repaired = repaired with { Id = Guid.NewGuid() };
            }
            else if (ids.Contains(repaired.Id))
            {
                LogRepaired($"{game.ExecutableName}: ID used twice", "new ID");
                repaired = repaired with { Id = Guid.NewGuid() };
            }

            // Shown as the icon, and used for "only this exe file": a path of another program is dropped
            if (repaired.ExecutablePath is not null
                && GameProfile.PathIfItMatches(repaired.ExecutablePath, repaired.ExecutableName) is null)
            {
                LogRepaired($"{game.ExecutableName}: exe path doesn't match the exe name", "path removed");
                repaired = repaired with { ExecutablePath = null };
            }

            // "Only this exe file" needs a file, and an empty title filter is no filter: stored the way the window saves them
            if (repaired.MatchExecutablePath && repaired.ExecutablePath is null)
            {
                repaired = repaired with { MatchExecutablePath = false };
            }

            if (GameMatcher.NormalizeTitle(repaired.WindowTitleContains) != repaired.WindowTitleContains)
            {
                repaired = repaired with { WindowTitleContains = GameMatcher.NormalizeTitle(repaired.WindowTitleContains) };
            }

            // Takes exactly the same windows as an earlier entry: the watcher would only ever use the first one
            if (!seen.Add(GameMatcher.Key(repaired)))
            {
                LogRepaired($"a second entry for {game.ExecutableName} with the same filters", "removed");
                continue;
            }

            ids.Add(repaired.Id);
            games.Add(repaired);
        }

        var theme = settings.Theme;
        if (!Enum.IsDefined(theme))
        {
            LogRepaired($"unknown theme {theme}", "system theme used");
            theme = AppTheme.System;
        }

        // The settings window offers "en" and "de" only. A regional name like "de-DE" would start the app in
        // German, but match no entry there: the window would show "Windows default" and write that back with
        // the next save of anything else. Translations are per language anyway, not per region.
        var language = settings.Language;
        if (language?.IndexOf('-') is > 0 and var dash)
        {
            LogRepaired($"regional language {language}", $"{language[..dash]} used");
            language = language[..dash];
        }

        return settings with
        {
            Language = language,
            Theme = theme,
            DefaultPlacement = Repair(settings.DefaultPlacement, "default placement"),
            Games = games,
            Hotkeys = Repair(settings.Hotkeys),
        };
    }

    /// <summary>
    /// Same rules as the Hotkeys tab: a combination that isn't allowed, or one used twice, would block typing or
    /// make one of the two never fire. Window shortcuts go back to their default, global hotkeys are turned off.
    /// </summary>
    private HotkeySettings Repair(HotkeySettings hotkeys)
    {
        var result = hotkeys;
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (result.Get(action) is { } hotkey && HotkeyRules.Check(action, hotkey) != HotkeyProblem.None)
            {
                LogRepaired($"hotkey {action}: {hotkey.Modifiers}+{hotkey.Key} not allowed", "default used");
                result = result.With(action, null);
            }
        }

        if (result.Save == result.RemoveGame)
        {
            LogRepaired("hotkeys Save and RemoveGame are the same", "defaults used");
            result = result with { Save = HotkeySettings.DefaultSave, RemoveGame = HotkeySettings.DefaultRemoveGame };
        }

        if (result.TogglePause is { } pause && (pause == result.Save || pause == result.RemoveGame))
        {
            LogRepaired("hotkey TogglePause is used twice", "turned off");
            result = result with { TogglePause = null };
        }

        var add = result.AddForegroundGame;
        if (add is not null && (add == result.Save || add == result.RemoveGame || add == result.TogglePause))
        {
            LogRepaired("hotkey AddForegroundGame is used twice", "turned off");
            result = result with { AddForegroundGame = null };
        }

        var apply = result.ApplyToForeground;
        if (apply is not null && (apply == result.Save || apply == result.RemoveGame || apply == result.TogglePause
            || apply == result.AddForegroundGame))
        {
            LogRepaired("hotkey ApplyToForeground is used twice", "turned off");
            result = result with { ApplyToForeground = null };
        }

        return result;
    }

    private PlacementSettings Repair(PlacementSettings placement, string owner)
    {
        var result = placement;

        // Same rule as the placement editor; Custom values are only used in Custom mode
        var invalidCustom = result.Mode == PlacementMode.Custom
            && (result.Custom is not { } custom || !WindowPlacement.IsValidSize(custom.Width, custom.Height, custom.OffsetX));

        if (!Enum.IsDefined(result.Mode) || invalidCustom)
        {
            LogRepaired(invalidCustom ? $"{owner}: invalid size" : $"{owner}: unknown placement mode", "automatic size used");
            result = result with { Mode = PlacementMode.Automatic, Custom = null };
        }

        if (!Enum.IsDefined(result.AspectRatio))
        {
            LogRepaired($"{owner}: unknown aspect ratio {result.AspectRatio}", "16:9 used");
            result = result with { AspectRatio = AspectRatio.Widescreen16x9 };
        }

        if (result.Horizontal is { } horizontal && !Enum.IsDefined(horizontal))
        {
            LogRepaired($"{owner}: unknown horizontal position {horizontal}", "the mode's usual position used");
            result = result with { Horizontal = null };
        }

        // Files of older versions have no position: stored as what applies (Custom: its offset, the rest: centered),
        // as the editor saves it. Otherwise the settings window would see a change as soon as it opens. Not a repair.
        if (result.Horizontal != result.EffectiveHorizontal())
        {
            result = result with { Horizontal = result.EffectiveHorizontal() };
        }

        if (!Enum.IsDefined(result.Vertical))
        {
            LogRepaired($"{owner}: unknown vertical position {result.Vertical}", "centered");
            result = result with { Vertical = VerticalAnchor.Center };
        }

        if (!Enum.IsDefined(result.Display)
            || (result.Display == DisplayTarget.Specific && string.IsNullOrWhiteSpace(result.DisplayId)))
        {
            LogRepaired($"{owner}: invalid display", "primary display used");
            result = result with { Display = DisplayTarget.Primary, DisplayId = null, DisplayName = null };
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Settings: {Problem}, {Fix}")]
    private partial void LogRepaired(string problem, string fix);

    // Without the path: it contains the Windows user name and would repeat on every save
    [LoggerMessage(Level = LogLevel.Information, Message = "Settings saved")]
    private partial void LogSaved();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read settings from {Path}, using defaults")]
    private partial void LogLoadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The unreadable settings file was kept as {FileName} in the settings folder")]
    private partial void LogBackedUp(string fileName);

    [LoggerMessage(Level = LogLevel.Information, Message = "Could not read the settings file (attempt {Attempt}), trying again")]
    private partial void LogReadRetry(Exception ex, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not keep a copy of the unreadable settings file, saving is refused until a copy works")]
    private partial void LogBackupFailed(Exception ex);
}