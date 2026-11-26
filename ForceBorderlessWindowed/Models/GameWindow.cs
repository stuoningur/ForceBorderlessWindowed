namespace ForceBorderlessWindowed.Models;

/// <summary>The main window of a running, configured game.</summary>
/// <param name="ProfileId">The game profile it belongs to (<see cref="GameProfile.Id"/>), see GameMatcher.</param>
public sealed record GameWindow(nint Handle, int ProcessId, string ExecutableName, Guid ProfileId);
