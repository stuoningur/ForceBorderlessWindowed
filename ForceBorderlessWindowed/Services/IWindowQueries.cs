using Windows.Win32;
using Windows.Win32.Foundation;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The read-only window and process questions <see cref="WindowTracker"/> asks. An interface so the tracker's rules
/// can be tested against a fake desktop; the app uses <see cref="WindowQueryService"/>, which simply calls
/// <see cref="WindowQueries"/>.
/// </summary>
internal interface IWindowQueries
{
    /// <summary>The process that owns the window, 0 if the window is gone.</summary>
    uint GetProcessId(nint windowHandle);

    /// <summary>See <see cref="WindowQueries.GetProcessStartTime"/>.</summary>
    long? GetProcessStartTime(uint processId);

    /// <summary>Full exe path, or null if the process is gone or protected.</summary>
    string? GetExecutablePath(uint processId);

    /// <summary>See <see cref="WindowQueries.IsMainWindowCandidate"/>.</summary>
    bool IsMainWindowCandidate(nint windowHandle, bool requireTitle);

    /// <summary>See <see cref="WindowQueries.GetWindowArea"/>.</summary>
    long GetWindowArea(nint windowHandle);

    /// <summary>True for top-level windows, false for child windows.</summary>
    bool IsTopLevel(nint windowHandle);

    bool IsMinimized(nint windowHandle);

    /// <summary>Windows considers the window "not responding".</summary>
    bool IsHung(nint windowHandle);

    /// <summary>The window's title. Doesn't wait for the window's program (Windows keeps titles itself).</summary>
    string GetWindowTitle(nint windowHandle);
}

internal sealed class WindowQueryService : IWindowQueries
{
    public uint GetProcessId(nint windowHandle) => WindowQueries.GetProcessId(new HWND(windowHandle));

    public long? GetProcessStartTime(uint processId) => WindowQueries.GetProcessStartTime(processId);

    public string? GetExecutablePath(uint processId) => WindowQueries.GetExecutablePath(processId);

    public bool IsMainWindowCandidate(nint windowHandle, bool requireTitle) =>
        WindowQueries.IsMainWindowCandidate(new HWND(windowHandle), requireTitle);

    public long GetWindowArea(nint windowHandle) => WindowQueries.GetWindowArea(new HWND(windowHandle));

    public bool IsTopLevel(nint windowHandle) => WindowQueries.IsTopLevel(new HWND(windowHandle));

    public bool IsMinimized(nint windowHandle) => PInvoke.IsIconic(new HWND(windowHandle));

    public bool IsHung(nint windowHandle) => PInvoke.IsHungAppWindow(new HWND(windowHandle));

    public string GetWindowTitle(nint windowHandle) => WindowQueries.GetWindowTitle(new HWND(windowHandle));
}
