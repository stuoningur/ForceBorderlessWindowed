using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Tests.Fakes;
using ForceBorderlessWindowed.ViewModels;

using Microsoft.Extensions.Logging.Abstractions;

namespace ForceBorderlessWindowed.Tests;

/// <summary>
/// Saves from outside an open settings window (a game added by hotkey, a game turned on or off in the tray, Pause)
/// are taken into it, so a Save there doesn't undo them, while its own unsaved edits stay.
/// </summary>
public class SettingsWindowMergeTests
{
    private readonly GameProfile _game = new() { ExecutableName = "game.exe", DisplayName = "Game" };
    private readonly FakeSettingsService _settings;
    private readonly SettingsViewModel _window;

    public SettingsWindowMergeTests()
    {
        _settings = new FakeSettingsService(new AppSettings { Games = [_game] });
        var desktop = new FakeDesktop();
        _window = new SettingsViewModel(_settings, new FakeAutostart(), desktop, desktop, new NoStatuses(), new NoIcons(),
            new NoHotkeys(), new FakeShell(), new NoScreenPreview(), desktop, NullLogger<SettingsViewModel>.Instance);
    }

    private GameProfileViewModel InWindow(Guid id) => _window.Games.Profiles.Single(p => p.Id == id);

    [Fact]
    public async Task GameAddedByHotkey_IsKeptBySavingTheWindow_WithTheWindowsOwnEdits()
    {
        InWindow(_game.Id).DisplayName = "Renamed";   // unsaved edit in the window

        // The "add the game in the foreground" hotkey saves on its own
        var added = new GameProfile { ExecutableName = "other.exe" };
        _settings.Save(_settings.Current with { Games = [.. _settings.Current.Games, added] });

        Assert.Contains(_window.Games.Profiles, p => p.Id == added.Id);
        Assert.True(await _window.SaveChangesAsync());

        Assert.Contains(_settings.Current.Games, g => g.Id == added.Id);
        Assert.Equal("Renamed", _settings.Current.Games.Single(g => g.Id == _game.Id).DisplayName);
    }

    [Fact]
    public async Task GameTurnedOffInTheTray_StaysOff_AfterSavingTheWindow()
    {
        InWindow(_game.Id).LockCursor = true;   // unsaved edit of the same game

        _settings.Save(_settings.Current with { Games = [_game with { Enabled = false }] });

        Assert.False(InWindow(_game.Id).Enabled);
        Assert.True(await _window.SaveChangesAsync());

        var saved = _settings.Current.Games.Single();
        Assert.False(saved.Enabled);
        Assert.True(saved.LockCursor);
    }

    [Fact]
    public async Task GameRemovedInTheWindow_StaysRemoved_WhenPauseIsSavedMeanwhile()
    {
        _window.Games.SelectedProfile = InWindow(_game.Id);
        _window.Games.RemoveCommand.Execute(null);

        _settings.Save(_settings.Current with { IsPaused = true });

        Assert.Empty(_window.Games.Profiles);
        Assert.True(await _window.SaveChangesAsync());
        Assert.Empty(_settings.Current.Games);
        Assert.True(_settings.Current.IsPaused);   // the window's save keeps the pause from outside
    }
}
