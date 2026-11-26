using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Application-level actions that ViewModels may trigger.
/// The methods can be called from any thread; the work always runs on the UI thread.
/// </summary>
public interface IAppShell
{
    /// <summary>True once Exit or Restart has been requested.</summary>
    bool IsShuttingDown { get; }

    void ShowSettingsWindow();

    /// <summary>Opens the settings window on the Games tab with this game selected (e.g. its problem from the tray).</summary>
    void ShowGameSettings(Guid gameId);
    void ExitApplication();
    /// <param name="asAdministrator">Start the new instance elevated (shows the UAC prompt).</param>
    void RestartApplication(bool asAdministrator = false);
    void OpenLogFolder();
    void OpenSettingsFolder();

    /// <summary>Switches all windows and menus to the theme, without a restart.</summary>
    void ApplyTheme(AppTheme theme);
}
