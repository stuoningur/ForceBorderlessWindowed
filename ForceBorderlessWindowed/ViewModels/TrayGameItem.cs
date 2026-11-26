using System.Globalization;
using System.Windows.Media;

using ForceBorderlessWindowed.Resources;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>The small badge on a game's icon in the tray menu. Only for running games.</summary>
public enum TrayBadge
{
    /// <summary>Not running.</summary>
    None,

    /// <summary>Running, nothing to report: waiting, no frame of its own, turned off.</summary>
    Neutral,

    /// <summary>Running while detection is paused.</summary>
    Paused,

    Success,
    Warning,
    Error,
}

/// <summary>
/// One game in the tray's "Games" submenu: icon with a status badge, name, a short status at the right, and a
/// checkmark that turns it on or off right away (saved like the Pause item). A placeholder says there are no games.
/// </summary>
public sealed class TrayGameItem
{
    private readonly Action<TrayGameItem, bool>? _toggle;

    internal TrayGameItem(
        Guid id, string title, string? titleFilter, ImageSource? icon, string? statusText, TrayBadge badge, bool enabled,
        Action<TrayGameItem, bool> toggle)
    {
        Id = id;
        Title = title;
        TitleFilter = titleFilter;
        Icon = icon;
        StatusText = statusText;
        Badge = badge;
        _enabled = enabled;
        IsOn = enabled;
        _toggle = toggle;
    }

    private TrayGameItem(string title)
    {
        Title = title;
        IsPlaceholder = true;
    }

    /// <summary>"No games yet": disabled, no checkmark.</summary>
    internal static TrayGameItem Placeholder(string title) => new(title);

    public Guid Id { get; }

    public string Title { get; }

    /// <summary>
    /// The game's "window title contains", null = none. Shown after the name: two games with the same exe and name
    /// would otherwise look the same in the menu.
    /// </summary>
    public string? TitleFilter { get; }

    /// <summary>The filter in quotes, as in the Games list; null = none.</summary>
    public string? TitleFilterText => TitleFilter is null
        ? null
        : string.Format(CultureInfo.CurrentCulture, Formats.Games_ListTitleFilter, TitleFilter);

    /// <summary>
    /// What a screen reader says for the item, e.g. "StarCraft II, No frame". Without it, it read the class name.
    /// The checkmark is announced by the menu item itself.
    /// </summary>
    public string AccessibleName => string.Join(", ", new[]
    {
        Title,
        TitleFilter is null ? null : string.Format(CultureInfo.CurrentCulture, Formats.Games_Accessible_TitleFilter, TitleFilter),
        StatusText,
    }.Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>The program's icon; null = the generic game glyph (not seen this session).</summary>
    public ImageSource? Icon { get; }

    /// <summary>Shown at the right of the item, like a shortcut, e.g. "Borderless", "Off"; null = nothing to say.</summary>
    public string? StatusText { get; }

    public TrayBadge Badge { get; }

    public bool IsPlaceholder { get; }

    /// <summary>As saved when the menu was built: the icon of a game that is off is faded.</summary>
    public bool IsOn { get; }

    private bool _enabled;

    /// <summary>The checkmark. Set by the menu: saves right away (see TrayIconViewModel).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled != value)
            {
                _enabled = value;
                _toggle?.Invoke(this, value);
            }
        }
    }
}
