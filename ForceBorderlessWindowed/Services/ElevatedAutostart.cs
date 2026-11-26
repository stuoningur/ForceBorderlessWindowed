using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// The part of "start as administrator" that needs admin rights. Runs in a separate, elevated instance of
/// this exe (see <see cref="TryRunFromCommandLine"/>), or directly when the app already runs as admin.
///
/// The task starts a copy of the app in Program Files, not the exe the user happens to run (e.g. from
/// Downloads): only administrators can change files there. Otherwise any program running as the user
/// could replace the exe or put a DLL next to it and get administrator rights at the next sign-in.
/// For the same reason the task definition is written inside that folder, not to %TEMP%, where it could be
/// swapped while the UAC prompt is open.
///
/// One task per Windows user (the name ends with the user's SID), so users on the same PC don't overwrite or
/// delete each other's task. The copy in Program Files is shared by all of them.
/// </summary>
internal static class ElevatedAutostart
{
    public const string RegisterArgument = "--register-elevated-autostart";
    public const string UnregisterArgument = "--unregister-elevated-autostart";

    private const string TaskNamePrefix = "ForceBorderlessWindowed";

    /// <summary>The single, machine-wide task name of earlier versions. Taken over (deleted) by its own user.</summary>
    private const string LegacyTaskName = TaskNamePrefix;

    /// <summary>schtasks normally answers within a second; a hanging one must not block forever.</summary>
    private static readonly TimeSpan SchtasksTimeout = TimeSpan.FromSeconds(30);

    // Full path on purpose: a bare "schtasks.exe" is searched in the app's own folder and the current
    // directory first, so a planted schtasks.exe next to the app (e.g. in Downloads) would run as admin
    private static readonly string SchtasksPath = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

    /// <summary>Exit codes of the elevated instance.</summary>
    public enum Result
    {
        Success = 0,
        InvalidArguments = 2,
        NotElevated = 3,
        CopyFailed = 4,
        SchtasksFailed = 5,

        /// <summary>Another user's change to the shared copy didn't finish in time.</summary>
        Busy = 6,

        /// <summary>The copy in Program Files is running (for another Windows user): it can't be replaced now.</summary>
        InUse = 7,
    }

    public static string InstallFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ForceBorderlessWindowed");

    /// <summary>The app's assembly name; the build output's files (dll, deps.json, runtimeconfig.json) are named after it.</summary>
    private static readonly string AppName = typeof(ElevatedAutostart).Assembly.GetName().Name!;

    /// <summary>
    /// The copy the task starts. Always under the original name, not the name of the exe that happens to run:
    /// the copy is shared, and registering from a renamed exe must not break the other users' tasks.
    /// </summary>
    public static string InstalledExePath { get; } = Path.Combine(InstallFolder, AppName + ".exe");

    private static string ExePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The process path is not available.");

    private static string AppFolder => Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    public static bool IsRunningFromInstallFolder =>
        string.Equals(Path.GetFullPath(AppFolder), Path.GetFullPath(InstallFolder), StringComparison.OrdinalIgnoreCase);

    private static string TaskNameFor(string userSid) => $"{TaskNamePrefix}-{userSid}";

    /// <summary>
    /// Program.Main calls this first: if the app was started as the elevated helper, does the work and returns
    /// true with the exit code. The normal app (tray icon, single instance) doesn't start then.
    /// </summary>
    public static bool TryRunFromCommandLine(string[] args, out int exitCode)
    {
        Result? result = args switch
        {
            [RegisterArgument, var userSid] => WithSid(userSid, Register),
            [UnregisterArgument, var userSid] => WithSid(userSid, Unregister),
            [RegisterArgument, ..] or [UnregisterArgument, ..] => Result.InvalidArguments,
            _ => null,
        };

        exitCode = (int)result.GetValueOrDefault();
        return result is not null;
    }

    /// <summary>Validates the SID from the command line (it ends up in the task XML and the task name).</summary>
    private static Result WithSid(string userSid, Func<string, Result> action)
    {
        try
        {
            return action(new SecurityIdentifier(userSid).Value);
        }
        catch (ArgumentException)
        {
            return Result.InvalidArguments;
        }
    }

    /// <param name="userSid">
    /// The user whose sign-in starts the task. Passed in by the non-elevated app: if a different admin account
    /// approves the UAC prompt, this process runs as that account, but the task must start for the actual user.
    /// </param>
    public static Result Register(string userSid) => RunExclusive(() => RegisterCore(userSid));

    public static Result Unregister(string userSid) => RunExclusive(() => UnregisterCore(userSid));

    /// <summary>
    /// One change at a time, machine-wide: the copy in Program Files, its staging folder and the task list are
    /// shared by all users. "Global\" so helpers in different sign-in sessions see the same mutex.
    /// Any program can create or hold a global mutex (only file mappings need a privilege there), so the
    /// lock is only coordination between our own helpers: someone else holding it can at most make this
    /// return Busy, and one we aren't allowed to open counts as Busy too. It guards no security decision.
    /// </summary>
    private static Result RunExclusive(Func<Result> action)
    {
        using var mutex = TryCreateInstallMutex();
        if (mutex is null)
        {
            return Result.Busy;
        }

        try
        {
            // Below the 60 s the app waits for this helper, so a wait here ends as "Busy", not as a timeout
            if (!mutex.WaitOne(TimeSpan.FromSeconds(45)))
            {
                return Result.Busy;
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous helper died while holding it: we own it now, and its half-done work is cleaned up
            // like any failed attempt (staging folder deleted, backups removed)
        }

        try
        {
            return action();
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static Mutex? TryCreateInstallMutex()
    {
        try
        {
            return new Mutex(initiallyOwned: false, @"Global\ForceBorderlessWindowed-Install");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // Created by another program with permissions that lock us out (or a different object type)
            return null;
        }
    }

    private static Result RegisterCore(string userSid)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return Result.NotElevated;
        }

        // Windows doesn't let a running copy's folder be renamed or replaced: the .NET host keeps its files open.
        // That happens when another user of this PC runs it. Checked first, so nothing is copied for nothing.
        if (!IsRunningFromInstallFolder && IsInstalledCopyRunning())
        {
            return Result.InUse;
        }

        try
        {
            CopyApp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.CopyFailed;
        }

        // Unique name: never a file another helper is writing or deleting at the same moment
        var xmlPath = Path.Combine(InstallFolder, $"autostart-task-{Guid.NewGuid():N}.xml");
        try
        {
            // schtasks expects UTF-16 for /XML
            File.WriteAllText(xmlPath, BuildTaskXml(userSid, InstalledExePath), Encoding.Unicode);
            if (!RunSchtasks($"/Create /TN \"{TaskNameFor(userSid)}\" /XML \"{xmlPath}\" /F"))
            {
                return Result.SchtasksFailed;
            }
        }
        finally
        {
            File.Delete(xmlPath);
        }

        // Replaced by the per-user task now; the old one would start the app a second time
        if (IsLegacyTaskOf(userSid))
        {
            DeleteTask(LegacyTaskName);
        }

        return Result.Success;
    }

    private static Result UnregisterCore(string userSid)
    {
        if (!Environment.IsPrivilegedProcess)
        {
            return Result.NotElevated;
        }

        if (!DeleteTask(TaskNameFor(userSid))
            || (IsLegacyTaskOf(userSid) && !DeleteTask(LegacyTaskName)))
        {
            return Result.SchtasksFailed;
        }

        // The copy isn't needed anymore once no user's task starts it, unless it's the one running right now
        // (it can't delete itself)
        if (!IsRunningFromInstallFolder && !AnyTaskLeft())
        {
            TryDeleteFolder(InstallFolder);   // a leftover is harmless, the tasks are gone
        }

        DeleteOldBackups();
        return Result.Success;
    }

    /// <summary>True if this user's task exists and starts the copy in Program Files. Doesn't need admin rights.</summary>
    public static bool IsTaskRegistered(string userSid)
    {
        var task = QueryTask(TaskNameFor(userSid));
        if (task is null && IsLegacyTaskOf(userSid))
        {
            task = QueryTask(LegacyTaskName);
        }

        return string.Equals(task?.Command?.Trim().Trim('"'), InstalledExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Replaces the copy in Program Files with the files of the running app, without ever leaving a
    /// half-copied folder there: the new files go to a separate folder first, which then takes the place of the
    /// old one by renaming. If anything fails, the old copy stays (or is put back) and the task keeps working.
    /// </summary>
    private static void CopyApp()
    {
        if (IsRunningFromInstallFolder)
        {
            return;   // this already is the copy
        }

        // Everything decided and checked before anything is written: names from deps.json (writable by the user)
        // that would lead out of the staging folder stop the copy before a single folder is created
        var plan = BuildCopyPlan();

        DeleteOldBackups();

        // Fresh folder: no files of an older version stay behind, and it inherits the Program Files permissions
        var staging = InstallFolder + ".new";
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);   // left over from a failed attempt
        }

        Directory.CreateDirectory(staging);

        string? backup = null;
        try
        {
            foreach (var (source, target) in plan.Files)
            {
                var targetPath = Path.Combine(staging, target);
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);   // inside staging, see CheckRelativePath

                // Read through the checked handle, not copied by path: the check and the copy are the same open file
                using var input = OpenSource(plan.SourceFolder, source);
                using var output = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write);
                input.CopyTo(output);
            }

            // The old copy is renamed out of the way, so the new one takes its place in one step. Windows refuses that
            // while the copy runs (RegisterCore checks it first; it may have been started since): then nothing has
            // changed yet. Unique name, so an old backup that couldn't be deleted yet doesn't block it.
            if (Directory.Exists(InstallFolder))
            {
                backup = $"{InstallFolder}.old-{Guid.NewGuid():N}";
                Directory.Move(InstallFolder, backup);
            }

            try
            {
                Directory.Move(staging, InstallFolder);
            }
            catch when (backup is not null)
            {
                Directory.Move(backup, InstallFolder);   // put the old copy back
                throw;
            }
        }
        catch
        {
            TryDeleteFolder(staging);   // a half-done copy is of no use, and gone already if it took its place
            throw;
        }

        if (backup is not null)
        {
            TryDeleteFolder(backup);   // fails while a file in it is still open; DeleteOldBackups tries again next time
        }
    }

    /// <summary>The copy in Program Files runs right now, in any session (as administrator, all of them can be seen).</summary>
    private static bool IsInstalledCopyRunning()
    {
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(InstalledExePath)))
        {
            using (process)
            {
                if (string.Equals(WindowQueries.GetExecutablePath((uint)process.Id), InstalledExePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <param name="SourceFolder">Where the app folder really is, after following any junction above it.</param>
    /// <param name="Files">Paths relative to the app folder (source) and to the staging folder (target).</param>
    private sealed record CopyPlan(string SourceFolder, List<(string Source, string Target)> Files);

    /// <summary>
    /// The exe under its fixed name (see InstalledExePath), then only the files the app consists of.
    /// Never the whole folder: if the app was unpacked straight into e.g. Downloads, everything else in
    /// there would end up in Program Files, readable by every user of the PC.
    /// </summary>
    /// <exception cref="IOException">A file is missing, unreadable, or its name leads somewhere else.</exception>
    private static CopyPlan BuildCopyPlan()
    {
        // The app folder can be reached through a junction (e.g. Downloads moved to another drive); that's fine,
        // as long as every file is really inside it. It can't be swapped meanwhile: a folder that holds a running
        // exe (this one) can't be renamed or deleted.
        string sourceFolder;
        using (var folder = ElevatedWriteGuard.OpenFolder(Path.GetFullPath(AppFolder)))
        {
            sourceFolder = ElevatedWriteGuard.GetFinalPath(folder)
                ?? throw new IOException("Not copied: could not check where the app folder really is.");
        }

        var exePath = Path.GetFullPath(ExePath);
        if (!string.Equals(Path.GetDirectoryName(exePath), Path.GetFullPath(AppFolder), StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Not copied: the exe isn't in the app folder.");
        }

        var files = new List<(string Source, string Target)> { (Path.GetFileName(exePath), Path.GetFileName(InstalledExePath)) };
        files.AddRange(GetAppFiles(sourceFolder).Select(file => (file, file)));

        foreach (var (source, target) in files)
        {
            CheckRelativePath(source);
            CheckRelativePath(target);
        }

        return new CopyPlan(sourceFolder, files);
    }

    /// <summary>
    /// A path from deps.json must stay inside the folder it's relative to: no drive or root ("C:\Windows\x" would
    /// make Path.Combine ignore the staging folder), no "..", no ":" (alternate data streams), nothing empty.
    /// </summary>
    /// <exception cref="IOException">It doesn't.</exception>
    private static void CheckRelativePath(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (relativePath.Length == 0
            || Path.IsPathRooted(relativePath)
            || relativePath.Contains(':')
            || relativePath.IndexOfAny(Path.GetInvalidPathChars()) >= 0
            || parts.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
        {
            throw new IOException($"Not copied: \"{relativePath}\" from the app's file list leads outside its folder.");
        }
    }

    /// <summary>
    /// Opens a source file for reading and checks the handle: it must really be that file in the app folder,
    /// not reached through a junction or link, and not a hard link (see ElevatedWriteGuard.VerifyHandle). The
    /// app folder is writable by the user, and an admin process copying a protected file into Program Files would
    /// make it readable for every user. FileShare.Read: nobody can change or replace it while it's copied.
    /// </summary>
    private static FileStream OpenSource(string sourceFolder, string relativePath)
    {
        var stream = new FileStream(Path.Combine(AppFolder, relativePath), FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            ElevatedWriteGuard.VerifyHandle(stream.SafeFileHandle, Path.Combine(sourceFolder, relativePath));
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The app's files besides the exe, relative to its folder: the main dll with its runtimeconfig and
    /// deps.json, plus every library, native file and satellite resource that deps.json lists. That's the
    /// same list the .NET host uses to load the app, so nothing it needs is left out.
    /// None for the release build: published as a single file, the exe holds all of that.
    /// </summary>
    /// <exception cref="IOException">deps.json is missing or not readable.</exception>
    private static List<string> GetAppFiles(string sourceFolder)
    {
        if (IsSingleFile())
        {
            // Not even a .pdb lying next to it: the release build has its debug symbols inside the exe
            return [];
        }

        var depsFile = AppName + ".deps.json";
        var files = new List<string> { AppName + ".dll", AppName + ".runtimeconfig.json", depsFile };

        // Optional: gives the installed copy's logs line numbers in stack traces
        if (File.Exists(Path.Combine(AppFolder, AppName + ".pdb")))
        {
            files.Add(AppName + ".pdb");
        }

        try
        {
            using var stream = OpenSource(sourceFolder, depsFile);
            using var deps = JsonDocument.Parse(stream);

            foreach (var target in deps.RootElement.GetProperty("targets").EnumerateObject())
            {
                foreach (var library in target.Value.EnumerateObject())
                {
                    // Framework-dependent output layout: libraries and native files flat next to the exe,
                    // resources in a folder per language, RID-specific files under their runtimes\ path
                    AddAssets(library.Value, "runtime", (key, _) => Path.GetFileName(key));
                    AddAssets(library.Value, "native", (key, _) => Path.GetFileName(key));
                    AddAssets(library.Value, "resources",
                        (key, asset) => Path.Combine(asset.GetProperty("locale").GetString()!, Path.GetFileName(key)));
                    AddAssets(library.Value, "runtimeTargets", (key, _) => key);
                }
            }
        }
        // ArgumentException: e.g. a "locale" that is null
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            throw new IOException($"{depsFile} can't be read.", ex);
        }

        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        void AddAssets(JsonElement library, string kind, Func<string, JsonElement, string> toRelativePath)
        {
            if (library.TryGetProperty(kind, out var assets))
            {
                foreach (var asset in assets.EnumerateObject())
                {
                    files.Add(toRelativePath(asset.Name, asset.Value).Replace('/', Path.DirectorySeparatorChar));
                }
            }
        }
    }

    /// <summary>
    /// Published as a single file (the release build), the app's assemblies are loaded from inside the exe and
    /// have no location of their own. An empty Location is the documented way to tell.
    /// </summary>
    [UnconditionalSuppressMessage("SingleFile", "IL3000:Avoid accessing Assembly file path when publishing as a single file",
        Justification = "Only checked for being empty, which is exactly what a single-file app returns.")]
    private static bool IsSingleFile() => string.IsNullOrEmpty(typeof(ElevatedAutostart).Assembly.Location);

    /// <summary>Backups of earlier updates that couldn't be deleted then (a file in them was still open).</summary>
    private static void DeleteOldBackups()
    {
        var parent = Path.GetDirectoryName(InstallFolder)!;
        try
        {
            foreach (var folder in Directory.EnumerateDirectories(parent, Path.GetFileName(InstallFolder) + ".old-*"))
            {
                TryDeleteFolder(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only tidying up
        }
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still in use; harmless
        }
    }

    /// <summary>True if the task is confirmed gone afterwards (also when it didn't exist).</summary>
    /// <remarks>
    /// schtasks has the same exit code for "not found" and for real errors (or a timeout). So: try to delete,
    /// and if that fails, it only counts as done when the full task list could be read and doesn't contain it.
    /// A failed query of the task itself proves nothing: Task Scheduler may just not be answering.
    /// </remarks>
    private static bool DeleteTask(string taskName) =>
        RunSchtasks($"/Delete /TN \"{taskName}\" /F")
        || ListTasks() is { } list && !list.Contains($"\"\\{taskName}\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>True if any user still has one of our tasks. When unsure (query failed): true, so nothing is deleted.</summary>
    private static bool AnyTaskLeft() =>
        ListTasks() is not { } list || list.Contains($"\"\\{TaskNamePrefix}", StringComparison.OrdinalIgnoreCase);

    /// <summary>All tasks in the root folder as CSV ("\name" quoted in the first column), or null if schtasks failed.</summary>
    private static string? ListTasks() => RunSchtasksForOutput("/Query /FO CSV /NH");

    /// <summary>The old machine-wide task exists and belongs to this user (set up with an earlier version).</summary>
    private static bool IsLegacyTaskOf(string userSid) =>
        QueryTask(LegacyTaskName)?.UserId is { } owner && string.Equals(ToSid(owner), userSid, StringComparison.OrdinalIgnoreCase);

    /// <summary>The task's command and user, or null if it doesn't exist (or can't be read).</summary>
    private static (string? Command, string? UserId)? QueryTask(string taskName)
    {
        try
        {
            var xml = RunSchtasksForOutput($"/Query /TN \"{taskName}\" /XML");
            if (xml is null)
            {
                return null;
            }

            var elements = XDocument.Parse(xml).Descendants().ToList();
            string? Value(string name) => elements.FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            return (Value("Command"), Value("UserId"));
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>Task Scheduler may show the user as a SID or as "DOMAIN\name".</summary>
    private static string? ToSid(string user)
    {
        try
        {
            return user.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                ? new SecurityIdentifier(user).Value
                : new NTAccount(user).Translate(typeof(SecurityIdentifier)).Value;
        }
        catch (SystemException)   // e.g. IdentityNotMappedException, or no domain controller reachable
        {
            return null;
        }
    }

    private static string BuildTaskXml(string userSid, string exePath)
    {
        // As XML because schtasks' command-line options can't express everything:
        // no time limit (default: stopped after 3 days), runs on battery, normal process priority.
        var command = SecurityElement.Escape($"\"{exePath}\"");

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts Force Borderless Windowed with administrator rights when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{userSid}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{userSid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                  <Arguments>{AutostartService.StartedAtSignInArgument}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchtasks(string arguments) => RunSchtasksForOutput(arguments) is not null;

    /// <summary>Runs schtasks.exe and returns its output, or null if it failed or didn't finish in time.</summary>
    private static string? RunSchtasksForOutput(string arguments)
    {
        var startInfo = new ProcessStartInfo(SchtasksPath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // Only stdout: a redirected stream that is never read could fill up and block schtasks
            RedirectStandardOutput = true,
        };

        try
        {
            using var process = Process.Start(startInfo)!;

            // Read in the background: reading to the end would wait for schtasks without a time limit
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(SchtasksTimeout))
            {
                try
                {
                    process.Kill();
                }
                catch (InvalidOperationException)
                {
                    // Exited just now
                }

                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
