using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;
using ForceBorderlessWindowed.ViewModels;
using ForceBorderlessWindowed.Views;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace ForceBorderlessWindowed;

public partial class App : Application, IAppShell
{
    private readonly IHost _host;
    private SettingsWindow? _settingsWindow;
    private TrayIconView? _trayIcon;
    private readonly ILogger<App> _logger;
    private readonly AppTheme _initialTheme;

    public App()
    {
        // Empty: the default builder also reads appsettings*.json, environment variables and the command line,
        // and watches the folder for changes. We use none of it, and an instance running as administrator
        // shouldn't take configuration from a folder the user (or any of their programs) can write to.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddSingleton<ISettingsService, SettingsService>();
        builder.Services.AddSingleton<IAutostartService, AutostartService>();
        builder.Services.AddSingleton<IDisplayService, DisplayService>();
        builder.Services.AddSingleton<IProcessDiscoveryService, ProcessDiscoveryService>();
        builder.Services.AddSingleton<IWindowManipulator, WindowManipulator>();
        builder.Services.AddSingleton<IGameIconService, GameIconService>();
        builder.Services.AddSingleton<IWindowPlacementStore, WindowPlacementStore>();
        builder.Services.AddSingleton<HotkeyService>();
        builder.Services.AddSingleton<IHotkeyService>(sp => sp.GetRequiredService<HotkeyService>());
        // One instance in two roles: the host starts/stops it, the UI reads its statuses
        builder.Services.AddSingleton<IChangedWindowsStore, ChangedWindowsStore>();
        builder.Services.AddSingleton<IWindowQueries, WindowQueryService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WindowTracker>();
        builder.Services.AddSingleton<GameWindowWatcher>();
        builder.Services.AddSingleton<IGameStatusProvider>(sp => sp.GetRequiredService<GameWindowWatcher>());
        builder.Services.AddSingleton<IOneOffWindows>(sp => sp.GetRequiredService<GameWindowWatcher>());
        builder.Services.AddSingleton<IScreenPreview, ScreenPreviewOverlay>();
        builder.Services.AddSingleton<IGameOverlay, GameOverlay>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<GameWindowWatcher>());
        builder.Services.AddSingleton<IAppShell>(this);
        builder.Services.AddSingleton<TrayIconView>();
        builder.Services.AddSingleton<TrayIconViewModel>();
        // Windows: transient, because a closed WPF window can't be shown again
        builder.Services.AddTransient<SettingsWindow>();
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddAppLogging();

        _host = builder.Build();
        _logger = _host.Services.GetRequiredService<ILogger<App>>();
        RegisterExceptionHandlers();
        var settings = _host.Services.GetRequiredService<ISettingsService>().Current;
        ApplyCulture(settings.Language);
        _initialTheme = settings.Theme;
    }

    private static void ApplyCulture(string? cultureName)
    {
        if (!string.IsNullOrEmpty(cultureName))
        {
            try
            {
                var uiCulture = CultureInfo.GetCultureInfo(cultureName);
                CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
                CultureInfo.CurrentUICulture = uiCulture;
            }
            catch (CultureNotFoundException)
            {
                // Invalid value in settings.json: fall back to the Windows language
            }
        }

        // WPF ignores CurrentCulture when formatting bindings and defaults to en-US.
        // This makes WPF use the user's regional format settings.
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(
                XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Here and not in the constructor: InitializeComponent (called after the constructor)
        // would overwrite it with ThemeMode="System" from App.xaml
        ApplyThemeCore(_initialTheme);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        try
        {
            LogStarting(AppInfo.DisplayVersion, Environment.OSVersion.Version, Environment.IsPrivilegedProcess,
                StartedAtSignIn, CultureInfo.CurrentUICulture.Name);
            if (ReplacedVersion is { } replaced)
            {
                LogReplacedOlderVersion(replaced);
            }

            await _host.StartAsync();

            // Created now on purpose: it starts checking for the elevated task (schtasks.exe) in the background,
            // so opening the settings window doesn't have to wait for that
            _host.Services.GetRequiredService<IAutostartService>();

            _trayIcon = _host.Services.GetRequiredService<TrayIconView>();
            _trayIcon.ForceCreate(enablesEfficiencyMode: false);

            // Here, not as a hosted service: its hidden window must be created on the UI thread.
            // Disposed (hotkeys unregistered) by the DI container when the host is disposed in OnExit.
            _host.Services.GetRequiredService<HotkeyService>().Start();

            // Started by the user (not at sign-in): they want to see the app, not just a new tray icon
            if (!StartedAtSignIn)
            {
                ShowSettingsWindow();
            }
        }
        catch (Exception ex)
        {
            LogStartupFailed(ex);
            ShowErrorMessage(ex);
            Shutdown(1);
        }
    }

    // IAppShell: every action is queued on the UI thread instead of running directly (see RunOnUiThread)
    public void ShowSettingsWindow() => RunOnUiThread(ShowSettingsWindowCore);

    public void ShowGameSettings(Guid gameId) => RunOnUiThread(() =>
    {
        ShowSettingsWindowCore();
        _settingsWindow?.ShowGame(gameId);
    });

    public void ExitApplication() => RunOnUiThread(async () => await RequestExitAsync());

    /// <summary>
    /// A newer version was started and asks this one to exit (see SingleInstance). Same as Exit in the tray menu,
    /// so unsaved changes are asked about first. If the user cancels, <paramref name="onDeclined"/> tells the newer
    /// version to give up, and this one keeps running.
    /// </summary>
    public void ExitForNewerVersion(Action onDeclined) => RunOnUiThread(async () =>
    {
        LogExitForNewerVersion();
        if (!await RequestExitAsync())
        {
            LogExitForNewerVersionDeclined();
            onDeclined();
        }
    });

    /// <summary>While Exit waits for the "Save your changes?" answer: a second request gets the same answer.</summary>
    private Task<bool>? _pendingExit;

    /// <summary>
    /// Like closing the settings window, Exit asks about unsaved changes (Save / Don't save / Cancel) and only
    /// quits if they're saved or discarded. Signing out of Windows still quits without asking: that can't wait.
    /// </summary>
    /// <returns>True if the app is exiting; false if it keeps running (cancelled).</returns>
    private Task<bool> RequestExitAsync()
    {
        if (_pendingExit is { IsCompleted: false } pending)
        {
            return pending;
        }

        return _pendingExit = ConfirmAndExitAsync();
    }

    private async Task<bool> ConfirmAndExitAsync()
    {
        if (IsShuttingDown)
        {
            return true;
        }

        if (_settingsWindow is { } window && !await window.ConfirmExitAsync())
        {
            return false;
        }

        IsShuttingDown = true;
        Shutdown();
        return true;
    }

    /// <summary>
    /// Signing out or shutting down Windows: WPF closes every window and exits right after this. Marked first, so
    /// the settings window closes without asking about unsaved changes. Windows doesn't wait for an answer, and
    /// a question left open could keep OnExit from putting the game windows back before the process is ended.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsShuttingDown = true;
        base.OnSessionEnding(e);
    }

    public void OpenLogFolder() => RunOnUiThread(() => OpenFolderCore(LoggingSetup.LogFolder));

    public void OpenSettingsFolder() => RunOnUiThread(() =>
        OpenFolderCore(_host.Services.GetRequiredService<ISettingsService>().SettingsFolder));

    public void ApplyTheme(AppTheme theme) => RunOnUiThread(() => ApplyThemeCore(theme));

    // Application.ThemeMode switches the Fluent theme of every window at runtime.
    // All our colors are DynamicResource brushes, so they follow along.
    private void ApplyThemeCore(AppTheme theme)
    {
        _currentTheme = theme;
        ThemeMode = theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        ApplyAppColors();
    }

    private AppTheme _currentTheme;
    private ResourceDictionary? _appColors;

    /// <summary>
    /// Our own few theme colors (Resources/AppColors.*.xaml) next to Fluent's: swaps in the file for the theme
    /// that is actually shown. For "System" that's the Windows app mode, and a contrast theme wins over all.
    /// </summary>
    private void ApplyAppColors()
    {
        var variant = SystemParameters.HighContrast ? "HighContrast"
            : _currentTheme switch
            {
                AppTheme.Light => "Light",
                AppTheme.Dark => "Dark",
                _ => IsWindowsInLightMode() ? "Light" : "Dark",
            };

        var colors = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Resources/AppColors.{variant}.xaml", UriKind.Absolute),
        };

        var merged = Resources.MergedDictionaries;
        if (_appColors is not null)
        {
            merged.Remove(_appColors);
        }

        merged.Add(colors);
        _appColors = colors;

        ShownTheme = variant switch
        {
            "Light" => ShownTheme.Light,
            "Dark" => ShownTheme.Dark,
            _ => ShownTheme.HighContrast,
        };
        ShownThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The theme the windows actually show right now ("System" resolved, a contrast theme wins).</summary>
    internal ShownTheme ShownTheme { get; private set; }

    /// <summary>After a theme switch (setting, preview, or Windows): for things DynamicResource can't follow.</summary>
    internal event EventHandler? ShownThemeChanged;

    /// <summary>The "app mode" in Windows Settings → Personalization → Colors (what Fluent's System mode follows).</summary>
    private static bool IsWindowsInLightMode()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    // Windows switched light/dark or a contrast theme: Fluent follows by itself, our colors and the tray icon's
    // artwork need a nudge. Raised on a SystemEvents thread, hence the dispatcher.
    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category is Microsoft.Win32.UserPreferenceCategory.General
            or Microsoft.Win32.UserPreferenceCategory.Color
            or Microsoft.Win32.UserPreferenceCategory.Accessibility)
        {
            RunOnUiThread(() =>
            {
                ApplyAppColors();
                _trayIcon?.UpdateForTaskbarColor();
            });
        }
    }

    public bool IsShuttingDown { get; private set; }

    /// <summary>Started by the Windows autostart (Run key or task): stays in the tray, no window.</summary>
    public bool StartedAtSignIn { get; init; }

    /// <summary>The older version that exited for this one (see SingleInstance), for the log.</summary>
    public Version? ReplacedVersion { get; init; }

    public bool RestartRequested { get; private set; }

    /// <summary>Program.Main starts the new instance with "run as administrator".</summary>
    public bool RestartAsAdministrator { get; private set; }

    public void RestartApplication(bool asAdministrator = false) => RunOnUiThread(() =>
    {
        IsShuttingDown = true;
        RestartRequested = true;
        RestartAsAdministrator = asAdministrator;
        Shutdown();
    });

    /// <summary>
    /// Queues an action on the UI thread. This makes the IAppShell methods safe to call from any thread
    /// (e.g. single-instance activation), and makes exceptions reach OnDispatcherUnhandledException.
    /// Running inline would break that for tray clicks: H.NotifyIcon calls our commands via Dispatcher.Invoke
    /// from a background timer thread, and Invoke rethrows exceptions there, which terminates the process.
    /// </summary>
    private void RunOnUiThread(Action action) => Dispatcher.BeginInvoke(action);

    private void ShowSettingsWindowCore()
    {
        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = _host.Services.GetRequiredService<SettingsWindow>();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private static void OpenFolderCore(string folder)
    {
        // The folder may not exist yet: logs appear after the first log line, settings after the first save.
        // As administrator never through a junction (see ElevatedWriteGuard); if that's refused, Explorer says it's missing.
        try
        {
            ElevatedWriteGuard.CreateFolder(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Opening it below shows what's wrong
        }

        // UseShellExecute: let Windows decide how to open the path, i.e. in Explorer
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogExiting(e.ApplicationExitCode, RestartRequested);
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _trayIcon?.Dispose();
        // Synchronous on purpose, see explanation
        _host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        _host.Dispose();
        base.OnExit(e);
    }

    private void RegisterExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    // UI thread (button clicks, commands, bindings...): we can catch these and keep running
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogUiThreadException(e.Exception);
        ShowErrorMessage(e.Exception);
        e.Handled = true;
    }

    // Background thread: the process terminates after this handler no matter what. We can only log.
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogFatalException((Exception)e.ExceptionObject);

        // Last chance: don't leave the user without taskbar or with a confined mouse
        ForegroundEffects.UndoRecorded();
    }

    // A Task failed and nobody awaited it: not fatal, just make sure it doesn't go unnoticed
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogUnobservedTaskException(e.Exception);
        e.SetObserved();
    }

    private void ShowErrorMessage(Exception ex)
    {
        try
        {
            // Without a log file there are no details to point to, and no folder worth opening
            var answer = MessageDialog.Show(
                _settingsWindow,
                Strings.Error_Unexpected_Title,
                UnexpectedErrorMessage(LoggingSetup.FileLogActive),
                primaryText: Strings.Common_OK,
                secondaryText: LoggingSetup.FileLogActive ? Strings.Tray_OpenLogFolder : null,
                details: ex.Message);

            if (answer == MessageDialogResult.Secondary)
            {
                OpenFolderCore(LoggingSetup.LogFolder);
            }
        }
        catch (Exception dialogError)
        {
            // The themed dialog itself failed (e.g. broken resources): the plain system box always works
            LogUiThreadException(dialogError);
            MessageBox.Show($"{UnexpectedErrorMessage(LoggingSetup.FileLogActive)}\n\n{ex.Message}", Strings.AppName,
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>"Details are in the log file" only if there is one (see <see cref="LoggingSetup.FileLogActive"/>).</summary>
    internal static string UnexpectedErrorMessage(bool logWritten) =>
        logWritten ? Strings.Error_Unexpected_Message : Strings.Error_Unexpected_MessageNoLog;

    // Who runs and how, first in every run: several runs share a day's file
    [LoggerMessage(Level = LogLevel.Information, Message = "Starting version {Version} on Windows {WindowsVersion}, administrator: {Elevated}, started at sign-in: {AtSignIn}, UI culture {Culture}")]
    private partial void LogStarting(string version, Version windowsVersion, bool elevated, bool atSignIn, string culture);

    [LoggerMessage(Level = LogLevel.Information, Message = "Took over from the older version {OldVersion}, which exited for this one")]
    private partial void LogReplacedOlderVersion(Version oldVersion);

    [LoggerMessage(Level = LogLevel.Information, Message = "A newer version was started and asks this one to exit")]
    private partial void LogExitForNewerVersion();

    [LoggerMessage(Level = LogLevel.Information, Message = "Staying: the exit for the newer version was cancelled")]
    private partial void LogExitForNewerVersionDeclined();

    [LoggerMessage(Level = LogLevel.Critical, Message = "Startup failed")]
    private partial void LogStartupFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception on the UI thread")]
    private partial void LogUiThreadException(Exception ex);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Unhandled exception on a background thread, the process will terminate")]
    private partial void LogFatalException(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unobserved task exception")]
    private partial void LogUnobservedTaskException(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Exiting with code {ExitCode}, restart: {Restart}")]
    private partial void LogExiting(int exitCode, bool restart);
}

