using System.ComponentModel;
using System.Globalization;
using System.Text;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

public enum PlacementChoice
{
    UseDefault,
    Automatic,
    Custom,
    GameSize,
}

/// <summary>Common placements the editor can fill in at once, see <see cref="PlacementEditorViewModel.ApplyPresetCommand"/>.</summary>
public enum PlacementPreset
{
    FillDisplay,
    Centered16x9,
    Left16x9,
    Right16x9,
    LeftHalf,
    RightHalf,
}

/// <summary>An entry of the presets menu.</summary>
public sealed record PresetOption(PlacementPreset Preset, string Label);

/// <summary>
/// Editor for a <see cref="PlacementSettings"/>. Used for the global default (no "Use default" option)
/// and for each game (with "Use default", which then shows what the default currently resolves to).
/// </summary>
public partial class PlacementEditorViewModel : ObservableObject
{
    private readonly PlacementEditorViewModel? _defaultEditor;
    private bool _customValuesInitialized;

    /// <summary>The custom values as saved; null if there were none. See <see cref="IsSavedCustomUnchanged"/>.</summary>
    private readonly WindowPlacement? _savedCustom;

    /// <summary>The saved display choice, used until the display list is known (and to keep "not connected" ones).</summary>
    private readonly (DisplayTarget Target, string? Id, string? Name) _savedDisplay;

    private IReadOnlyList<DisplayInfo> _displays = [];

    /// <summary>True while SetDisplays replaces the list: selection changes then don't come from the user.</summary>
    private bool _rebuildingOptions;
    private MonitorBounds? _windowMonitor;

    /// <summary>The monitor the selected display resolves to: basis for the preview (see also _fitMonitor).</summary>
    private MonitorBounds? _monitor;

    /// <param name="settings">The saved placement; null = "Use default".</param>
    /// <param name="defaultEditor">The editor of the global default, or null if this IS the global default.</param>
    public PlacementEditorViewModel(PlacementSettings? settings, PlacementEditorViewModel? defaultEditor)
    {
        _defaultEditor = defaultEditor;

        Choice = ChoiceFor(settings);

        // Kept while Custom is selected (and saved with it), so switching back to Automatic restores it
        SelectedAspectRatio = AspectRatios.FirstOrDefault(r => r.Ratio == settings?.AspectRatio) ?? AspectRatios[0];
        KeepAspectRatio = settings?.KeepAspectRatio ?? false;

        // As saved; older files without it get what they did back then (Custom: its offset, the rest: centered)
        Horizontal = settings?.EffectiveHorizontal() ?? HorizontalAnchor.Center;
        Vertical = settings?.Vertical ?? VerticalAnchor.Center;

        _savedCustom = settings is { Mode: PlacementMode.Custom } ? settings.Custom : null;

        if (settings?.Custom is { } custom)
        {
            // Through the texts: a saved value equal to the number's default (an offset of 0) wouldn't count as a
            // change of the number, and the field would stay empty
            WidthText = custom.Width.ToString(CultureInfo.CurrentCulture);
            HeightText = custom.Height.ToString(CultureInfo.CurrentCulture);
            OffsetXText = custom.OffsetX.ToString(CultureInfo.CurrentCulture);
            _customValuesInitialized = true;
        }

        _savedDisplay = settings is null
            ? (DisplayTarget.Primary, null, null)
            : (settings.Display, settings.DisplayId, settings.DisplayName);

        if (defaultEditor is not null)
        {
            // Keep "Currently: ..." up to date while the default is being edited on the other tab
            defaultEditor.PropertyChanged += OnDefaultEditorChanged;
        }
    }

    public bool CanUseDefault => _defaultEditor is not null;

    private PlacementChoice ChoiceFor(PlacementSettings? settings) => settings switch
    {
        null when CanUseDefault => PlacementChoice.UseDefault,
        { Mode: PlacementMode.Custom } => PlacementChoice.Custom,
        { Mode: PlacementMode.GameSize } => PlacementChoice.GameSize,
        _ => PlacementChoice.Automatic,
    };

    /// <summary>
    /// Puts every choice back to <paramref name="settings"/>, as an edit: it takes effect with Save, and Close without
    /// saving undoes it (used by "Reset to defaults"). Without custom values the custom fields get the automatic values
    /// of the display, as on a fresh game.
    /// </summary>
    public void ResetTo(PlacementSettings settings)
    {
        Choice = ChoiceFor(settings);
        SelectedAspectRatio = AspectRatios.FirstOrDefault(r => r.Ratio == settings.AspectRatio) ?? AspectRatios[0];
        KeepAspectRatio = settings.KeepAspectRatio;
        Horizontal = settings.EffectiveHorizontal();
        Vertical = settings.Vertical;
        if (DisplayOptions.Count > 0)
        {
            SelectedDisplay = DisplayOptions.FirstOrDefault(o => o.Matches(settings.Display, settings.DisplayId)) ?? DisplayOptions[0];
        }

        if (settings.Custom is { } custom)
        {
            Prefill(custom);
            _prefill = null;   // real values, not a pre-fill
        }
        else if (AutomaticPlacement is { } automatic)
        {
            Prefill(automatic);
        }
    }

    /// <summary>For "Show on display"; null = no such button (set by the tab that owns this editor).</summary>
    public IScreenPreview? ScreenPreview
    {
        get;
        set
        {
            field = value;
            OnPropertyChanged(nameof(CanShowOnDisplay));
            ShowOnDisplayCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>
    /// The game's window right now (its monitor and inner size), for "Show on display"; null if it isn't running.
    /// Only for a game's editor (the default has no game). Asks Windows, so only called on a click.
    /// </summary>
    public Func<RunningGameWindow?>? RunningGameProvider { get; set; }

    /// <summary>
    /// The monitor the game's window is on, while it runs (refreshed with the running markers); null otherwise and
    /// for the default's editor. For "the display the game is on" it replaces the settings window's monitor as the
    /// guess, so preview, summary and "Show on display" show where the watcher really puts the window.
    /// </summary>
    public MonitorBounds? GameMonitor
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            UpdateMonitor();
            OnSummaryChanged();
            OnPropertyChanged(nameof(DefaultSummary));   // "Use default" on the game's display: see DefaultOnGameMonitor
        }
    }

    /// <summary>Whether the game runs: "Show on display" for the game's own size needs its window.</summary>
    public bool IsGameRunning
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                OnShowOnDisplayChanged();
            }
        }
    }

    /// <summary>The editor whose choice applies: the default's for "Use default".</summary>
    private PlacementEditorViewModel Effective => Choice == PlacementChoice.UseDefault && _defaultEditor is not null ? _defaultEditor : this;

    public bool CanShowOnDisplay => ScreenPreview is not null;

    /// <summary>The game's own size, but no running game to take it from: says why the button is off.</summary>
    public bool ShowOnDisplayNeedsGame =>
        ScreenPreview is not null && Effective.Choice == PlacementChoice.GameSize && !(IsGameRunning && RunningGameProvider is not null);

    private void OnShowOnDisplayChanged()
    {
        OnPropertyChanged(nameof(ShowOnDisplayNeedsGame));
        ShowOnDisplayCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Outlines the effective placement on the real monitor for a moment (see IScreenPreview).</summary>
    [RelayCommand(CanExecute = nameof(CanShowOnDisplayNow))]
    private void ShowOnDisplay()
    {
        var source = Effective;
        if (ScreenPreview is not { } screen || source._monitor is not { } monitor)
        {
            return;
        }

        // "The display the game is on": the game's monitor if it runs (the watcher uses that one too), otherwise the
        // settings window's monitor stands in, as in the preview above
        var game = RunningGameProvider?.Invoke();
        if (source.SelectedDisplay?.Target == DisplayTarget.Current && game is not null)
        {
            monitor = game.Monitor;
        }

        // The game's own size needs the running game's size (the button is off without it)
        if (source.ToSettings() is not { } settings
            || (settings.Mode == PlacementMode.GameSize && game?.ClientSize is null))
        {
            return;
        }

        // The same calculation as the watcher's
        var placement = PlacementCalculator.Resolve(settings, settings, monitor, game?.ClientSize);
        screen.Show(monitor, placement, string.Format(CultureInfo.CurrentCulture, "{0} × {1}", placement.Width, placement.Height));
    }

    private bool CanShowOnDisplayNow()
    {
        var source = Effective;
        return ScreenPreview is not null && source._monitor is not null
            && (source.Choice != PlacementChoice.Custom || source.IsValid)
            && !ShowOnDisplayNeedsGame;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUseDefault), nameof(IsAutomatic), nameof(IsCustom), nameof(IsGameSize),
        nameof(ShowDisplaySelection), nameof(EffectiveHorizontal), nameof(IsAnchorLeft), nameof(IsAnchorCenterX),
        nameof(IsAnchorRight), nameof(IsAnchorOffset), nameof(ShowOffsetX))]
    public partial PlacementChoice Choice { get; set; }

    // One bool per RadioButton: IsChecked binds to bools, and setting false is ignored,
    // because the RadioButton group unchecks the old one when another one is checked.
    public bool IsUseDefault
    {
        get => Choice == PlacementChoice.UseDefault;
        set { if (value) { Choice = PlacementChoice.UseDefault; } }
    }

    public bool IsAutomatic
    {
        get => Choice == PlacementChoice.Automatic;
        set { if (value) { Choice = PlacementChoice.Automatic; } }
    }

    public bool IsCustom
    {
        get => Choice == PlacementChoice.Custom;
        set { if (value) { Choice = PlacementChoice.Custom; } }
    }

    public bool IsGameSize
    {
        get => Choice == PlacementChoice.GameSize;
        set { if (value) { Choice = PlacementChoice.GameSize; } }
    }

    public IReadOnlyList<AspectRatioOption> AspectRatios { get; } =
    [
        new(AspectRatio.Widescreen16x9, Strings.Placement_AspectRatio_16x9, "16:9"),
        new(AspectRatio.Widescreen16x10, "16:10", "16:10"),
        new(AspectRatio.Ultrawide21x9, Strings.Placement_AspectRatio_21x9, "21:9"),
        new(AspectRatio.Standard4x3, "4:3", "4:3"),
        new(AspectRatio.Standard5x4, "5:4", "5:4"),
    ];

    /// <summary>Shape of the window for "Automatic".</summary>
    [ObservableProperty]
    public partial AspectRatioOption SelectedAspectRatio { get; set; }

    /// <summary>See <see cref="PlacementSettings.KeepAspectRatio"/>: the shape also on narrower displays.</summary>
    [ObservableProperty]
    public partial bool KeepAspectRatio { get; set; }

    partial void OnKeepAspectRatioChanged(bool value)
    {
        UpdateMonitor();
        OnSummaryChanged();
    }

    // Same monitor, new shape: recalculate the automatic size. The summary names the ratio, so it changes
    // even where the size doesn't (a monitor narrower than both ratios gets the full monitor either way).
    partial void OnSelectedAspectRatioChanged(AspectRatioOption value)
    {
        UpdateMonitor();
        OnSummaryChanged();
    }

    /// <summary>
    /// Where the window sits horizontally, as chosen. <see cref="HorizontalAnchor.Offset"/> stays chosen while another
    /// mode is selected (so going back to Custom keeps it), but only Custom has an offset: see <see cref="EffectiveHorizontal"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveHorizontal), nameof(IsAnchorLeft), nameof(IsAnchorCenterX), nameof(IsAnchorRight),
        nameof(IsAnchorOffset), nameof(ShowOffsetX))]
    public partial HorizontalAnchor Horizontal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAnchorTop), nameof(IsAnchorCenterY), nameof(IsAnchorBottom))]
    public partial VerticalAnchor Vertical { get; set; }

    /// <summary>The horizontal position that applies to the selected mode (and gets saved).</summary>
    public HorizontalAnchor EffectiveHorizontal =>
        Horizontal == HorizontalAnchor.Offset && Choice != PlacementChoice.Custom ? HorizontalAnchor.Center : Horizontal;

    // One bool per RadioButton, like the modes above
    public bool IsAnchorLeft
    {
        get => EffectiveHorizontal == HorizontalAnchor.Left;
        set { if (value) { Horizontal = HorizontalAnchor.Left; } }
    }

    public bool IsAnchorCenterX
    {
        get => EffectiveHorizontal == HorizontalAnchor.Center;
        set { if (value) { Horizontal = HorizontalAnchor.Center; } }
    }

    public bool IsAnchorRight
    {
        get => EffectiveHorizontal == HorizontalAnchor.Right;
        set { if (value) { Horizontal = HorizontalAnchor.Right; } }
    }

    public bool IsAnchorOffset
    {
        get => EffectiveHorizontal == HorizontalAnchor.Offset;
        set { if (value) { Horizontal = HorizontalAnchor.Offset; } }
    }

    public bool IsAnchorTop
    {
        get => Vertical == VerticalAnchor.Top;
        set { if (value) { Vertical = VerticalAnchor.Top; } }
    }

    public bool IsAnchorCenterY
    {
        get => Vertical == VerticalAnchor.Center;
        set { if (value) { Vertical = VerticalAnchor.Center; } }
    }

    public bool IsAnchorBottom
    {
        get => Vertical == VerticalAnchor.Bottom;
        set { if (value) { Vertical = VerticalAnchor.Bottom; } }
    }

    /// <summary>The Offset X field: only for Custom with an exact offset; the anchors need no number.</summary>
    public bool ShowOffsetX => IsAnchorOffset;

    partial void OnHorizontalChanged(HorizontalAnchor value)
    {
        OnValidationChanged();   // the offset only counts for the exact offset
        OnSummaryChanged();
    }

    partial void OnVerticalChanged(VerticalAnchor value) => OnSummaryChanged();

    public IReadOnlyList<PresetOption> Presets { get; } =
    [
        new(PlacementPreset.FillDisplay, Strings.Placement_Preset_FillDisplay),
        new(PlacementPreset.Centered16x9, Strings.Placement_Preset_Centered16x9),
        new(PlacementPreset.Left16x9, Strings.Placement_Preset_Left16x9),
        new(PlacementPreset.Right16x9, Strings.Placement_Preset_Right16x9),
        new(PlacementPreset.LeftHalf, Strings.Placement_Preset_LeftHalf),
        new(PlacementPreset.RightHalf, Strings.Placement_Preset_RightHalf),
    ];

    /// <summary>
    /// Fills in a common placement, once: afterwards the mode, size and position are ordinary choices that can be
    /// changed. Sizes in pixels (the halves, the whole display) come from the previewed display.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanApplyPreset))]
    private void ApplyPreset(PlacementPreset preset)
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        switch (preset)
        {
            case PlacementPreset.FillDisplay:
                FillCustom(monitor.Width, monitor.Height, HorizontalAnchor.Center);
                break;
            case PlacementPreset.LeftHalf or PlacementPreset.RightHalf:
                FillCustom(monitor.Width / 2, monitor.Height,
                    preset == PlacementPreset.LeftHalf ? HorizontalAnchor.Left : HorizontalAnchor.Right);
                break;
            default:
                Choice = PlacementChoice.Automatic;
                SelectedAspectRatio = AspectRatios.First(r => r.Ratio == AspectRatio.Widescreen16x9);
                KeepAspectRatio = false;
                Horizontal = preset switch
                {
                    PlacementPreset.Left16x9 => HorizontalAnchor.Left,
                    PlacementPreset.Right16x9 => HorizontalAnchor.Right,
                    _ => HorizontalAnchor.Center,
                };
                break;
        }

        Vertical = VerticalAnchor.Center;
    }

    private bool CanApplyPreset() => _monitor is not null;

    private void FillCustom(int width, int height, HorizontalAnchor horizontal)
    {
        WidthText = width.ToString(CultureInfo.CurrentCulture);
        HeightText = height.ToString(CultureInfo.CurrentCulture);
        Choice = PlacementChoice.Custom;
        Horizontal = horizontal;
    }

    /// <summary>Where a window of this size goes on <paramref name="monitor"/> with this editor's position choice.</summary>
    private WindowRect Place(WindowPlacement size, MonitorBounds monitor) =>
        PlacementCalculator.Position(size, EffectiveHorizontal, Vertical, monitor);

    /// <summary>"Use default" takes the display from the default too, so the selection is only shown otherwise.</summary>
    public bool ShowDisplaySelection => Choice != PlacementChoice.UseDefault;

    /// <summary>
    /// In place of the selection while "Use default" is chosen: which display that is. Takes the selection's room,
    /// so the options below don't move when switching between "Use default" and the others.
    /// </summary>
    public string DefaultDisplayText =>
        _defaultEditor?.SelectedDisplay is { } display
            ? string.Format(CultureInfo.CurrentCulture, Formats.Placement_DisplayFromDefault, display.Label)
            : "";

    /// <summary>Primary, "the game's display", every connected display, plus the saved one if it's not connected.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<DisplayOption> DisplayOptions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Preview))]
    public partial DisplayOption? SelectedDisplay { get; set; }

    // The numbers are the last valid input: preview, summary and what gets saved. The fields bind to the
    // *Text properties instead, so text that isn't a valid number shows up here as an error, instead of
    // silently leaving the old number in place (which Save would then have stored).

    // Validation (errors, IsValid, preview outline) is raised by OnValidationChanged, see the partial methods below

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int Width { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int Height { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    public partial int OffsetX { get; set; }

    /// <summary>What the width field shows, exactly as typed.</summary>
    [ObservableProperty]
    public partial string WidthText { get; set; } = "";

    [ObservableProperty]
    public partial string HeightText { get; set; } = "";

    [ObservableProperty]
    public partial string OffsetXText { get; set; } = "";

    /// <summary>The width text isn't a usable number; null = it is (the number is then in <see cref="Width"/>).</summary>
    [ObservableProperty]
    private partial string? WidthInputError { get; set; }

    [ObservableProperty]
    private partial string? HeightInputError { get; set; }

    [ObservableProperty]
    private partial string? OffsetXInputError { get; set; }

    /// <summary>What's wrong with the width field, shown right below it; null = fine.</summary>
    public string? WidthError => WidthInputError ?? FitErrors.Width;

    public string? HeightError => HeightInputError ?? FitErrors.Height;

    /// <summary>Only while the field is shown (see <see cref="UsesOffset"/>): a hidden error would block saving.</summary>
    public string? OffsetXError => UsesOffset ? OffsetXInputError ?? FitErrors.OffsetX : null;

    /// <summary>
    /// The monitor custom values must fit on, or null if there is nothing reliable to check against:
    /// the displays aren't known yet, or the selected specific display is unplugged (it falls back to the
    /// primary display, whose size says nothing about the unplugged one; the watcher shrinks the window then).
    /// For "the display the game is on": the largest connected display (see UpdateMonitor).
    /// </summary>
    private MonitorBounds? _fitMonitor;

    /// <summary>
    /// Custom values that don't fit on the display: errors like an invalid number, so they can't be saved.
    /// Each field is checked as soon as its own number is valid; the offset only once the width fits,
    /// otherwise no offset would help.
    /// </summary>
    private (string? Width, string? Height, string? OffsetX) FitErrors
    {
        get
        {
            // Saved values that don't fit this display (anymore): a warning, not an error. The watcher shrinks the
            // window to fit anyway, and an error would block saving anything else (and count as an unsaved change
            // as soon as the window opens, e.g. after lowering the resolution).
            if (Choice != PlacementChoice.Custom || _fitMonitor is not { } monitor || IsSavedCustomUnchanged)
            {
                return default;
            }

            var widthFits = Width <= monitor.Width;
            return (
                WidthInputError is null && !widthFits ? Format(Formats.Placement_Error_WidthTooLarge, monitor.Width) : null,
                HeightInputError is null && Height > monitor.Height ? Format(Formats.Placement_Error_HeightTooLarge, monitor.Height) : null,
                UsesOffset && WidthInputError is null && OffsetXInputError is null && widthFits && OffsetX + Width > monitor.Width
                    ? Format(Formats.Placement_Error_OffsetTooLarge, monitor.Width - Width)
                    : null);
        }
    }

    /// <summary>
    /// Custom with an exact offset: only then the Offset X field is shown and counts (its errors, the fit check). With
    /// an anchor the number is kept as it is, for switching back.
    /// </summary>
    private bool UsesOffset => Choice == PlacementChoice.Custom && EffectiveHorizontal == HorizontalAnchor.Offset;

    /// <summary>The custom numbers are exactly the saved ones (and the fields in use hold valid numbers).</summary>
    private bool IsSavedCustomUnchanged =>
        _savedCustom is { } saved
        && WidthInputError is null && HeightInputError is null
        && (Width, Height) == (saved.Width, saved.Height)
        && (!UsesOffset || (OffsetXInputError is null && OffsetX == saved.OffsetX));

    /// <summary>Too large for <paramref name="monitor"/>: wider, taller, or (with an exact offset) too far right.</summary>
    private bool DoesNotFit(MonitorBounds monitor) =>
        Width > monitor.Width || Height > monitor.Height || (UsesOffset && OffsetX + Width > monitor.Width);

    /// <summary>
    /// Saved custom values that don't fit the display: the game window is shrunk to fit. Shown below the fields;
    /// doesn't block saving. Editing them turns it into the usual field errors.
    /// </summary>
    public string? FitWarning =>
        Choice == PlacementChoice.Custom && IsSavedCustomUnchanged && _fitMonitor is { } monitor && DoesNotFit(monitor)
            ? string.Format(CultureInfo.CurrentCulture, Formats.Placement_Warning_DoesNotFit, monitor.Width, monitor.Height)
            : null;

    /// <summary>What "Automatic" results in on the selected display (preview only).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(Preview))]
    public partial WindowPlacement? AutomaticPlacement { get; private set; }

    public bool IsValid => FirstInvalidField is null;

    /// <summary>The first field (in reading order) whose input can't be saved; null if all are fine or not used.</summary>
    public SettingsField? FirstInvalidField =>
        Choice != PlacementChoice.Custom ? null
        : WidthError is not null ? SettingsField.Width
        : HeightError is not null ? SettingsField.Height
        : OffsetXError is not null ? SettingsField.OffsetX
        // Not filled in yet (the display isn't known yet): the numbers are still 0
        : !WindowPlacement.IsValidSize(Width, Height, UsesOffset ? OffsetX : 0) ? SettingsField.Width
        : null;

    /// <summary>
    /// Outline color of the preview: the custom window doesn't fit on the monitor drawn. Unlike the fit errors
    /// also for an unplugged display, where the preview shows the primary display instead.
    /// </summary>
    private bool ExceedsMonitor(MonitorBounds monitor) =>
        Choice == PlacementChoice.Custom
        && WidthInputError is null && HeightInputError is null && (!UsesOffset || OffsetXInputError is null)
        && DoesNotFit(monitor);

    /// <summary>
    /// What the preview drawing shows: the effective placement on its monitor. For "Use default" that is
    /// the default's. null until the displays are known.
    /// </summary>
    public PlacementPreviewData? Preview
    {
        get
        {
            if (Choice == PlacementChoice.UseDefault)
            {
                return DefaultOnGameMonitor is { } gameMonitor
                    ? _defaultEditor!.BuildPreview(gameMonitor)
                    : _defaultEditor?.Preview;
            }

            return _monitor is { } monitor ? BuildPreview(monitor) : null;
        }
    }

    /// <summary>
    /// The default's "display the game is on" is this game's monitor while it runs (the default has no game):
    /// that monitor then, so preview and summary of "Use default" describe the same window. null = the default's own.
    /// </summary>
    private MonitorBounds? DefaultOnGameMonitor =>
        _defaultEditor is { SelectedDisplay.Target: DisplayTarget.Current } ? GameMonitor : null;

    /// <summary>This editor's placement drawn on the given monitor.</summary>
    private PlacementPreviewData BuildPreview(MonitorBounds monitor)
    {
        // The game decides the size: an example window at three quarters of the monitor, labeled as such
        if (Choice == PlacementChoice.GameSize)
        {
            var example = Place(PlacementCalculator.CenterOnMonitor((monitor.Width * 3 / 4, monitor.Height * 3 / 4), monitor), monitor);
            return new PlacementPreviewData(monitor.Width, monitor.Height, example.OffsetX, example.OffsetY, example.Width,
                example.Height, Exceeds: false, Strings.Placement_GameSize_PreviewLabel);
        }

        // Custom as typed, not shrunk to fit: the drawing shows the overflow in the caution color
        var window = Place(Choice == PlacementChoice.Custom ? new WindowPlacement(Width, Height, OffsetX) : AutomaticOn(monitor), monitor);
        return new PlacementPreviewData(monitor.Width, monitor.Height, window.OffsetX, window.OffsetY, window.Width, window.Height,
            ExceedsMonitor(monitor));
    }

    /// <summary>The automatic size on <paramref name="monitor"/>: <see cref="AutomaticPlacement"/> for the previewed one.</summary>
    private WindowPlacement AutomaticOn(MonitorBounds monitor) =>
        monitor == _monitor && AutomaticPlacement is { } own
            ? own
            : PlacementCalculator.CalculateAutomatic(monitor, SelectedAspectRatio.Ratio, KeepAspectRatio);

    /// <summary>Short description of the effective placement, e.g. "Automatic 16:9 – 2560 × 1440, left, primary display".</summary>
    public string Summary => SummaryOn(null);

    /// <summary>The summary, with the automatic size calculated for <paramref name="monitor"/> instead of the previewed one.</summary>
    /// <param name="monitor">null = the previewed monitor (<see cref="AutomaticPlacement"/>).</param>
    private string SummaryOn(MonitorBounds? monitor)
    {
        var automatic = monitor is { } other && other != _monitor
            ? PlacementCalculator.CalculateAutomatic(other, SelectedAspectRatio.Ratio, KeepAspectRatio)
            : AutomaticPlacement;

        var placement = Choice switch
        {
            PlacementChoice.UseDefault => null,
            PlacementChoice.Custom => string.Format(CultureInfo.CurrentCulture, Formats.Placement_Summary_Custom, Width, Height, PositionText),
            PlacementChoice.GameSize => string.Format(CultureInfo.CurrentCulture, Formats.Placement_Summary_GameSize, PositionText),
            _ when automatic is not null =>
                string.Format(CultureInfo.CurrentCulture, Formats.Placement_Summary_Automatic,
                    SelectedAspectRatio.ShortLabel, automatic.Width, automatic.Height, PositionText),
            _ => "",
        };

        if (placement is null)
        {
            return DefaultEffectiveSummary;   // already includes the default's display
        }

        return SelectedDisplay is { } display
            ? string.Format(CultureInfo.CurrentCulture, Formats.Placement_Summary_WithDisplay, placement, display.ShortLabel)
            : placement;
    }

    /// <summary>The position part of the summary, e.g. "left", "X 100" or "centered, top".</summary>
    private string PositionText
    {
        get
        {
            var horizontal = EffectiveHorizontal switch
            {
                HorizontalAnchor.Left => Strings.Placement_Position_Left,
                HorizontalAnchor.Right => Strings.Placement_Position_Right,
                HorizontalAnchor.Offset => Format(Formats.Placement_Position_Offset, OffsetX),
                _ => Strings.Placement_Position_Centered,
            };
            var vertical = Vertical switch
            {
                VerticalAnchor.Top => Strings.Placement_Position_Top,
                VerticalAnchor.Bottom => Strings.Placement_Position_Bottom,
                _ => null,
            };
            return vertical is null ? horizontal : $"{horizontal}, {vertical}";   // a list, like the rest of the summary
        }
    }

    /// <summary>The default's summary as it applies to this game: on the same monitor as the preview.</summary>
    private string DefaultEffectiveSummary => _defaultEditor?.SummaryOn(DefaultOnGameMonitor) ?? "";

    /// <summary>Shown under "Use default", e.g. "Currently: Automatic – 2560 × 1440 at X 1280, primary display".</summary>
    public string DefaultSummary =>
        _defaultEditor is null ? "" : string.Format(CultureInfo.CurrentCulture, Formats.Placement_CurrentDefault, DefaultEffectiveSummary);

    /// <summary>
    /// Called when the settings window opens, moves to another monitor, or the displays change.
    /// <paramref name="windowMonitor"/> is the settings window's monitor: the best guess for
    /// "the display the game is on", since the editor doesn't know where the game will be.
    /// </summary>
    public void SetDisplays(IReadOnlyList<DisplayInfo> displays, MonitorBounds windowMonitor)
    {
        _displays = displays;
        _windowMonitor = windowMonitor;

        // Keep the current choice (or the saved one on the first call) across rebuilds of the list
        var keep = SelectedDisplay is { } selected ? (selected.Target, selected.Id, selected.Name) : _savedDisplay;
        var options = BuildOptions(displays, keep);

        // Replacing the list makes the ComboBox clear its selection, then we select the matching new entry.
        // Neither is a choice by the user, so it must not reset the custom values (see OnSelectedDisplayChanged).
        _rebuildingOptions = true;
        try
        {
            DisplayOptions = options;
            SelectedDisplay = options.FirstOrDefault(o => o.Matches(keep.Target, keep.Id)) ?? options[0];
        }
        finally
        {
            _rebuildingOptions = false;
        }

        UpdateMonitor();
    }

    /// <summary>
    /// Whether a change of this property is an edit of what gets saved (see <see cref="ToSettings"/>). Everything else
    /// (preview, summary, errors, the display list) also changes by itself: the settings window moved to another
    /// monitor, a display was plugged in, the default changed. A display picked in the list counts, the list being
    /// rebuilt doesn't.
    /// </summary>
    public bool IsEdit(string? propertyName) => propertyName switch
    {
        nameof(Choice) or nameof(SelectedAspectRatio) or nameof(KeepAspectRatio) or nameof(WidthText) or nameof(HeightText)
            or nameof(OffsetXText) or nameof(Horizontal) or nameof(Vertical) => true,
        nameof(SelectedDisplay) => !_rebuildingOptions,
        _ => false,
    };

    public PlacementSettings? ToSettings()
    {
        if (Choice == PlacementChoice.UseDefault)
        {
            return null;
        }

        var (target, id, name) = SelectedDisplay is { } display ? (display.Target, display.Id, display.Name) : _savedDisplay;
        var isSpecific = target == DisplayTarget.Specific;

        // The name is only a label for when the display is unplugged. For the saved display it stays as saved: names
        // change by themselves (language, driver, "Display 2" after re-plugging), and that alone isn't an edit
        if (isSpecific && _savedDisplay.Target == DisplayTarget.Specific && _savedDisplay.Name is not null
            && string.Equals(id, _savedDisplay.Id, StringComparison.OrdinalIgnoreCase))
        {
            name = _savedDisplay.Name;
        }

        return new PlacementSettings
        {
            Mode = Choice switch
            {
                PlacementChoice.Custom => PlacementMode.Custom,
                PlacementChoice.GameSize => PlacementMode.GameSize,
                _ => PlacementMode.Automatic,
            },
            AspectRatio = SelectedAspectRatio.Ratio,
            KeepAspectRatio = KeepAspectRatio,
            Custom = Choice == PlacementChoice.Custom ? new WindowPlacement(Width, Height, OffsetX) : null,
            Horizontal = EffectiveHorizontal,
            Vertical = Vertical,
            Display = target,
            DisplayId = isSpecific ? id : null,
            DisplayName = isSpecific ? name : null,
        };
    }

    partial void OnChoiceChanged(PlacementChoice value)
    {
        OnSummaryChanged();
        OnValidationChanged();
        OnShowOnDisplayChanged();
    }

    // Typed text → the number (only if valid) and the field's error
    partial void OnWidthTextChanged(string value) => WidthInputError = ParseField(value, minimum: 1, v => Width = v);

    partial void OnHeightTextChanged(string value) => HeightInputError = ParseField(value, minimum: 1, v => Height = v);

    partial void OnOffsetXTextChanged(string value) => OffsetXInputError = ParseField(value, minimum: 0, v => OffsetX = v);

    partial void OnWidthInputErrorChanged(string? value) => OnValidationChanged();

    partial void OnHeightInputErrorChanged(string? value) => OnValidationChanged();

    partial void OnOffsetXInputErrorChanged(string? value) => OnValidationChanged();

    // Number set in code (loaded, or filled with the automatic values) → the field shows it
    partial void OnWidthChanged(int value)
    {
        WidthText = SyncText(WidthText, value);
        OnValidationChanged();
    }

    partial void OnHeightChanged(int value)
    {
        HeightText = SyncText(HeightText, value);
        OnValidationChanged();
    }

    partial void OnOffsetXChanged(int value)
    {
        OffsetXText = SyncText(OffsetXText, value);
        OnValidationChanged();
    }

    /// <summary>
    /// Everything that depends on the numbers, their input errors, the choice and the monitor. One place,
    /// because the fit check ties the fields together: a new width can move the error to the offset field.
    /// </summary>
    private void OnValidationChanged()
    {
        ShowOnDisplayCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(WidthError));
        OnPropertyChanged(nameof(HeightError));
        OnPropertyChanged(nameof(OffsetXError));
        OnPropertyChanged(nameof(FitWarning));
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(FirstInvalidField));
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(CanFitToDisplay));
        FitToDisplayCommand.NotifyCanExecuteChanged();
        ApplyPresetCommand.NotifyCanExecuteChanged();   // needs the monitor, which is set right before this
    }

    /// <summary>Hands a valid number to <paramref name="apply"/>; returns the field's error, or null.</summary>
    private static string? ParseField(string text, int minimum, Action<int> apply)
    {
        // long: "99999999999" is a number that's too big, not "not a number"
        if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var number))
        {
            return Strings.Placement_Error_NotANumber;
        }

        if (number < minimum || number > WindowPlacement.MaxCoordinate)
        {
            return string.Format(CultureInfo.CurrentCulture, Formats.Placement_Error_Range, minimum, WindowPlacement.MaxCoordinate);
        }

        apply((int)number);
        return null;
    }

    /// <summary>Keeps the text as typed while it already means this number (e.g. " 1920"), otherwise formats it.</summary>
    private static string SyncText(string text, int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var current) && current == value
            ? text
            : value.ToString(CultureInfo.CurrentCulture);

    /// <summary>Summary and preview describe the same thing, so they always change together.</summary>
    private void OnSummaryChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Preview));
    }

    partial void OnSelectedDisplayChanged(DisplayOption? oldValue, DisplayOption? newValue)
    {
        // The user picked another display. Custom values the user has seen stay as they are: the fit warning or the
        // field errors say if they don't fit, and "Fit to display" shrinks them. Only the untouched pre-fill of
        // hidden custom fields follows the display, so choosing Custom afterwards starts from this display's values.
        var chosenByUser = !_rebuildingOptions && oldValue is not null && newValue is not null;
        var refillPrefill = chosenByUser && Choice != PlacementChoice.Custom && IsUntouchedPrefill;
        UpdateMonitor();
        if (refillPrefill && AutomaticPlacement is { } automatic)
        {
            Prefill(automatic);
        }
    }

    /// <summary>The custom values pre-filled from the automatic result, or null once there are saved or typed ones.</summary>
    private WindowPlacement? _prefill;

    private bool IsUntouchedPrefill =>
        _prefill is { } prefill
        && WidthInputError is null && HeightInputError is null && OffsetXInputError is null
        && (Width, Height, OffsetX) == (prefill.Width, prefill.Height, prefill.OffsetX);

    /// <summary>
    /// Fills the custom fields with a starting point. Through the texts, not the numbers: that also clears the fields'
    /// errors. Setting only the numbers would leave an error (and its invalid text) in place wherever the number
    /// didn't change, e.g. a mistyped offset next to an automatic offset of 0.
    /// </summary>
    private void Prefill(WindowPlacement values)
    {
        WidthText = values.Width.ToString(CultureInfo.CurrentCulture);
        HeightText = values.Height.ToString(CultureInfo.CurrentCulture);
        OffsetXText = values.OffsetX.ToString(CultureInfo.CurrentCulture);
        _prefill = values;
    }

    /// <summary>
    /// Custom values that are too large for the display they're checked against (the fit warning for saved ones, the
    /// field errors for edited ones): "Fit to display" shrinks them, see <see cref="FitToDisplay"/>.
    /// </summary>
    public bool CanFitToDisplay => Choice == PlacementChoice.Custom && _fitMonitor is { } monitor && ExceedsMonitor(monitor);

    /// <summary>Shrinks the custom values to fit the display, as the watcher would: only what's too large changes.</summary>
    [RelayCommand(CanExecute = nameof(CanFitToDisplay))]
    private void FitToDisplay()
    {
        if (_fitMonitor is { } monitor)
        {
            var fitted = PlacementCalculator.FitToMonitor(new WindowPlacement(Width, Height, OffsetX), monitor);
            WidthText = fitted.Width.ToString(CultureInfo.CurrentCulture);
            HeightText = fitted.Height.ToString(CultureInfo.CurrentCulture);
            OffsetXText = fitted.OffsetX.ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Recalculates preview and warning for the selected display. Never changes custom values the user has seen.</summary>
    private void UpdateMonitor()
    {
        if (_windowMonitor is not { } windowMonitor || SelectedDisplay is not { } display)
        {
            return;
        }

        var selection = new PlacementSettings { Display = display.Target, DisplayId = display.Id };
        var (monitor, found) = PlacementCalculator.ResolveDisplay(selection, _displays, GameMonitor ?? windowMonitor);
        _monitor = monitor;

        // "The display the game is on": the settings window's monitor is only a guess, good for the preview but
        // not as a limit (moving this window to a smaller monitor would make saved values invalid). Values that
        // fit no connected display at all are still wrong; anything else the watcher fits to the game's monitor.
        _fitMonitor = display.Target == DisplayTarget.Current ? LargestDisplay(_displays, windowMonitor)
            : found ? monitor
            : null;

        var automatic = PlacementCalculator.CalculateAutomatic(monitor, SelectedAspectRatio.Ratio, KeepAspectRatio);
        AutomaticPlacement = automatic;
        OnValidationChanged();   // the monitor size may have changed without anything else changing

        // Without saved custom values, pre-fill the custom fields with the automatic result as a starting point.
        // Never just because the settings window moved to another monitor: that would silently change values.
        if (!_customValuesInitialized)
        {
            Prefill(automatic);
            _customValuesInitialized = true;
        }
    }

    /// <summary>The widest and the tallest connected display combined: the largest size any display allows.</summary>
    private static MonitorBounds LargestDisplay(IReadOnlyList<DisplayInfo> displays, MonitorBounds fallback) =>
        displays.Count == 0
            ? fallback
            : new MonitorBounds(0, 0, displays.Max(d => d.Bounds.Width), displays.Max(d => d.Bounds.Height));

    private static List<DisplayOption> BuildOptions(
        IReadOnlyList<DisplayInfo> displays, (DisplayTarget Target, string? Id, string? Name) keep)
    {
        var options = new List<DisplayOption>();

        var primaryLabel = displays.FirstOrDefault(d => d.IsPrimary) is { } primary
            ? string.Format(CultureInfo.CurrentCulture, Formats.Placement_Display_PrimaryOption,
                primary.Name, primary.Bounds.Width, primary.Bounds.Height)
            : Strings.Placement_Display_PrimaryShort;
        options.Add(new DisplayOption(DisplayTarget.Primary, null, null, primaryLabel, Strings.Placement_Display_PrimaryShort));

        options.Add(new DisplayOption(DisplayTarget.Current, null, null,
            Strings.Placement_Display_Current, Strings.Placement_Display_CurrentShort));

        foreach (var display in displays)
        {
            var label = string.Format(CultureInfo.CurrentCulture, Formats.Placement_Display_Option,
                display.Number, display.Name, display.Bounds.Width, display.Bounds.Height);
            options.Add(new DisplayOption(DisplayTarget.Specific, display.Id, display.Name, label, display.Name));
        }

        // A saved display that is unplugged right now stays selectable, so the setting isn't lost
        if (keep.Target == DisplayTarget.Specific && !options.Any(o => o.Matches(keep.Target, keep.Id)))
        {
            var name = keep.Name ?? keep.Id ?? "?";
            var label = string.Format(CultureInfo.CurrentCulture, Formats.Placement_Display_NotConnected, name);
            options.Add(new DisplayOption(DisplayTarget.Specific, keep.Id, keep.Name, label, label));
        }

        return options;
    }

    /// <summary>
    /// The game was removed from the list: stop following the default editor, which lives as long as the
    /// settings window and would otherwise keep this editor alive and updating.
    /// </summary>
    public void Detach()
    {
        if (_defaultEditor is not null)
        {
            _defaultEditor.PropertyChanged -= OnDefaultEditorChanged;
        }
    }

    private void OnDefaultEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // "Use default" follows the default's choice, also for "Show on display"
        if (e.PropertyName is nameof(Choice) or nameof(IsValid) && Choice == PlacementChoice.UseDefault)
        {
            OnShowOnDisplayChanged();
        }

        if (e.PropertyName is nameof(Summary) or nameof(Preview))
        {
            OnPropertyChanged(nameof(DefaultSummary));
            OnPropertyChanged(nameof(DefaultDisplayText));
            if (Choice == PlacementChoice.UseDefault)
            {
                OnSummaryChanged();
            }
        }
    }

    private static string Format(CompositeFormat format, int width, int height, int offsetX) =>
        string.Format(CultureInfo.CurrentCulture, format, width, height, offsetX);

    private static string Format(CompositeFormat format, int value) =>
        string.Format(CultureInfo.CurrentCulture, format, value);
}

/// <summary>A running game's window for "Show on display": the monitor it is on, and its inner size (null if unknown).</summary>
public sealed record RunningGameWindow(MonitorBounds Monitor, (int Width, int Height)? ClientSize);
