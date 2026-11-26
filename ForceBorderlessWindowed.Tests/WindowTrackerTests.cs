using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;
using ForceBorderlessWindowed.Tests.Fakes;

using Microsoft.Extensions.Logging.Abstractions;

namespace ForceBorderlessWindowed.Tests;

public class WindowTrackerTests
{
    private const string GameExe = "game.exe";

    private readonly FakeDesktop _desktop = new();
    private readonly FakeChangedWindowsStore _store = new();
    private readonly FakeTime _time = new();
    private readonly WindowTracker _tracker;

    private AppSettings _settings = new() { Games = [new GameProfile { ExecutableName = GameExe }] };

    public WindowTrackerTests()
    {
        _tracker = new WindowTracker(_desktop, _desktop, _desktop, _desktop, _store, _time, NullLogger<WindowTracker>.Instance);
        _tracker.RefreshDisplays();
    }

    private void Check() => _tracker.Reconcile(_settings);

    /// <summary>The status of the (first) game with this exe name in the current settings.</summary>
    private GameWindowStatus? StatusOf(string exe) =>
        _tracker.BuildStatuses().TryGetValue(_settings.Games.First(g => g.ExecutableName == exe).Id, out var status)
            ? status
            : null;

    private GameWindowStatus? StatusOf(GameProfile game) =>
        _tracker.BuildStatuses().TryGetValue(game.Id, out var status) ? status : null;

    [Fact]
    public void Window_WithFrame_IsMadeBorderlessAtAutomaticSize()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);

        Check();

        // 16:9 at full height on a 3440 × 1440 monitor, centered
        Assert.False(window.HasFrame);
        Assert.Equal((440, 0, 3000, 1440), window.Rect);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void Window_WithoutFrame_IsLeftAlone()
    {
        var window = _desktop.AddWindow(1, 100, GameExe, hasFrame: false);
        var before = window.Rect;

        Check();

        Assert.Equal(before, window.Rect);
        Assert.Equal(0, _desktop.MakeBorderlessCalls);
        Assert.Equal(GameWindowStatus.NoFrame, StatusOf(GameExe));
    }

    [Fact]
    public void Window_OfOtherProgram_IsNotTouched()
    {
        _desktop.AddWindow(1, 100, "notepad.exe");

        Check();

        Assert.Equal(0, _desktop.MakeBorderlessCalls);
        Assert.Empty(_tracker.BuildStatuses());
    }

    [Fact]
    public void SecondCheck_WithNothingChanged_DoesNothing()
    {
        _desktop.AddWindow(1, 100, GameExe);

        Check();
        Check();
        Check();

        Assert.Equal(1, _desktop.MakeBorderlessCalls);
    }

    [Fact]
    public void Pause_RestoresTheOriginalWindow()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        var original = window.Rect;
        Check();

        _settings = _settings with { IsPaused = true };
        Check();

        Assert.True(window.HasFrame);
        Assert.Equal(original, window.Rect);
        Assert.False(_tracker.HasTrackedWindows);
        Assert.Empty(_tracker.BuildStatuses());
    }

    [Fact]
    public void DisablingTheGame_RestoresItsWindow()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        _settings = _settings with { Games = [new GameProfile { ExecutableName = GameExe, Enabled = false }] };
        Check();

        Assert.True(window.HasFrame);
        Assert.False(_tracker.HasTrackedWindows);
    }

    [Fact]
    public void RestoreOfHungWindow_IsRetriedUntilItWorks()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        window.Hung = true;
        _settings = _settings with { IsPaused = true };
        Check();

        Assert.False(window.HasFrame);
        Assert.True(_tracker.HasTrackedWindows);

        window.Hung = false;
        Check();

        Assert.True(window.HasFrame);
        Assert.False(_tracker.HasTrackedWindows);
    }

    [Fact]
    public void HungWindow_IsWaitedFor_ThenChanged()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Hung = true;

        Check();
        Assert.True(window.HasFrame);
        Assert.Equal(GameWindowStatus.NotResponding, StatusOf(GameExe));

        window.Hung = false;
        Check();
        Assert.False(window.HasFrame);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void BorderlessWindow_ThatStopsResponding_StaysBorderless()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        window.Hung = true;
        Check();

        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void GameUndoingTheChange_HitsTheFightLimit_AndIsTriedAgainLater()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);

        // The game puts its frame back after every change: one apply per check
        for (var i = 0; i <= WindowTracker.MaxApplies; i++)
        {
            Check();
            window.HasFrame = true;
            _time.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(WindowTracker.MaxApplies + 1, _desktop.MakeBorderlessCalls);
        Assert.Equal(GameWindowStatus.FightPaused, StatusOf(GameExe));

        Check();
        Assert.Equal(WindowTracker.MaxApplies + 1, _desktop.MakeBorderlessCalls);   // paused: not touched

        _time.Advance(WindowTracker.FightPause);
        Check();
        Assert.Equal(WindowTracker.MaxApplies + 2, _desktop.MakeBorderlessCalls);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void SlowResets_DoNotHitTheFightLimit()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);

        for (var i = 0; i < 10; i++)
        {
            Check();
            window.HasFrame = true;
            _time.Advance(WindowTracker.FightWindow);
        }

        Assert.Equal(10, _desktop.MakeBorderlessCalls);
    }

    [Fact]
    public void ResetFightLimits_EndsAFightPause()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        for (var i = 0; i <= WindowTracker.MaxApplies; i++)
        {
            Check();
            window.HasFrame = true;
        }

        Assert.Equal(GameWindowStatus.FightPaused, StatusOf(GameExe));

        _tracker.ResetFightLimits();
        Check();

        Assert.False(window.HasFrame);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void LargerWindowOfTheSameGame_TakesOver_AndTheOldOneIsRestored()
    {
        var launcher = _desktop.AddWindow(1, 100, GameExe);
        launcher.Rect = (0, 0, 800, 600);
        var launcherOriginal = launcher.Rect;
        Check();
        Assert.False(launcher.HasFrame);

        // The real window opens, larger than the launcher even after our resize (the launcher competes with its
        // original size only)
        var real = _desktop.AddWindow(2, 100, GameExe);
        real.Rect = (0, 0, 3440, 1440);
        Check();

        Assert.True(launcher.HasFrame);
        Assert.Equal(launcherOriginal, launcher.Rect);
        Assert.False(real.HasFrame);
    }

    [Fact]
    public void GameSwitchingToItsOwnMode_IsForgotten_NotRestored()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        window.SwitchedOwnMode = true;
        Check();

        Assert.Equal(GameWindowStatus.NoFrame, StatusOf(GameExe));

        _settings = _settings with { IsPaused = true };
        Check();

        Assert.Equal(0, _desktop.RestoreCalls);
        Assert.False(window.HasFrame);
    }

    [Fact]
    public void ReusedHandle_NeverGetsTheOldSnapshot()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        // The game closed and Windows gave the handle to another program's window
        window.ProcessId = 999;
        _settings = _settings with { IsPaused = true };
        Check();

        Assert.Equal(0, _desktop.RestoreCalls);
        Assert.False(_tracker.HasTrackedWindows);
    }

    [Fact]
    public void AccessDenied_IsShown_AndNothingIsRestoredLater()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.AccessDenied = true;

        Check();
        Assert.Equal(GameWindowStatus.AccessDenied, StatusOf(GameExe));

        _settings = _settings with { IsPaused = true };
        Check();
        Assert.Equal(0, _desktop.RestoreCalls);
    }

    [Fact]
    public void MinimizedWindow_HasNoStatusYet_AndIsChangedOnceRestored()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Minimized = true;

        Check();
        Assert.Null(StatusOf(GameExe));

        window.Minimized = false;
        Check();
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
    }

    [Fact]
    public void ChangedWindows_AreKeptInTheStore_UntilClosed()
    {
        _desktop.AddWindow(1, 100, GameExe);
        Check();
        _tracker.PersistChangedWindows();

        var record = Assert.Single(_store.Records);
        Assert.Equal(1, record.Handle);
        Assert.Equal(GameExe, record.ExecutableName);

        Assert.True(_tracker.ForgetDestroyed(1));
        _tracker.PersistChangedWindows();
        Assert.Empty(_store.Records);
    }

    [Fact]
    public void OriginalState_IsOnDisk_BeforeTheFirstChange()
    {
        _desktop.AddWindow(1, 100, GameExe);
        var recordsAtChange = -1;
        _desktop.BeforeChange = _ => recordsAtChange = _store.Records.Count;

        Check();

        Assert.Equal(1, recordsAtChange);
    }

    [Fact]
    public void OriginalState_IsOnDisk_BeforeTheFirstChange_EvenWithoutAProcessStartTime()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.ProcessStartTime = null;   // e.g. an anti-cheat blocks opening the game's process
        var recordsAtChange = -1;
        _desktop.BeforeChange = _ => recordsAtChange = _store.Records.Count;

        Check();

        Assert.False(window.HasFrame);
        Assert.Equal(1, recordsAtChange);
        Assert.Null(Assert.Single(_store.Records).ProcessStartTime);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OriginalState_IsOnDisk_BeforeTheFirstChange_WhenTheWindowWakesUpDuringTheCheck(bool hung)
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Hung = hung;
        window.Minimized = !hung;
        window.WakesUpAfterQuery = true;   // responsive again / opened right after the tracker looked
        var recordsAtChange = -1;
        _desktop.BeforeChange = _ => recordsAtChange = _store.Records.Count;

        Check();
        Check();

        Assert.False(window.HasFrame);   // changed on the next check
        Assert.Equal(1, recordsAtChange);
    }

    [Fact]
    public void SkippedWindow_IsNotWrittenOnEveryCheck()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Hung = true;

        Check();
        _tracker.PersistChangedWindows();
        var afterFirstCheck = _store.SaveCalls;   // the very first write always happens: the file's state isn't known yet
        Check();
        _tracker.PersistChangedWindows();
        Check();
        _tracker.PersistChangedWindows();

        Assert.Equal(afterFirstCheck, _store.SaveCalls);
        Assert.Empty(_store.Records);
    }

    [Fact]
    public void WindowFromTheLastRun_IsTakenOver_AndRestoredOnPause()
    {
        var window = _desktop.AddWindow(1, 100, GameExe, hasFrame: false);
        window.Rect = (440, 0, 3000, 1440);
        _store.Records = [new ChangedWindowRecord(1, 100, 1000, GameExe, 0x00CF0000, 0, 10, 20, 810, 620, false)];

        _tracker.RecoverFromLastRun();
        Check();
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));   // not "no frame": it's ours

        _settings = _settings with { IsPaused = true };
        Check();
        Assert.True(window.HasFrame);
        Assert.Equal((10, 20, 810, 620), window.Rect);
    }

    [Fact]
    public void WindowFromTheLastRun_OfAnotherRunOfTheGame_IsIgnored()
    {
        _desktop.AddWindow(1, 100, GameExe, hasFrame: false);
        _store.Records = [new ChangedWindowRecord(1, 100, ProcessStartTime: 555, GameExe, 0, 0, 0, 0, 800, 600, false)];

        _tracker.RecoverFromLastRun();

        Assert.False(_tracker.HasTrackedWindows);
        Assert.Empty(_store.Records);   // written again without it
    }

    [Theory]
    [InlineData(null, null)]   // not known then and not now (e.g. anti-cheat)
    [InlineData(null, 1000L)]  // not known then, known now
    [InlineData(1000L, null)]  // known then, not now
    public void WindowFromTheLastRun_WithAnUnknownStartTime_IsRecognizedByHandleProcessAndExe(long? recorded, long? current)
    {
        var window = _desktop.AddWindow(1, 100, GameExe, hasFrame: false);
        window.ProcessStartTime = current;
        _store.Records = [new ChangedWindowRecord(1, 100, recorded, GameExe, 0x00CF0000, 0, 10, 20, 810, 620, false)];

        _tracker.RecoverFromLastRun();

        Assert.True(_tracker.HasTrackedWindows);
        Assert.Single(_store.Records);   // kept
    }

    [Fact]
    public void WindowFromTheLastRun_WithAnUnknownStartTime_InAnotherProcess_IsIgnored()
    {
        var window = _desktop.AddWindow(1, 200, GameExe, hasFrame: false);   // handle reused by another process
        window.ProcessStartTime = null;
        _store.Records = [new ChangedWindowRecord(1, 100, ProcessStartTime: null, GameExe, 0, 0, 0, 0, 800, 600, false)];

        _tracker.RecoverFromLastRun();

        Assert.False(_tracker.HasTrackedWindows);
    }

    [Fact]
    public void UnreadableStore_IsNeverOverwritten_UntilItCanBeRead()
    {
        _store.Unreadable = true;
        _store.Records = [new ChangedWindowRecord(7, 700, 1000, GameExe, 0, 0, 0, 0, 800, 600, false)];
        var window = _desktop.AddWindow(1, 100, GameExe);

        _tracker.RecoverFromLastRun();
        Check();
        _tracker.PersistChangedWindows();
        Assert.Equal(0, _store.SaveCalls);
        Assert.True(window.HasFrame);   // no record possible yet: not changed either

        _store.Unreadable = false;
        Check();
        _tracker.PersistChangedWindows();
        Assert.True(_store.SaveCalls > 0);
        Assert.False(window.HasFrame);
    }

    [Fact]
    public void UnwritableStore_NoChangeWithoutARecord_AndAFailedStatusMeanwhile()
    {
        _store.Unwritable = true;
        var window = _desktop.AddWindow(1, 100, GameExe);

        Check();

        Assert.True(window.HasFrame);
        Assert.Equal(GameWindowStatus.Failed, StatusOf(GameExe));

        _store.Unwritable = false;
        Check();

        Assert.False(window.HasFrame);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(GameExe));
        Assert.Contains(_store.Records, r => r.Handle == 1);
    }

    [Fact]
    public void EnlargedLauncher_LosesToALargerRealWindow()
    {
        var launcher = _desktop.AddWindow(1, 100, GameExe);
        launcher.Rect = (0, 0, 800, 600);
        Check();
        Assert.False(launcher.HasFrame);   // enlarged to 2560 × 1440 now

        // Smaller than the enlarged launcher, larger than its original size
        var real = _desktop.AddWindow(2, 100, GameExe);
        real.Rect = (0, 0, 1920, 1080);
        Check();

        Assert.True(launcher.HasFrame);
        Assert.False(real.HasFrame);

        Check();   // and stays that way, no back and forth
        Assert.True(launcher.HasFrame);
        Assert.False(real.HasFrame);
    }

    [Fact]
    public void ForegroundGame_OnlyWhileBorderless_AndNotPausedOrMinimized()
    {
        _settings = _settings with { Games = [new GameProfile { ExecutableName = GameExe, LockCursor = true }] };
        var window = _desktop.AddWindow(1, 100, GameExe);
        Check();

        Assert.True(_tracker.GetForegroundGame(1, _settings)?.LockCursor);
        Assert.Null(_tracker.GetForegroundGame(2, _settings));

        window.Minimized = true;
        Assert.Null(_tracker.GetForegroundGame(1, _settings));

        window.Minimized = false;
        Assert.Null(_tracker.GetForegroundGame(1, _settings with { IsPaused = true }));
    }

    [Fact]
    public void GamesSharingAnExe_AreToldApartByWindowTitle()
    {
        var custom = new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 0) };
        var minecraft = new GameProfile { ExecutableName = "javaw.exe", WindowTitleContains = "Minecraft", Placement = custom };
        var other = new GameProfile { ExecutableName = "javaw.exe" };
        _settings = new AppSettings { Games = [other, minecraft] };   // the more specific one wins, whatever the order

        var mc = _desktop.AddWindow(1, 100, "javaw.exe");
        mc.Title = "Minecraft 1.21.4";
        var tool = _desktop.AddWindow(2, 200, "javaw.exe");
        tool.Title = "Some Java tool";

        Check();

        Assert.Equal(1280, mc.Rect.Right - mc.Rect.Left);
        Assert.Equal(2560, tool.Rect.Right - tool.Rect.Left);
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(minecraft));
        Assert.Equal(GameWindowStatus.Borderless, StatusOf(other));
    }

    [Fact]
    public void TitleFilter_LeavesOtherWindowsOfTheProgramAlone()
    {
        _settings = new AppSettings { Games = [new GameProfile { ExecutableName = "javaw.exe", WindowTitleContains = "minecraft" }] };
        var tool = _desktop.AddWindow(1, 100, "javaw.exe");
        tool.Title = "Some Java tool";

        Check();

        Assert.True(tool.HasFrame);
        Assert.Empty(_tracker.BuildStatuses());
    }

    [Fact]
    public void ChangedFilter_ThatNoLongerTakesTheWindow_RestoresIt()
    {
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Title = "Game";
        Check();
        Assert.False(window.HasFrame);

        _settings = new AppSettings { Games = [_settings.Games[0] with { WindowTitleContains = "Launcher" }] };
        Check();

        Assert.True(window.HasFrame);
        Assert.False(_tracker.HasTrackedWindows);
    }

    [Fact]
    public void PathFilter_OnlyTakesThatFile()
    {
        var game = new GameProfile
        {
            ExecutableName = GameExe,
            ExecutablePath = FakeDesktop.GamesFolder + GameExe,
            MatchExecutablePath = true,
        };
        _settings = new AppSettings { Games = [game] };
        var copy = _desktop.AddWindow(1, 100, GameExe);
        copy.Folder = @"D:\Backup\";
        var real = _desktop.AddWindow(2, 200, GameExe);

        Check();

        Assert.True(copy.HasFrame);
        Assert.False(real.HasFrame);
    }

    [Fact]
    public void GameSize_KeepsTheInnerSize_Centered_AndFollowsTheGame()
    {
        _settings = new AppSettings { DefaultPlacement = new PlacementSettings { Mode = PlacementMode.GameSize }, Games = _settings.Games };
        var window = _desktop.AddWindow(1, 100, GameExe);
        window.Rect = (100, 100, 100 + 1920 + 16, 100 + 1080 + 39);   // 1920 × 1080 inside its frame

        Check();
        Assert.Equal((760, 180, 2680, 1260), window.Rect);

        // A new resolution picked in the game: the window gets that size, we center it again
        window.Rect = (760, 180, 760 + 2560, 180 + 1440);
        Check();
        Assert.Equal((440, 0, 3000, 1440), window.Rect);

        Check();
        Assert.Equal(2, _desktop.MakeBorderlessCalls);   // and then leave it alone
    }

    [Fact]
    public void OneOffWindow_IsMadeBorderless_AndRestoredByTheHotkeyAgain()
    {
        var window = _desktop.AddWindow(5, 500, "notepad.exe");
        var original = window.Rect;

        Assert.Equal(OneOffResult.Added, _tracker.ToggleOneOff(5, _settings));
        Check();
        Assert.False(window.HasFrame);
        Assert.Empty(_tracker.BuildStatuses());   // no game to show it under

        Check();
        Assert.False(window.HasFrame);   // kept, although no game takes it

        Assert.Equal(OneOffResult.Restored, _tracker.ToggleOneOff(5, _settings));
        Assert.True(window.HasFrame);
        Assert.Equal(original, window.Rect);
    }

    [Fact]
    public void OneOff_DoesNotFollowAReusedHandle_IntoAnotherProgram()
    {
        var first = _desktop.AddWindow(5, 500, "notepad.exe");
        _tracker.ToggleOneOff(5, _settings);
        Check();
        Assert.False(first.HasFrame);

        // Closed without a "destroyed" event, and Windows hands the number to another program's window
        first.Exists = false;
        var other = _desktop.AddWindow(5, 600, "other.exe");
        Check();
        Check();

        Assert.True(other.HasFrame);
    }

    [Fact]
    public void OneOff_ReportsAccessDenied_AndGivesUp()
    {
        var window = _desktop.AddWindow(5, 500, "admin.exe");
        window.AccessDenied = true;

        var result = _tracker.ToggleOneOff(5, _settings);
        Check();

        Assert.Equal(OneOffResult.AccessDenied, _tracker.ConfirmOneOff(5, result));
        Assert.False(_tracker.HasTrackedWindows);

        // Not tried again: the next press is a fresh attempt, not a "put back"
        window.AccessDenied = false;
        Check();
        Assert.True(window.HasFrame);
        Assert.Equal(OneOffResult.Added, _tracker.ToggleOneOff(5, _settings));
    }

    [Fact]
    public void OneOff_ConfirmsWhatWorked()
    {
        _desktop.AddWindow(5, 500, "notepad.exe");

        var added = _tracker.ToggleOneOff(5, _settings);
        Check();
        Assert.Equal(OneOffResult.Added, _tracker.ConfirmOneOff(5, added));

        var restored = _tracker.ToggleOneOff(5, _settings);
        Check();
        Assert.Equal(OneOffResult.Restored, _tracker.ConfirmOneOff(5, restored));
    }

    [Fact]
    public void OneOffWindow_IsForgottenByPause()
    {
        var window = _desktop.AddWindow(5, 500, "notepad.exe");
        _tracker.ToggleOneOff(5, _settings);
        Check();

        _settings = _settings with { IsPaused = true };
        Check();
        Assert.True(window.HasFrame);

        _settings = _settings with { IsPaused = false };
        Check();
        Assert.True(window.HasFrame);
    }

    [Fact]
    public void OneOff_IsRefused_ForGameWindows_FramelessWindows_AndWhilePaused()
    {
        _desktop.AddWindow(1, 100, GameExe);
        _desktop.AddWindow(2, 200, "video.exe", hasFrame: false);
        _desktop.AddWindow(3, 300, "notepad.exe");

        Assert.Equal(OneOffResult.AlreadyAGame, _tracker.ToggleOneOff(1, _settings));
        Assert.Equal(OneOffResult.NoFrame, _tracker.ToggleOneOff(2, _settings));
        Assert.Equal(OneOffResult.Paused, _tracker.ToggleOneOff(3, _settings with { IsPaused = true }));
    }

    [Fact]
    public void IsGameWindow_MatchesEnabledGamesOnly()
    {
        _desktop.AddWindow(1, 100, GameExe);
        _desktop.AddWindow(2, 200, "other.exe");

        Assert.True(_tracker.IsGameWindow(1, _settings));
        Assert.False(_tracker.IsGameWindow(2, _settings));
        Assert.False(_tracker.IsGameWindow(3, _settings));
    }
}
