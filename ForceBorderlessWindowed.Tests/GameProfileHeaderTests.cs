using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.ViewModels;

namespace ForceBorderlessWindowed.Tests;

/// <summary>The status line in the pinned header of a game's details.</summary>
public class GameProfileHeaderTests
{
    private static GameProfileViewModel Game(bool enabled, string? titleFilter = null) =>
        new(new GameProfile { ExecutableName = "game.exe", Enabled = enabled, WindowTitleContains = titleFilter },
            new PlacementEditorViewModel(new PlacementSettings(), defaultEditor: null));

    [Fact]
    public void EnabledGame_NotRunning_SaysNotRunning()
    {
        Assert.Equal(Strings.Games_Status_NotRunning, Game(enabled: true).HeaderStatusText);
    }

    [Fact]
    public void OffGame_SaysOffFirst_EvenWithATitleMismatch()
    {
        var game = Game(enabled: false, titleFilter: "Modpack");
        game.CurrentWindowTitle = "Launcher";

        Assert.Equal($"{Strings.Games_Status_Disabled} · {Strings.Games_Status_NoMatchingTitle}", game.HeaderStatusText);
    }

    [Fact]
    public void RunningGame_LeavesTheStatusToTheStatusBar()
    {
        var game = Game(enabled: false);
        game.IsRunning = true;

        Assert.Null(game.HeaderStatusText);
    }

    [Fact]
    public void TurningOff_UpdatesTheHeader()
    {
        var game = Game(enabled: true);
        var changed = new List<string?>();
        game.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        game.Enabled = false;

        Assert.Contains(nameof(GameProfileViewModel.HeaderStatusText), changed);
        Assert.StartsWith(Strings.Games_Status_Disabled, game.HeaderStatusText);
    }
}
