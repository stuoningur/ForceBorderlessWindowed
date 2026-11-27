using System.IO;
using System.Windows.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ForceBorderlessWindowed.Tests;

/// <summary>Loading, repairing and saving settings.json, in a temporary folder.</summary>
public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "FBW-Tests-" + Guid.NewGuid().ToString("N"));

    public SettingsServiceTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private SettingsService Load(string? json = null)
    {
        if (json is not null)
        {
            File.WriteAllText(Path.Combine(_folder, "settings.json"), json);
        }

        return new SettingsService(NullLogger<SettingsService>.Instance, _folder);
    }

    [Fact]
    public void NoFile_GivesTheDefaults()
    {
        var settings = Load().Current;

        Assert.Empty(settings.Games);
        Assert.False(settings.IsPaused);
        Assert.Equal(PlacementMode.Automatic, settings.DefaultPlacement.Mode);
        Assert.Equal(HotkeySettings.DefaultSave, settings.Hotkeys.Save);
    }

    [Fact]
    public void MissingKeys_GiveTheDefaults_NotNullOrFalse()
    {
        var settings = Load("""{ "games": [ { "executableName": "game.exe" } ] }""").Current;

        var game = Assert.Single(settings.Games);
        Assert.True(game.Enabled);
        Assert.NotNull(settings.DefaultPlacement);
        Assert.Equal(HotkeySettings.DefaultRemoveGame, settings.Hotkeys.RemoveGame);
        Assert.True(settings.Hotkeys.ShowMessages);
        Assert.True(settings.Hotkeys.PlaySounds);
        Assert.False(settings.DefaultPlacement.KeepAspectRatio);
    }

    [Fact]
    public void BrokenEntries_AreRepaired()
    {
        var settings = Load("""
            {
              "theme": "Neon",
              "language": "de-AT",
              "games": [
                null,
                { "executableName": "" },
                { "executableName": "game.exe", "executablePath": "C:\\Other\\other.exe",
                  "placement": { "mode": "Custom", "custom": { "width": 0, "height": 100, "offsetX": 0 } } },
                { "executableName": "GAME.exe" }
              ],
              "defaultPlacement": { "display": "Specific" }
            }
            """).Current;

        var game = Assert.Single(settings.Games);
        Assert.Equal("game.exe", game.ExecutableName);
        Assert.Null(game.ExecutablePath);
        Assert.Equal(PlacementMode.Automatic, game.Placement!.Mode);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal("de", settings.Language);
        Assert.Equal(DisplayTarget.Primary, settings.DefaultPlacement.Display);
    }

    [Fact]
    public void Positions_AreReadAndWrittenAsNames_UnknownOnesAreRepaired()
    {
        var service = Load("""
            {
              "games": [
                { "executableName": "old.exe", "placement": { "mode": "Custom", "custom": { "width": 1280, "height": 720, "offsetX": 100 } } },
                { "executableName": "new.exe", "placement": { "horizontal": "Right", "vertical": "Bottom" } },
                { "executableName": "odd.exe", "placement": { "horizontal": "Diagonal", "vertical": "Sideways" } }
              ]
            }
            """);
        var games = service.Current.Games;

        // Older file without the keys: Custom keeps its offset, as before; stored explicitly, as the editor saves it
        Assert.Equal(HorizontalAnchor.Offset, games[0].Placement!.Horizontal);
        Assert.Equal((HorizontalAnchor.Right, VerticalAnchor.Bottom), (games[1].Placement!.Horizontal, games[1].Placement!.Vertical));
        Assert.Equal((HorizontalAnchor.Center, VerticalAnchor.Center), (games[2].Placement!.Horizontal, games[2].Placement!.Vertical));
        Assert.Equal(HorizontalAnchor.Center, service.Current.DefaultPlacement.Horizontal);

        service.Save(service.Current);
        var json = File.ReadAllText(Path.Combine(_folder, "settings.json"));
        Assert.Contains("\"horizontal\": \"Right\"", json);
        Assert.Contains("\"vertical\": \"Bottom\"", json);
        Assert.Equal(service.Current.Games[1].Placement, Load().Current.Games[1].Placement);
    }

    [Fact]
    public void GamesFromOlderVersions_GetIds_AndRepeatedIdsAreReplaced()
    {
        var games = Load("""
            {
              "games": [
                { "executableName": "a.exe" },
                { "executableName": "b.exe", "id": "11111111-1111-1111-1111-111111111111" },
                { "executableName": "c.exe", "id": "11111111-1111-1111-1111-111111111111" }
              ]
            }
            """).Current.Games;

        Assert.Equal(3, games.Count);
        Assert.DoesNotContain(games, g => g.Id == Guid.Empty);
        Assert.Equal(3, games.Select(g => g.Id).Distinct().Count());
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), games[1].Id);
    }

    [Fact]
    public void SameExe_WithDifferentFilters_IsKept_SameFilters_IsRemoved()
    {
        var games = Load("""
            {
              "games": [
                { "executableName": "javaw.exe", "windowTitleContains": "Minecraft" },
                { "executableName": "javaw.exe" },
                { "executableName": "JAVAW.exe", "windowTitleContains": " minecraft " },
                { "executableName": "javaw.exe", "windowTitleContains": "   " }
              ]
            }
            """).Current.Games;

        Assert.Equal(2, games.Count);
        Assert.Equal("Minecraft", games[0].WindowTitleContains);
        Assert.Null(games[1].WindowTitleContains);
    }

    [Fact]
    public void OnlyThisExeFile_WithoutAPath_IsTurnedOff()
    {
        var game = Assert.Single(Load("""
            { "games": [ { "executableName": "game.exe", "matchExecutablePath": true } ] }
            """).Current.Games);

        Assert.False(game.MatchExecutablePath);
    }

    [Fact]
    public void HotkeysUsedTwice_AreTurnedOff()
    {
        var settings = Load("""
            { "hotkeys": { "togglePause": { "modifiers": "Control", "key": "S" } } }
            """).Current;

        Assert.Null(settings.Hotkeys.TogglePause);   // same as Save
    }

    [Fact]
    public void UnreadableFile_IsBackedUp_AndTheDefaultsAreUsed()
    {
        var service = Load("{ this is not json");

        Assert.Empty(service.Current.Games);
        Assert.NotNull(service.LoadProblem?.BackupFileName);
        Assert.True(File.Exists(Path.Combine(_folder, service.LoadProblem!.BackupFileName!)));
    }

    [Fact]
    public void Save_ThenLoad_GivesTheSameSettings()
    {
        var service = Load();
        var raised = false;
        service.SettingsChanged += (_, _) => raised = true;

        var settings = new AppSettings
        {
            Language = "de",
            Theme = AppTheme.Dark,
            Games =
            [
                new GameProfile
                {
                    ExecutableName = "game.exe",
                    DisplayName = "Game",
                    Enabled = false,
                    LockCursor = true,
                    Placement = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 0),
                        Horizontal = HorizontalAnchor.Right, Vertical = VerticalAnchor.Top },
                },
            ],
            Hotkeys = new HotkeySettings { TogglePause = new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.P) },
        };
        service.Save(settings);

        Assert.True(raised);
        var loaded = Load().Current;
        Assert.Equal(settings.Language, loaded.Language);
        Assert.Equal(settings.Theme, loaded.Theme);
        Assert.Equal(settings.Hotkeys, loaded.Hotkeys);
        Assert.Equal(settings.Games[0], Assert.Single(loaded.Games));
    }

    [Fact]
    public void FileLockedForAMoment_IsReadAfterAll()
    {
        var path = Path.Combine(_folder, "settings.json");
        File.WriteAllText(path, """{ "games": [ { "executableName": "game.exe" } ] }""");

        // Released when the service says it tries again, not after a fixed delay: a timer on a busy test machine
        // could fire after the last attempt
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var retries = 0;
        var logger = new CallbackLogger<SettingsService>(() =>
        {
            retries++;
            locked.Dispose();
        });

        var service = new SettingsService(logger, _folder);

        Assert.Equal(1, retries);   // locked once, read on the next attempt
        Assert.Null(service.LoadProblem);
        Assert.Single(service.Current.Games);
    }

    /// <summary>Calls back on every log entry.</summary>
    private sealed class CallbackLogger<T>(Action onLog) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => onLog();
    }

    [Fact]
    public void FileThatStaysLocked_IsNeverReplaced_BeforeACopyExists()
    {
        var path = Path.Combine(_folder, "settings.json");
        const string original = """{ "games": [ { "executableName": "game.exe" } ] }""";
        File.WriteAllText(path, original);

        SettingsService service;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            service = Load();
            Assert.Null(service.LoadProblem?.BackupFileName);
            Assert.Empty(service.Current.Games);

            // Still locked: refused, the file stays as it is
            Assert.Throws<IOException>(() => service.Save(service.Current with { IsPaused = true }));
        }

        Assert.Equal(original, File.ReadAllText(path));

        // Unlocked: a copy is made first, then the save goes through
        service.Save(service.Current with { IsPaused = true });

        Assert.NotNull(service.LoadProblem?.BackupFileName);
        Assert.Equal(original, File.ReadAllText(Path.Combine(_folder, service.LoadProblem!.BackupFileName!)));
        Assert.True(Load().Current.IsPaused);
    }
}
