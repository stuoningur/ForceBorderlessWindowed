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
            var app = new App { StartedAtSignIn = startedAtSignIn, ReplacedVersion = replacedVersion };
            app.InitializeComponent();

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
