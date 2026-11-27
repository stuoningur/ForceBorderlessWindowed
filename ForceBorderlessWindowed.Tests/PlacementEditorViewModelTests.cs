using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.ViewModels;

namespace ForceBorderlessWindowed.Tests;

public class PlacementEditorViewModelTests
{
    private static readonly MonitorBounds FullHd = new(0, 0, 1920, 1080);

    private static PlacementEditorViewModel EditorFor(WindowPlacement custom)
    {
        var editor = new PlacementEditorViewModel(new PlacementSettings { Mode = PlacementMode.Custom, Custom = custom }, defaultEditor: null);
        editor.SetDisplays([new DisplayInfo("a", "A", 1, FullHd, IsPrimary: true)], FullHd);
        return editor;
    }

    [Fact]
    public void SavedValues_TooLargeForTheDisplay_AreAWarning_NotAnError()
    {
        var editor = EditorFor(new WindowPlacement(2560, 1440, 0));

        Assert.True(editor.IsValid);
        Assert.Null(editor.WidthError);
        Assert.NotNull(editor.FitWarning);
    }

    [Fact]
    public void EditedValues_TooLargeForTheDisplay_AreAnError()
    {
        var editor = EditorFor(new WindowPlacement(2560, 1440, 0));

        editor.WidthText = "2561";

        Assert.False(editor.IsValid);
        Assert.NotNull(editor.WidthError);
        Assert.Null(editor.FitWarning);
    }

    [Theory]
    [InlineData(OffsetAlignment.Left, 0)]
    [InlineData(OffsetAlignment.Center, 320)]
    [InlineData(OffsetAlignment.Right, 640)]
    public void AlignX_FillsTheOffsetForTheDisplay(OffsetAlignment alignment, int expected)
    {
        var editor = EditorFor(new WindowPlacement(1280, 720, 100));

        Assert.True(editor.AlignXCommand.CanExecute(alignment));
        editor.AlignXCommand.Execute(alignment);

        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.CurrentCulture), editor.OffsetXText);
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void AlignX_IsDisabled_ForAWidthThatDoesNotFit()
    {
        var editor = EditorFor(new WindowPlacement(1280, 720, 0));

        editor.WidthText = "2000";

        Assert.False(editor.AlignXCommand.CanExecute(OffsetAlignment.Right));
    }

    [Fact]
    public void PickingAnotherDisplay_KeepsTheCustomValues()
    {
        var ultrawide = new MonitorBounds(1920, 0, 3440, 1440);
        var editor = new PlacementEditorViewModel(
            new PlacementSettings { Mode = PlacementMode.Custom, Custom = new WindowPlacement(1280, 720, 100) }, defaultEditor: null);
        editor.SetDisplays([new DisplayInfo("a", "A", 1, FullHd, IsPrimary: true), new DisplayInfo("b", "B", 2, ultrawide, IsPrimary: false)], FullHd);
        var original = editor.SelectedDisplay;

        editor.SelectedDisplay = editor.DisplayOptions.Single(o => o.Id == "b");
        Assert.Equal(new WindowPlacement(1280, 720, 100), editor.ToSettings()!.Custom);

        editor.SelectedDisplay = original;
        Assert.Equal(new WindowPlacement(1280, 720, 100), editor.ToSettings()!.Custom);
    }

    [Fact]
    public void PickingAnotherDisplay_UpdatesTheHiddenUntouchedPrefill()
    {
        var ultrawide = new MonitorBounds(1920, 0, 3440, 1440);
        var editor = new PlacementEditorViewModel(new PlacementSettings(), defaultEditor: null);
        editor.SetDisplays([new DisplayInfo("a", "A", 1, FullHd, IsPrimary: true), new DisplayInfo("b", "B", 2, ultrawide, IsPrimary: false)], FullHd);
        Assert.Equal((1920, 1080), (editor.Width, editor.Height));

        editor.SelectedDisplay = editor.DisplayOptions.Single(o => o.Id == "b");
        editor.IsCustom = true;

        Assert.Equal(new WindowPlacement(2560, 1440, 440), editor.ToSettings()!.Custom);
    }

    [Fact]
    public void FitToDisplay_ShrinksOnlyWhatIsTooLarge()
    {
        var editor = EditorFor(new WindowPlacement(1600, 1440, 500));
        Assert.NotNull(editor.FitWarning);
        Assert.True(editor.CanFitToDisplay);

        editor.FitToDisplayCommand.Execute(null);

        Assert.Equal(new WindowPlacement(1600, 1080, 320), editor.ToSettings()!.Custom);
        Assert.Null(editor.FitWarning);
        Assert.False(editor.CanFitToDisplay);
        Assert.True(editor.IsValid);
    }

    [Fact]
    public void SavedValues_ThatFit_GiveNoWarning()
    {
        var editor = EditorFor(new WindowPlacement(1280, 720, 0));

        Assert.True(editor.IsValid);
        Assert.Null(editor.FitWarning);
    }

    [Fact]
    public void KeepAspectRatio_ChangesTheAutomaticSize_AndIsSaved()
    {
        var editor = new PlacementEditorViewModel(new PlacementSettings(), defaultEditor: null);
        var monitor = new MonitorBounds(0, 0, 1920, 1200);
        editor.SetDisplays([new DisplayInfo("a", "A", 1, monitor, IsPrimary: true)], monitor);

        Assert.Equal(new WindowPlacement(1920, 1200, 0), editor.AutomaticPlacement);

        editor.KeepAspectRatio = true;

        Assert.Equal(new WindowPlacement(1920, 1080, 0), editor.AutomaticPlacement);
        Assert.True(editor.ToSettings()!.KeepAspectRatio);
        Assert.True(editor.IsEdit(nameof(PlacementEditorViewModel.KeepAspectRatio)));
    }

    [Fact]
    public void DisplayTheGameIsOn_PreviewFollowsTheRunningGame_AlsoUnderUseDefault()
    {
        var ultrawide = new MonitorBounds(1920, 0, 3440, 1440);
        DisplayInfo[] displays = [new("a", "A", 1, FullHd, IsPrimary: true), new("b", "B", 2, ultrawide, IsPrimary: false)];

        var defaultEditor = new PlacementEditorViewModel(new PlacementSettings { Display = DisplayTarget.Current }, defaultEditor: null);
        var own = new PlacementEditorViewModel(new PlacementSettings { Display = DisplayTarget.Current }, defaultEditor);
        var usingDefault = new PlacementEditorViewModel(null, defaultEditor);
        foreach (var editor in new[] { defaultEditor, own, usingDefault })
        {
            editor.SetDisplays(displays, FullHd);   // the settings window is on the Full HD monitor
        }

        Assert.Equal(1920, own.Preview!.MonitorWidth);
        Assert.Equal(1920, usingDefault.Preview!.MonitorWidth);

        // The game runs on the ultrawide one
        own.GameMonitor = ultrawide;
        usingDefault.GameMonitor = ultrawide;

        Assert.Equal(3440, own.Preview!.MonitorWidth);
        Assert.Equal(new WindowPlacement(2560, 1440, 440), own.AutomaticPlacement);
        Assert.Equal(3440, usingDefault.Preview!.MonitorWidth);
        Assert.Equal(2560, usingDefault.Preview!.Width);
        Assert.Equal(1920, defaultEditor.Preview!.MonitorWidth);   // the default itself has no game

        // The texts say the same as the preview, not the default's own 1920 × 1080
        const string size = "2560 × 1440";
        Assert.Contains(size, usingDefault.Summary);
        Assert.Contains(size, usingDefault.DefaultSummary);
        Assert.DoesNotContain(size, defaultEditor.Summary);

        // Closed again: back to the guess
        own.GameMonitor = null;
        Assert.Equal(1920, own.Preview!.MonitorWidth);
    }
}
