namespace ForceBorderlessWindowed.Models;

public enum PlacementMode
{
    /// <summary>16:9 at full monitor height, centered horizontally.</summary>
    Automatic,

    /// <summary>Fixed size and offset from <see cref="PlacementSettings.Custom"/>.</summary>
    Custom,

    /// <summary>
    /// The size the game gives its window (its inner area, without the frame), centered. Read on every check, so a
    /// resolution picked in the game's own settings simply moves the window to the new centered spot.
    /// </summary>
    GameSize,
}

/// <summary>Shape of the window for <see cref="PlacementMode.Automatic"/>.</summary>
public enum AspectRatio
{
    /// <summary>16:9, the default (also for settings files from older versions).</summary>
    Widescreen16x9,

    /// <summary>4:3, for older games that don't support widescreen.</summary>
    Standard4x3,

    /// <summary>5:4, e.g. games made for 1280 × 1024.</summary>
    Standard5x4,

    // Added later: at the end, so the values above keep their numbers

    /// <summary>16:10, e.g. 1920 × 1200 or 2560 × 1600.</summary>
    Widescreen16x10,

    /// <summary>"21:9" ultrawide, calculated as 43:18 (3440 × 1440, the most common ultrawide size).</summary>
    Ultrawide21x9,
}

/// <summary>Which display a game window is placed on.</summary>
public enum DisplayTarget
{
    /// <summary>The Windows primary display (default; also for settings files from older versions).</summary>
    Primary,

    /// <summary>Whatever display the game window is currently on.</summary>
    Current,

    /// <summary>A specific display, see <see cref="PlacementSettings.DisplayId"/>.</summary>
    Specific,
}

/// <summary>Where the window sits horizontally on its display. Follows the display: no pixel value is saved for it.</summary>
public enum HorizontalAnchor
{
    Center,
    Left,
    Right,

    /// <summary>At <see cref="WindowPlacement.OffsetX"/> from the left edge; only for <see cref="PlacementMode.Custom"/>.</summary>
    Offset,
}

/// <summary>Where the window sits vertically on its display.</summary>
public enum VerticalAnchor
{
    /// <summary>The default (also for settings files from older versions).</summary>
    Center,
    Top,
    Bottom,
}

public sealed record PlacementSettings
{
    public PlacementMode Mode { get; init; } = PlacementMode.Automatic;

    /// <summary>Only used when <see cref="Mode"/> is <see cref="PlacementMode.Automatic"/>.</summary>
    public AspectRatio AspectRatio { get; init; }

    /// <summary>
    /// For <see cref="PlacementMode.Automatic"/> on a display that is narrower than the aspect ratio (e.g. 16:9 on
    /// 16:10): keep the ratio at full width (by default centered vertically, "bars top and bottom"), instead of filling the
    /// whole display. Off by default (and in settings of older versions): the whole display, as before.
    /// </summary>
    public bool KeepAspectRatio { get; init; }

    /// <summary>Only used when <see cref="Mode"/> is <see cref="PlacementMode.Custom"/>.</summary>
    public WindowPlacement? Custom { get; init; }

    /// <summary>
    /// Horizontal position, for every mode. null in files of older versions, which had no choice: Custom used its
    /// offset, the others were centered (see <see cref="EffectiveHorizontal"/>).
    /// </summary>
    public HorizontalAnchor? Horizontal { get; init; }

    /// <summary>Vertical position, for every mode. Centered by default, as in older versions.</summary>
    public VerticalAnchor Vertical { get; init; }

    /// <summary>
    /// The horizontal position that applies: <see cref="Horizontal"/>, or what older versions did without it.
    /// <see cref="HorizontalAnchor.Offset"/> only exists for Custom (the other modes have no offset): centered there.
    /// A method, not a property: the JSON serializer would otherwise write it into settings.json.
    /// </summary>
    public HorizontalAnchor EffectiveHorizontal() => Horizontal switch
    {
        null => Mode == PlacementMode.Custom ? HorizontalAnchor.Offset : HorizontalAnchor.Center,
        HorizontalAnchor.Offset when Mode != PlacementMode.Custom => HorizontalAnchor.Center,
        { } anchor => anchor,
    };

    public DisplayTarget Display { get; init; }

    /// <summary>Only for <see cref="DisplayTarget.Specific"/>: the stable device path of the monitor.</summary>
    public string? DisplayId { get; init; }

    /// <summary>
    /// Only for <see cref="DisplayTarget.Specific"/>: the monitor's name when it was selected,
    /// so the settings can still show "DELL U3423WE (not connected)" while it's unplugged.
    /// </summary>
    public string? DisplayName { get; init; }
}

/// <summary>
/// Size and horizontal offset of a game window in physical pixels, as saved for Custom.
/// The offset is relative to the left edge of the monitor and only used with <see cref="HorizontalAnchor.Offset"/>;
/// where the window really goes is a <see cref="WindowRect"/> (see Services.PlacementCalculator.Resolve).
/// </summary>
public sealed record WindowPlacement(int Width, int Height, int OffsetX)
{
    /// <summary>
    /// The rule for a usable custom placement, shared by the editor and the check of loaded settings.
    /// A method, not a property: the JSON serializer would otherwise write it into settings.json.
    /// Upper limit: Win32 window coordinates only work up to about 32767; larger values could even overflow
    /// when the offset is added to the monitor position.
    /// </summary>
    public static bool IsValidSize(int width, int height, int offsetX) =>
        width is > 0 and <= MaxCoordinate && height is > 0 and <= MaxCoordinate && offsetX is >= 0 and <= MaxCoordinate;

    public const int MaxCoordinate = 32767;
}

/// <summary>
/// Where a game window goes: its size and its position relative to the top left corner of its monitor, in physical
/// pixels. The result of a placement on one monitor (see Services.PlacementCalculator.Resolve), never saved.
/// </summary>
public sealed record WindowRect(int Width, int Height, int OffsetX, int OffsetY);
