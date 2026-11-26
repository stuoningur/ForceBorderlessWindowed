namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Which of a game process's windows is its main window: the largest one. Shared by the real window search and
/// the tests' fake desktop, so both follow the same rule.
/// </summary>
internal static class MainWindowRanking
{
    /// <summary>
    /// The area a window takes part with. A window we resized counts with its size from before our change only,
    /// never with the size we gave it: an enlarged launcher would otherwise keep beating the game's real window, and
    /// a window made smaller would hand over to a second window, which would get the same size, and the two would
    /// take turns forever (each handover restores one and changes the other).
    /// </summary>
    public static (long Area, bool Resized) Measure(nint handle, long currentArea, IReadOnlyDictionary<nint, long>? originalAreas) =>
        originalAreas is not null && originalAreas.TryGetValue(handle, out var originalArea)
            ? (originalArea, true)
            : (currentArea, false);

    /// <summary>True if the candidate takes the place of the best window so far: larger, or as large and resized by us.</summary>
    public static bool Beats((long Area, bool Resized) candidate, (long Area, bool Resized) best) =>
        candidate.Area > best.Area || (candidate.Area == best.Area && candidate.Resized && !best.Resized);
}
