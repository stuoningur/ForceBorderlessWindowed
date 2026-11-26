using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

public interface IProcessDiscoveryService
{
    /// <summary>
    /// Programs with a window that shows up in the taskbar / Alt+Tab, one entry per process, sorted by title.
    /// Excludes this app and everything installed in the Windows folder. Never throws.
    /// </summary>
    IReadOnlyList<RunningProgram> GetRunningPrograms();

    /// <summary>
    /// The main window of every running process that one of <paramref name="games"/> takes (see GameMatcher): its
    /// largest visible top-level window that the game's filters take. One entry per process. Never throws.
    /// </summary>
    /// <param name="originalAreas">
    /// Windows this app has resized, with their area before that. Such a window counts with that area only, and wins
    /// a tie: the size we gave it must not decide whether it stays the main window (see MainWindowRanking).
    /// </param>
    IReadOnlyList<GameWindow> FindGameWindows(
        IReadOnlyCollection<GameProfile> games, IReadOnlyDictionary<nint, long>? originalAreas = null);
}
