using System.Text.Json.Serialization;

namespace ForceBorderlessWindowed.Models;

public sealed record GameProfile
{
    /// <summary>
    /// Identifies the game while the app runs and in settings.json: several games may share an exe name (see
    /// <see cref="MatchExecutablePath"/> and <see cref="WindowTitleContains"/>), so the name can't. Missing in files
    /// of older versions: SettingsService.Repair gives those games one.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>File name of the game's exe, e.g. "eldenring.exe". Used to match running processes.</summary>
    /// <remarks>
    /// Not "required": the JSON reader would then reject the whole settings file over one entry without it.
    /// Empty instead, and SettingsService.Repair removes such entries.
    /// </remarks>
    public string ExecutableName { get; init; } = "";

    /// <summary>Friendly name shown in the list, taken from the window title when the game is added.</summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Full path of the exe, only for showing its icon before the game runs. Never used to match windows (that's
    /// <see cref="ExecutableName"/>), so a game that moved still works. Known when the game was added from the
    /// running programs, with a file dialog or the hotkey; null for games typed in by hand or added by older versions.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// A new game for a running program, as the Add menu and the "add foreground game" hotkey create it.
    /// The name from the exe file first: window titles can contain private things (chat channels, document
    /// names, browser tabs). Only if the file has none, the title.
    /// </summary>
    /// <param name="friendlyName">The name from the exe's version info (GameNames.GetFriendlyName), or null.</param>
    internal static GameProfile FromRunningProgram(RunningProgram program, string? friendlyName) => new()
    {
        ExecutableName = program.ExecutableName,
        DisplayName = friendlyName ?? program.WindowTitle,
        ExecutablePath = program.ExecutablePath,
    };

    /// <summary>
    /// <paramref name="path"/> if it is a full path to the exe called <paramref name="executableName"/>, otherwise null.
    /// Used when saving (the exe name was typed over, so the old path belongs to another program) and when loading
    /// (hand-edited settings). Static, so the JSON serializer doesn't write it into settings.json.
    /// </summary>
    public static string? PathIfItMatches(string? path, string executableName) =>
        !string.IsNullOrWhiteSpace(path)
        && System.IO.Path.IsPathFullyQualified(path)
        && string.Equals(System.IO.Path.GetFileName(path), executableName.Trim(), StringComparison.OrdinalIgnoreCase)
            ? path
            : null;

    /// <summary>
    /// Only the program at <see cref="ExecutablePath"/>, not every program with that exe name. Ignored without a path
    /// (games typed in by hand), see GameMatcher.RequiredPath.
    /// </summary>
    public bool MatchExecutablePath { get; init; }

    /// <summary>
    /// Only windows whose title contains this text (case-insensitive); null = any window of the program. For programs
    /// that share one exe, such as Java games (javaw.exe).
    /// </summary>
    public string? WindowTitleContains { get; init; }

    [JsonIgnore]
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// How <see cref="Enabled"/> is stored. The source-generated JSON reader sets every init property, using the
    /// type's default for keys missing in the file: a plain bool would turn a missing "enabled" into false and
    /// silently disable the game. Missing or null means the default (true) instead; false stays false.
    /// </summary>
    [JsonInclude]
    [JsonPropertyName("enabled")]
    internal bool? EnabledForJson
    {
        get => Enabled;
        init => Enabled = value ?? true;
    }

    /// <summary>Confine the mouse to the game window while the game is in the foreground.</summary>
    public bool LockCursor { get; init; }

    /// <summary>Hide the taskbar on the game's display while the game is in the foreground.</summary>
    public bool HideTaskbar { get; init; }

    /// <summary>Cover the rest of the game's display in black while the game is in the foreground.</summary>
    public bool BlackBackground { get; init; }

    /// <summary>null = use <see cref="AppSettings.DefaultPlacement"/>.</summary>
    public PlacementSettings? Placement { get; init; }
}
