using System.Windows.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;
using ForceBorderlessWindowed.Tests.Fakes;
using ForceBorderlessWindowed.ViewModels;

namespace ForceBorderlessWindowed.Tests;

/// <summary>"Reset to defaults" (General tab + hotkeys) and "Remove all" (Games tab): edits that Save applies.</summary>
public class ResetTests
{
    private static readonly MonitorBounds FullHd = new(0, 0, 1920, 1080);
    private static readonly MonitorBounds Wide = new(1920, 0, 3440, 1440);
    private static readonly DisplayInfo[] Displays =
        [new("a", "A", 1, FullHd, IsPrimary: true), new("b", "B", 2, Wide, IsPrimary: false)];

    [Fact]
    public void Placement_ResetTo_GivesTheDefaultsBack_AsEdits()
    {
        var editor = new PlacementEditorViewModel(new PlacementSettings
        {
            Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100), Horizontal = HorizontalAnchor.Right,
            Vertical = VerticalAnchor.Bottom, Display = DisplayTarget.Specific, DisplayId = "b", KeepAspectRatio = true,
            AspectRatio = AspectRatio.Standard4x3,
        }, defaultEditor: null);
        editor.SetDisplays(Displays, FullHd);
        var edits = new List<string?>();
        editor.PropertyChanged += (_, e) => { if (editor.IsEdit(e.PropertyName)) { edits.Add(e.PropertyName); } };

        editor.ResetTo(new PlacementSettings());

        var settings = editor.ToSettings()!;
        Assert.Equal(PlacementMode.Automatic, settings.Mode);
        Assert.Equal((AspectRatio.Widescreen16x9, false), (settings.AspectRatio, settings.KeepAspectRatio));
        Assert.Equal((HorizontalAnchor.Center, VerticalAnchor.Center), (settings.Horizontal, settings.Vertical));
        Assert.Equal(DisplayTarget.Primary, settings.Display);
        Assert.NotEmpty(edits);   // counts as an unsaved change

        // Custom afterwards starts from the primary display's automatic size, not the old values
        editor.IsCustom = true;
        Assert.Equal(new WindowPlacement(1920, 1080, 0), editor.ToSettings()!.Custom);
    }

    [Fact]
    public void General_ResetToDefaults_KeepsAutostart()
    {
        var general = new GeneralSettingsViewModel(
            new AppSettings { Language = "de", Theme = AppTheme.Dark }, AutostartMode.Elevated, canStartElevated: true);
        general.DefaultPlacement.SetDisplays(Displays, FullHd);

        general.ResetToDefaults();

        Assert.Null(general.SelectedLanguage.CultureName);
        Assert.Equal(AppTheme.System, general.SelectedTheme.Theme);
        Assert.Equal(AutostartMode.Elevated, general.Autostart);
    }

    [Fact]
    public void Hotkeys_ResetToDefaults()
    {
        var hotkeys = new HotkeysViewModel(new HotkeySettings
        {
            TogglePause = new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.P),
            Save = new Hotkey(ModifierKeys.Control | ModifierKeys.Shift, Key.S),
            PlaySounds = false,
            ShowMessages = false,
        }, new NoHotkeys());

        hotkeys.ResetToDefaults();

        Assert.Equal(new HotkeySettings(), hotkeys.ToSettings());
    }

    [Fact]
    public void Games_RemoveAll_EmptiesTheList()
    {
        var defaultPlacement = new PlacementEditorViewModel(new PlacementSettings(), defaultEditor: null);
        var games = new GamesViewModel(
            [new GameProfile { ExecutableName = "a.exe" }, new GameProfile { ExecutableName = "b.exe" }],
            defaultPlacement, new FakeDesktop(), new NoStatuses(), new NoIcons());
        var changed = 0;
        games.Changed += (_, _) => changed++;

        Assert.True(games.RemoveAllCommand.CanExecute(null));
        games.RemoveAllCommand.Execute(null);

        Assert.Empty(games.Profiles);
        Assert.Null(games.SelectedProfile);
        Assert.True(games.IsEmpty);
        Assert.False(games.RemoveAllCommand.CanExecute(null));
        Assert.True(changed > 0);
    }
}
