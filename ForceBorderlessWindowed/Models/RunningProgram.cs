namespace ForceBorderlessWindowed.Models;

/// <summary>A running process that has at least one window the user can see.</summary>
public sealed record RunningProgram(int ProcessId, string ExecutableName, string WindowTitle, string ExecutablePath);
