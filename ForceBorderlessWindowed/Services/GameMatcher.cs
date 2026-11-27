using System.IO;

using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Which game profile a program's window belongs to. The exe name has to match; on top of that a profile can ask for
/// the exact exe file (<see cref="GameProfile.MatchExecutablePath"/>) and for a text in the window title
/// (<see cref="GameProfile.WindowTitleContains"/>). Several profiles may share an exe name as long as these differ
/// (e.g. two Java games, both javaw.exe); the most specific matching profile wins. One rule for the watcher, the
/// "add the game in the foreground" hotkey, the Add menu and the duplicate check of the Games tab.
/// </summary>
internal static class GameMatcher
{
    /// <summary>
    /// Whether a profile with these filters takes a program with this exe, path and window title.
    /// </summary>
    /// <param name="requiredPath">The exe path the profile insists on, or null for any.</param>
    /// <param name="titleContains">The text the window title must contain, or null/empty for any.</param>
    /// <param name="programTitle">The window's title; null if unknown, which a title filter never matches.</param>
    public static bool Matches(
        string executableName, string? requiredPath, string? titleContains,
        string programExe, string? programPath, string? programTitle)
    {
        if (!string.Equals(executableName.Trim(), programExe, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (requiredPath is not null && !SamePath(requiredPath, programPath))
        {
            return false;
        }

        var title = NormalizeTitle(titleContains);
        return title is null || (programTitle?.Contains(title, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public static bool Matches(GameProfile game, string programExe, string? programPath, string? programTitle) =>
        Matches(game.ExecutableName, RequiredPath(game), game.WindowTitleContains, programExe, programPath, programTitle);

    /// <summary>
    /// The most specific profile that takes the program, or null. An exact exe file counts more than a window title,
    /// and both more than the exe name alone. Among equally specific ones the longer title filter wins, and after that
    /// the lower <see cref="GameProfile.Id"/>: never the list position, which changes with a rename (the list is A–Z).
    /// </summary>
    /// <param name="programTitle">
    /// Read only when a candidate has a title filter: asking a window for its title costs a little.
    /// </param>
    public static GameProfile? FindBest(
        IEnumerable<GameProfile> games, string programExe, string? programPath, Func<string?> programTitle)
    {
        GameProfile? best = null;
        string? title = null;
        var titleRead = false;

        foreach (var game in games)
        {
            if (NormalizeTitle(game.WindowTitleContains) is not null && !titleRead)
            {
                title = programTitle();
                titleRead = true;
            }

            if ((best is null || Beats(game, best)) && Matches(game, programExe, programPath, title))
            {
                best = game;
            }
        }

        return best;
    }

    /// <summary>Whether <paramref name="game"/> wins over <paramref name="other"/> when both take a window, see FindBest.</summary>
    private static bool Beats(GameProfile game, GameProfile other)
    {
        var bySpecificity = Specificity(game).CompareTo(Specificity(other));
        if (bySpecificity != 0)
        {
            return bySpecificity > 0;
        }

        var byTitleLength = (NormalizeTitle(game.WindowTitleContains)?.Length ?? 0)
            .CompareTo(NormalizeTitle(other.WindowTitleContains)?.Length ?? 0);
        return byTitleLength != 0 ? byTitleLength > 0 : game.Id.CompareTo(other.Id) < 0;
    }

    /// <summary>The path a profile insists on: only with the box ticked and a path that belongs to the exe name.</summary>
    public static string? RequiredPath(GameProfile game) =>
        game.MatchExecutablePath ? GameProfile.PathIfItMatches(game.ExecutablePath, game.ExecutableName) : null;

    private static int Specificity(GameProfile game) =>
        (RequiredPath(game) is not null ? 2 : 0) + (NormalizeTitle(game.WindowTitleContains) is not null ? 1 : 0);

    /// <summary>
    /// What makes two profiles the same game: they would take exactly the same windows, so only the first one would
    /// ever be used. Compare with <see cref="StringComparer.OrdinalIgnoreCase"/>-like rules: the parts are lowercased.
    /// </summary>
    public static (string Exe, string? Path, string? Title) Key(string executableName, string? requiredPath, string? titleContains) =>
        (executableName.Trim().ToUpperInvariant(),
         requiredPath is null ? null : NormalizePath(requiredPath).ToUpperInvariant(),
         NormalizeTitle(titleContains)?.ToUpperInvariant());

    public static (string Exe, string? Path, string? Title) Key(GameProfile game) =>
        Key(game.ExecutableName, RequiredPath(game), game.WindowTitleContains);

    /// <summary>Trimmed, or null when empty: an empty filter means "any title".</summary>
    public static string? NormalizeTitle(string? title) => string.IsNullOrWhiteSpace(title) ? null : title.Trim();

    private static bool SamePath(string a, string? b) =>
        b is not null && string.Equals(NormalizePath(a), NormalizePath(b), StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
