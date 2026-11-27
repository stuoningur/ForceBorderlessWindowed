namespace ForceBorderlessWindowed.Models;

/// <summary>A running process that has at least one window the user can see.</summary>
/// <param name="WindowHandle">The window it was found by (its main window); 0 if unknown.</param>
public sealed record RunningProgram(int ProcessId, string ExecutableName, string WindowTitle, string ExecutablePath, nint WindowHandle = 0);
