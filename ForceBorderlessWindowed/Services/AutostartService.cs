using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Claims;
using System.Security.Principal;

using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ForceBorderlessWindowed.Services;

internal sealed partial class AutostartService : IAutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Task Manager's "Startup apps" page doesn't delete the Run value, it stores an enabled/disabled flag here
    private const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "ForceBorderlessWindowed";

    private const int ErrorCancelled = 1223;   // the user clicked "No" in the UAC prompt

    /// <summary>
    /// Command-line argument of both autostart registrations (Run key and elevated task): the app then starts
    /// in the tray only, while a start by the user opens the settings window.
    /// </summary>
    public const string StartedAtSignInArgument = "--autostart";

    /// <summary>
    /// Limit for the elevated helper, counted from when the UAC prompt was answered. Copying the app and
    /// creating the task takes a few seconds at most; longer means it hangs, and Settings must not wait forever.
    /// </summary>
    private static readonly TimeSpan ElevatedHelperTimeout = TimeSpan.FromSeconds(60);

    private readonly ILogger<AutostartService> _logger;

    /// <summary>
    /// The user running the app now, not an admin account that may approve the UAC prompt: the task is theirs.
    /// </summary>
    private readonly string _userSid;

    /// <summary>
    /// Whether the elevated task exists. Checking it means starting schtasks.exe, so it starts in the background
    /// when the service is created (at app startup) and is then only updated by <see cref="SetModeAsync"/>.
    /// </summary>
    private Task<bool> _taskRegistered;

    public AutostartService(ILogger<AutostartService> logger)
    {
        _logger = logger;

        using var identity = WindowsIdentity.GetCurrent();
        _userSid = identity.User?.Value ?? throw new InvalidOperationException("The current user's SID is not available.");
        CanStartElevated = IsAdministratorAccount(identity);

        _taskRegistered = CheckTaskRegisteredAsync();
        UpgradeRunKey();
    }

    public bool CanStartElevated { get; }

    public async Task<AutostartMode> GetModeAsync() =>
        await _taskRegistered.ConfigureAwait(false) ? AutostartMode.Elevated
        : IsRunKeyRegistered() ? AutostartMode.Normal
        : AutostartMode.Off;

    public async Task<AutostartChangeResult> SetModeAsync(AutostartMode mode)
    {
        var current = await GetModeAsync();
        if (mode == current)
        {
            return AutostartChangeResult.Changed;
        }

        // The task part first: it's the one that can be declined (UAC). If it is, nothing else changes.
        if (mode == AutostartMode.Elevated || current == AutostartMode.Elevated)
        {
            var change = await ChangeTaskAsync(register: mode == AutostartMode.Elevated);
            if (change != AutostartChangeResult.Changed)
            {
                return change;
            }
        }

        // Only "Normal" uses the Run key; for "Elevated" it must go, or the app would start twice
        SetRunKey(mode == AutostartMode.Normal);

        LogAutostartChanged(mode, mode == AutostartMode.Elevated ? ElevatedAutostart.InstalledExePath : ExePath);
        return AutostartChangeResult.Changed;
    }

    public async Task<bool> IsElevatedCopyOutdatedAsync() =>
        await _taskRegistered.ConfigureAwait(false)
        && !ElevatedAutostart.IsRunningFromInstallFolder
        && (AppInfo.GetVersionOfCopy(ElevatedAutostart.InstalledExePath) is not { } installed || installed < AppInfo.FileVersion);

    public async Task<AutostartChangeResult> UpdateElevatedCopyAsync()
    {
        // Registering again does exactly that: copies this exe over the old copy, and sets up the task for it
        var change = await ChangeTaskAsync(register: true);
        if (change == AutostartChangeResult.Changed)
        {
            LogElevatedCopyUpdated(ElevatedAutostart.InstalledExePath, AppInfo.DisplayVersion);
        }

        return change;
    }

    /// <summary>Creates or deletes this user's elevated task (see <see cref="ElevatedAutostart"/>).</summary>
    private async Task<AutostartChangeResult> ChangeTaskAsync(bool register)
    {
        var result = await RunElevatedAsync(register ? ElevatedAutostart.RegisterArgument : ElevatedAutostart.UnregisterArgument);
        if (result != ElevatedAutostart.Result.Success)
        {
            // Declined, failed or timed out: what is registered now? Checked again, so the window shows the truth
            _taskRegistered = CheckTaskRegisteredAsync();
            return result == ElevatedAutostart.Result.InUse ? AutostartChangeResult.CopyInUse : AutostartChangeResult.NotChanged;
        }

        _taskRegistered = Task.FromResult(register);
        return AutostartChangeResult.Changed;
    }

    private Task<bool> CheckTaskRegisteredAsync()
    {
        var userSid = _userSid;
        return Task.Run(() => ElevatedAutostart.IsTaskRegistered(userSid));
    }

    /// <summary>
    /// Member of the Administrators group. With UAC, an admin's normal token has that group only as
    /// "deny only" (it's switched on by elevating), so that counts too.
    /// </summary>
    private static bool IsAdministratorAccount(WindowsIdentity identity)
    {
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            return true;
        }

        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value;
        return identity.Claims.Any(c => c.Type == ClaimTypes.DenyOnlySid && c.Value == administrators);
    }

    private static string ExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The process path is not available.");

    // ---------- Registry (Normal) ----------

    private static bool IsRunKeyRegistered()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        if (runKey?.GetValue(ValueName) is not string command)
        {
            return false;
        }

        // An entry for a different (e.g. moved) exe counts as "off"; enabling again fixes the path
        return string.Equals(GetCommandPath(command), ExePath, StringComparison.OrdinalIgnoreCase)
            && !IsDisabledInTaskManager();
    }

    private static string RunKeyCommand => $"\"{ExePath}\" {StartedAtSignInArgument}";

    /// <summary>The exe path of a command line: the quoted part, or everything up to the first space.</summary>
    private static string GetCommandPath(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end < 0 ? command[1..] : command[1..end];
        }

        var space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    /// <summary>
    /// Keeps the Run key entry of older versions working:
    /// - Entries written by older versions start the app without <see cref="StartedAtSignInArgument"/>, which
    ///   would now open the settings window at every sign-in: the argument is added.
    /// - An entry for an older version of the app in another place (the user saved the update somewhere else):
    ///   moved to this exe, so the next sign-in starts the new version. Newer or unknown exes are left alone.
    /// Only the command changes: a "disabled" flag from Task Manager stays as it is (unlike <see cref="SetRunKey"/>,
    /// which is an explicit choice).
    /// </summary>
    private void UpgradeRunKey()
    {
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (runKey?.GetValue(ValueName) is not string command)
            {
                return;
            }

            var path = GetCommandPath(command);
            if (string.Equals(path, ExePath, StringComparison.OrdinalIgnoreCase))
            {
                if (!command.Contains(StartedAtSignInArgument, StringComparison.OrdinalIgnoreCase))
                {
                    runKey.SetValue(ValueName, RunKeyCommand);
                    LogRunKeyUpgraded();
                }
            }
            else if (AppInfo.GetVersionOfCopy(path) is { } version && version < AppInfo.FileVersion)
            {
                runKey.SetValue(ValueName, RunKeyCommand);
                LogRunKeyMovedFromOlderVersion(path, version);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Not worth failing the startup for: the window would just open at sign-in
            LogRunKeyUpgradeFailed(ex);
        }
    }

    private static void SetRunKey(bool enabled)
    {
        using (var runKey = Registry.CurrentUser.CreateSubKey(RunKeyPath))
        {
            if (enabled)
            {
                runKey.SetValue(ValueName, RunKeyCommand);
            }
            else
            {
                runKey.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        // Explicitly enabling in our app overrides a "disabled" flag from Task Manager;
        // disabling removes our entry completely, so the flag is cleaned up as well
        using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath, writable: true);
        approvedKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool IsDisabledInTaskManager()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupApprovedKeyPath);

        // First byte of the binary value: even (0x02, 0x06) = enabled, odd (0x03, 0x07) = disabled
        return key?.GetValue(ValueName) is byte[] { Length: > 0 } data && (data[0] & 1) == 1;
    }

    // ---------- Task Scheduler (Elevated) ----------

    /// <summary>
    /// Does the admin part (see <see cref="ElevatedAutostart"/>): directly if we already run as admin,
    /// otherwise in a second instance of this exe started with "run as administrator" (UAC prompt).
    /// </summary>
    /// <returns>The helper's result (failures are logged); null if UAC was declined or the helper timed out.</returns>
    private async Task<ElevatedAutostart.Result?> RunElevatedAsync(string command)
    {
        int exitCode;
        if (Environment.IsPrivilegedProcess)
        {
            // Same work as the helper, in the background so the settings window stays responsive
            var userSid = _userSid;
            exitCode = (int)await Task.Run(() => command == ElevatedAutostart.RegisterArgument
                ? ElevatedAutostart.Register(userSid)
                : ElevatedAutostart.Unregister(userSid));
        }
        else
        {
            // UseShellExecute is required for Verb "runas"
            var startInfo = new ProcessStartInfo(ExePath, $"{command} {_userSid}")
            {
                UseShellExecute = true,
                Verb = "runas",
            };

            try
            {
                // Returns once the UAC prompt is answered (the prompt has its own secure desktop anyway)
                using var process = Process.Start(startInfo)!;
                using var timeout = new CancellationTokenSource(ElevatedHelperTimeout);
                await process.WaitForExitAsync(timeout.Token);
                exitCode = process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                LogUacDeclined();
                return null;
            }
            catch (OperationCanceledException)
            {
                // Left running: it's elevated, we couldn't end it anyway
                LogElevatedTimeout(command, (int)ElevatedHelperTimeout.TotalSeconds);
                return null;
            }
        }

        var result = (ElevatedAutostart.Result)exitCode;
        if (result != ElevatedAutostart.Result.Success)
        {
            LogElevatedFailed(command, result);
        }

        return result;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Autostart set to {Mode} for {Path}")]
    private partial void LogAutostartChanged(AutostartMode mode, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Elevated autostart step {Command} failed: {Result}")]
    private partial void LogElevatedFailed(string command, ElevatedAutostart.Result result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Elevated autostart step {Command} didn't finish within {Seconds} s")]
    private partial void LogElevatedTimeout(string command, int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Administrator permission for the autostart task was declined")]
    private partial void LogUacDeclined();

    [LoggerMessage(Level = LogLevel.Information, Message = "Added the autostart argument to the Run key entry of an older version")]
    private partial void LogRunKeyUpgraded();

    [LoggerMessage(Level = LogLevel.Information, Message = "Moved the Run key entry from the older version {OldVersion} at {OldPath} to this exe")]
    private partial void LogRunKeyMovedFromOlderVersion(string oldPath, Version oldVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the Run key entry of an older version")]
    private partial void LogRunKeyUpgradeFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Updated the admin autostart copy at {Path} to version {Version}")]
    private partial void LogElevatedCopyUpdated(string path, string version);
}
