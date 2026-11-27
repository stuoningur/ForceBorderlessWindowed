using System.ComponentModel;
using System.Diagnostics;

using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed;

internal static class Program
{
    private const int ErrorCancelled = 1223;   // the user clicked "No" in the UAC prompt

    [STAThread]
    private static int Main(string[] args)
    {
        // Started as the elevated helper for "start as administrator": do that and exit, no UI, no single instance
        if (ElevatedAutostart.TryRunFromCommandLine(args, out var helperExitCode))
        {
            return helperExitCode;
        }

        int exitCode;
        bool restart;
        bool asAdministrator;

        // Windows autostart passes this: start in the tray only. Started by the user: show the window.
        var startedAtSignIn = args.Contains(AutostartService.StartedAtSignInArgument, StringComparer.OrdinalIgnoreCase);

        var instance = SingleInstance.TryAcquire();
        Version? replacedVersion = null;
        if (instance is null)
        {
            // Already running. An autostart just quits.
            if (startedAtSignIn)
            {
                return 0;
            }

            // Started by the user: a newer version takes the place of an older one (an update), otherwise the
            // running one brings up its window
            var handover = SingleInstance.TryHandOverFromOlderVersion(out replacedVersion);
            if (handover == HandoverResult.Done)
            {
                instance = SingleInstance.TryAcquire();
            }

            if (instance is null)
            {
                // Declined: the older one stays, and its window is open already (it asked about unsaved changes)
                if (handover != HandoverResult.Declined)
                {
                    SingleInstance.SignalFirstInstance();
                }

                return 0;
            }
        }

        using (instance)
        {
            // Before App's own exception handlers exist (set up in its constructor) and outside OnStartup's catch:
            // settings, culture, XAML resources. Without this a failure here ends the app without a word.
            App app;
            try
            {
                app = new App { StartedAtSignIn = startedAtSignIn, ReplacedVersion = replacedVersion };
                app.InitializeComponent();
            }
            catch (Exception ex)
            {
                ReportStartupCrash(ex);
                return 1;
            }

            // Called on thread-pool threads; the App methods move themselves to the UI thread
            instance.ListenForActivation(app.ShowSettingsWindow);
            instance.ListenForExitRequest(() => app.ExitForNewerVersion(instance.DeclineExit));

            exitCode = app.Run();
            restart = app.RestartRequested;
            asAdministrator = app.RestartAsAdministrator;
        } // Mutex is released here

        // Only now: the new instance would otherwise see the mutex, hand over to us and quit
        if (restart)
        {
            StartNewInstance(asAdministrator);
        }

        return exitCode;
    }

    /// <summary>A crash file (the logger may not exist yet) and a plain message box (WPF resources may be what failed).</summary>
    private static void ReportStartupCrash(Exception ex)
    {
        var written = LoggingSetup.TryWriteCrashFile(ex);

        string message;
        try
        {
            message = App.UnexpectedErrorMessage(written);
        }
        catch (Exception)
        {
            message = "An unexpected error occurred.";   // even the texts couldn't be loaded
        }

        System.Windows.MessageBox.Show($"{message}\n\n{ex.Message}", "Force Borderless Windowed",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }

    private static void StartNewInstance(bool asAdministrator)
    {
        // UseShellExecute is required for Verb "runas", which shows the UAC prompt
        var startInfo = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = true,
            Verb = asAdministrator ? "runas" : "",
        };

        try
        {
            Process.Start(startInfo)?.Dispose();
        }
        catch (Win32Exception ex) when (asAdministrator && ex.NativeErrorCode == ErrorCancelled)
        {
            // UAC declined: we have already exited, so come back without admin rights instead of vanishing
            Process.Start(Environment.ProcessPath!)?.Dispose();
        }
    }
}
