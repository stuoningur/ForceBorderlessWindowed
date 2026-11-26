namespace ForceBorderlessWindowed.Models;

public sealed record AppSettings
{
    /// <summary>Culture name such as "de" or "en". null = follow the Windows display language.</summary>
    public string? Language { get; init; }

    /// <summary>Missing in older settings files: the enum default (System) is the right fallback there.</summary>
    public AppTheme Theme { get; init; }

    /// <summary>Global switch from the tray menu: while paused, no game windows are changed.</summary>
    public bool IsPaused { get; init; }

    // The source-generated JSON deserializer sets every init property, using null for keys missing
    // in the file (e.g. a settings.json from an older version). That would overwrite the "= ..." defaults,
    // so the setters turn null back into the default.

    /// <summary>Placement used by every game that doesn't have its own.</summary>
    public PlacementSettings DefaultPlacement { get; init => field = value ?? new(); } = new();

    public IReadOnlyList<GameProfile> Games { get; init => field = value ?? []; } = [];

    public HotkeySettings Hotkeys { get; init => field = value ?? new(); } = new();
}
