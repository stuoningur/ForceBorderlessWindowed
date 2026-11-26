using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>The watcher's latest results, for showing them in the UI.</summary>
public interface IGameStatusProvider
{
    /// <summary>
    /// Status per game (<see cref="Models.GameProfile.Id"/>), only for games with a window the watcher has seen.
    /// Safe to call from any thread: returns a snapshot that is never modified afterwards.
    /// </summary>
    IReadOnlyDictionary<Guid, GameWindowStatus> GetStatuses();

    /// <summary>
    /// Raised when a status changed (not on every check). Raised on the watcher thread:
    /// UI subscribers must move to their own thread.
    /// </summary>
    event EventHandler? StatusesChanged;
}
