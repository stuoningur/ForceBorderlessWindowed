using System.Windows.Media;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.Tests.Fakes;

/// <summary>No global hotkeys: every combination is free, suspending does nothing.</summary>
internal sealed class NoHotkeys : IHotkeyService
{
    public IReadOnlySet<HotkeyAction> FailedHotkeys { get; } = new HashSet<HotkeyAction>();

    public event EventHandler? FailedHotkeysChanged { add { } remove { } }

    public IDisposable Suspend() => new System.IO.MemoryStream();   // any IDisposable

    public bool IsAvailable(Hotkey hotkey) => true;

    public void RetryFailed()
    {
    }
}

/// <summary>A watcher that has found no game windows.</summary>
internal sealed class NoStatuses : IGameStatusProvider
{
    public IReadOnlyDictionary<Guid, GameWindowStatus> GetStatuses() => new Dictionary<Guid, GameWindowStatus>();

    public event EventHandler? StatusesChanged { add { } remove { } }
}

/// <summary>No program icons.</summary>
internal sealed class NoIcons : IGameIconService
{
    public ImageSource? GetIcon(string executableName) => null;

    public ImageSource? Remember(string executableName, string executablePath) => null;

    public ImageSource? GetIconForFile(string path) => null;
}

/// <summary>The settings in memory: Save swaps them and raises SettingsChanged, like the real service.</summary>
internal sealed class FakeSettingsService(AppSettings initial) : ISettingsService
{
    public AppSettings Current { get; private set; } = initial;

    public SettingsLoadProblem? LoadProblem => null;

    public string? ActiveLanguage => Current.Language;

    public string SettingsFolder => @"C:\Settings";

    public void Save(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? SettingsChanged;
}

/// <summary>Autostart off, and every change works.</summary>
internal sealed class FakeAutostart : IAutostartService
{
    private AutostartMode _mode = AutostartMode.Off;

    public bool CanStartElevated => false;

    public Task<AutostartMode> GetModeAsync() => Task.FromResult(_mode);

    public Task<AutostartChangeResult> SetModeAsync(AutostartMode mode)
    {
        _mode = mode;
        return Task.FromResult(AutostartChangeResult.Changed);
    }

    public Task<bool> IsElevatedCopyOutdatedAsync() => Task.FromResult(false);

    public Task<AutostartChangeResult> UpdateElevatedCopyAsync() => Task.FromResult(AutostartChangeResult.Changed);
}

/// <summary>An app shell that does nothing (no windows, no theme switching).</summary>
internal sealed class FakeShell : IAppShell
{
    public bool IsShuttingDown => false;

    public void ShowSettingsWindow()
    {
    }

    public void ShowGameSettings(Guid gameId)
    {
    }

    public void ExitApplication()
    {
    }

    public void RestartApplication(bool asAdministrator = false)
    {
    }

    public void OpenLogFolder()
    {
    }

    public void OpenSettingsFolder()
    {
    }

    public void ApplyTheme(AppTheme theme)
    {
    }
}

/// <summary>"Show on display" without drawing anything.</summary>
internal sealed class NoScreenPreview : IScreenPreview
{
    public void Show(MonitorBounds monitor, WindowRect placement, string label)
    {
    }
}
