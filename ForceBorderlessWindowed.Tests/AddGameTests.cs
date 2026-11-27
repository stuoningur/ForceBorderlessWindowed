using System.Windows.Media;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;
using ForceBorderlessWindowed.Tests.Fakes;
using ForceBorderlessWindowed.ViewModels;

namespace ForceBorderlessWindowed.Tests;

/// <summary>The Games tab's running list, the "Add a game" dialog that shows it, and the match conflict hint.</summary>
public class AddGameTests
{
    private static readonly MonitorBounds Left = new(0, 0, 1920, 1080);
    private static readonly MonitorBounds Right = new(1920, 0, 2560, 1440);

    private sealed class NoStatuses : IGameStatusProvider
    {
        public IReadOnlyDictionary<Guid, GameWindowStatus> GetStatuses() => new Dictionary<Guid, GameWindowStatus>();

        public event EventHandler? StatusesChanged { add { } remove { } }
    }

    private sealed class NoIcons : IGameIconService
    {
        public ImageSource? GetIcon(string executableName) => null;

        public ImageSource? Remember(string executableName, string executablePath) => null;

        public ImageSource? GetIconForFile(string path) => null;
    }

    private static async Task<GamesViewModel> GamesWith(FakeDesktop desktop, params GameProfile[] games)
    {
        var defaultPlacement = new PlacementEditorViewModel(new PlacementSettings(), defaultEditor: null);
        var vm = new GamesViewModel(games, defaultPlacement, desktop, new NoStatuses(), new NoIcons(),
            windowMonitor: desktop.GetMonitorForWindow);
        vm.SetDisplays(desktop.GetDisplays(), Left);
        await vm.RefreshRunningAsync();
        return vm;
    }

    private static RunningProgram Program(int id, string exe, string title) =>
        new(id, exe, title, @"C:\Games\" + exe, WindowHandle: id);

    private static FakeDesktop DesktopWith(params RunningProgram[] programs)
    {
        var desktop = new FakeDesktop();
        desktop.RunningPrograms.AddRange(programs);
        return desktop;
    }

    [Fact]
    public async Task RunningList_SaysWhichGameTakesAWindow_ByTheWatchersRule()
    {
        var desktop = DesktopWith(Program(1, "javaw.exe", "Minecraft Modpack"), Program(2, "other.exe", "Other"));
        var games = await GamesWith(desktop,
            new GameProfile { ExecutableName = "javaw.exe", DisplayName = "Any Java" },
            new GameProfile { ExecutableName = "javaw.exe", DisplayName = "Modpack", WindowTitleContains = "Modpack" });

        var java = games.RunningPrograms.Single(i => i.Program.ExecutableName == "javaw.exe");
        Assert.Equal("Modpack", java.UsedBy);   // the more specific one, not the first
        Assert.True(java.IsAlreadyAdded);
        Assert.False(games.RunningPrograms.Single(i => i.Program.ExecutableName == "other.exe").IsAlreadyAdded);
    }

    [Fact]
    public async Task RunningList_NamesTheDisplay_OnlyWithSeveralDisplays()
    {
        var desktop = DesktopWith(Program(1, "game.exe", "Game"));
        Assert.Null((await GamesWith(desktop)).RunningPrograms[0].DisplayLabel);

        desktop.Displays.Clear();
        desktop.Displays.Add(new DisplayInfo("a", "Left one", 1, Left, IsPrimary: true));
        desktop.Displays.Add(new DisplayInfo("b", "Right one", 2, Right, IsPrimary: false));
        desktop.WindowMonitors[1] = Right;

        var item = (await GamesWith(desktop)).RunningPrograms[0];
        Assert.Contains("Right one", item.DisplayLabel);
        Assert.Contains("Right one", item.Details);
        Assert.Contains("game.exe", item.AccessibleName);
    }

    [Fact]
    public async Task Dialog_FiltersByTitleOrExe_AndSelectsTheFirstAddableMatch()
    {
        var desktop = DesktopWith(
            Program(1, "eldenring.exe", "ELDEN RING"), Program(2, "game.exe", "Elden Ring Launcher"), Program(3, "notes.exe", "Notes"));
        var games = await GamesWith(desktop, new GameProfile { ExecutableName = "eldenring.exe", DisplayName = "Elden Ring" });
        using var dialog = new AddGameDialogViewModel(games);

        dialog.SearchText = "elden";
        Assert.Equal(2, dialog.Items.Count);
        Assert.Equal(2, dialog.SelectedItem!.Program.ProcessId);   // the first one is already in the list
        Assert.True(dialog.CanAdd);

        dialog.SearchText = "NOTES.EXE";
        Assert.Equal(3, Assert.Single(dialog.Items).Program.ProcessId);

        dialog.SearchText = "nothing like it";
        Assert.False(dialog.HasItems);
        Assert.False(dialog.CanAdd);
        Assert.Contains("nothing like it", dialog.EmptyText);
    }

    [Fact]
    public async Task Dialog_KeepsTheSelection_WhenTheListRefreshes()
    {
        var desktop = DesktopWith(Program(1, "a.exe", "A"), Program(2, "b.exe", "B"));
        var games = await GamesWith(desktop);
        using var dialog = new AddGameDialogViewModel(games);
        dialog.SelectedItem = dialog.Items.Single(i => i.Program.ProcessId == 2);

        desktop.RunningPrograms.Insert(0, Program(3, "c.exe", "C"));
        await games.RefreshRunningAsync();

        Assert.Equal(3, dialog.Items.Count);
        Assert.Equal(2, dialog.SelectedItem!.Program.ProcessId);
    }

    [Fact]
    public async Task Dialog_OnlyAddedPrograms_SelectsOne_ButCannotAdd()
    {
        var desktop = DesktopWith(Program(1, "game.exe", "Game"));
        var games = await GamesWith(desktop, new GameProfile { ExecutableName = "game.exe" });
        using var dialog = new AddGameDialogViewModel(games);

        Assert.NotNull(dialog.SelectedItem);
        Assert.False(dialog.CanAdd);
    }

    [Fact]
    public async Task MatchConflict_IsShown_ForTheGameThatLosesTheWindow()
    {
        var desktop = DesktopWith(Program(1, "notepad.exe", "Untitled - Notepad"));
        var games = await GamesWith(desktop,
            new GameProfile { ExecutableName = "notepad.exe", DisplayName = "A", WindowTitleContains = "Untitled" },
            new GameProfile { ExecutableName = "notepad.exe", DisplayName = "B", WindowTitleContains = "Notepad" });

        var a = games.Profiles.Single(p => p.Title == "A");
        var b = games.Profiles.Single(p => p.Title == "B");
        Assert.Null(a.MatchConflictText);
        Assert.Contains("\"A\"", b.MatchConflictText!.Replace('“', '"').Replace('”', '"'));

        // Turned off, A no longer takes it: no conflict for B
        a.Enabled = false;
        Assert.Null(b.MatchConflictText);
    }
}
