using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

using ForceBorderlessWindowed.Services;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.System.Threading;

namespace ForceBorderlessWindowed;

/// <summary>What <see cref="SingleInstance.TryHandOverFromOlderVersion"/> did.</summary>
internal enum HandoverResult
{
    /// <summary>The running instance isn't an older version, or it can't be told: bring up its window as usual.</summary>
    NotNewer,

    /// <summary>The older version has exited: this one can take its place.</summary>
    Done,

    /// <summary>
    /// The older version stays: the user picked Cancel when it asked about unsaved changes (its window is open then),
    /// or it didn't exit in time.
    /// </summary>
    Declined,
}

internal sealed class SingleInstance : IDisposable
{
    private const string Id = "ForceBorderlessWindowed-0D419D79-05C7-4AC8-9722-FB92063ADE02";
    private const string MutexName = $@"Local\{Id}";
    private const string ActivateEventName = $@"Local\{Id}-Activate";

    /// <summary>A newer version asks this one to exit (see <see cref="TryHandOverFromOlderVersion"/>).</summary>
    private const string ExitEventName = $@"Local\{Id}-Exit";

    /// <summary>This one's answer when it stays after all (the user cancelled the exit).</summary>
    private const string ExitDeclinedEventName = $@"Local\{Id}-ExitDeclined";

    /// <summary>
    /// Exists while a newer version waits for this one to exit (it holds it open, it is never signaled). The older
    /// one looks for it right before exiting: if the newer one has given up meanwhile, exiting would leave no
    /// instance running at all, so it stays (see <see cref="IsNewerVersionWaiting"/>).
    /// </summary>
    private const string HandoverWaitingEventName = $@"Local\{Id}-HandoverWaiting";

    /// <summary>
    /// How long a newer version waits for the older one to exit. It may be asking about unsaved changes, so not too
    /// short; but if it hangs, the new one (with no window yet) shouldn't wait invisibly forever.
    /// </summary>
    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// After giving up: how long the newer version still waits for an exit already under way. The older one may have
    /// seen it waiting just before it gave up; its exit (putting the game windows back) takes a few seconds at most.
    /// </summary>
    private static readonly TimeSpan HandoverGracePeriod = TimeSpan.FromSeconds(10);

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activateEvent;
    private readonly EventWaitHandle _exitEvent;
    private readonly EventWaitHandle _exitDeclinedEvent;
    private RegisteredWaitHandle? _activateRegistration;
    private RegisteredWaitHandle? _exitRegistration;

    private SingleInstance(Mutex mutex, EventWaitHandle activateEvent, EventWaitHandle exitEvent, EventWaitHandle exitDeclinedEvent)
    {
        _mutex = mutex;
        _activateEvent = activateEvent;
        _exitEvent = exitEvent;
        _exitDeclinedEvent = exitDeclinedEvent;
    }

    /// <summary>Returns null if another instance is already running.</summary>
    /// <remarks>
    /// The lock and the events are created with permissions for this user. Without them they get the default
    /// permissions of the creating process, and when that one runs as administrator (elevated autostart task,
    /// "Restart as administrator") only administrators and SYSTEM may open them: a normal start of the app would
    /// then crash here instead of bringing up the running one. Only the two rights that checking and signaling need.
    /// </remarks>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            // Another instance first, opened with exactly those rights. MutexAcl.Create would ask for full control
            // of an existing lock, which isn't granted, and fail.
            if (Mutex.TryOpenExisting(MutexName, out var existing))
            {
                existing.Dispose();
                return null;
            }

            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException("The current user's SID is not available.");

            var mutexSecurity = new MutexSecurity();
            mutexSecurity.AddAccessRule(new MutexAccessRule(
                user, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            var mutex = MutexAcl.Create(initiallyOwned: true, MutexName, out bool createdNew, mutexSecurity);
            if (!createdNew)
            {
                mutex.Dispose();
                return null;
            }

            return new SingleInstance(
                mutex,
                CreateEvent(ActivateEventName, user),
                CreateEvent(ExitEventName, user),
                CreateEvent(ExitDeclinedEventName, user));
        }
        catch (UnauthorizedAccessException)
        {
            // It exists, but we may not open it: an older version running as administrator, or an instance that
            // started at the same moment (MutexAcl.Create found its lock). Either way, another instance runs.
            return null;
        }
    }

    private static EventWaitHandle CreateEvent(string name, SecurityIdentifier user)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(
            user, EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    public static void SignalFirstInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activateEvent))
            {
                using (activateEvent)
                {
                    AllowFirstInstanceToForeground();
                    activateEvent.Set();
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An older version running as administrator: its event can't be reached from here. Nothing to show
            // then, but no crash; the running app is in the tray.
        }
    }

    /// <summary>
    /// The user started this exe while another instance runs. If that one is an older version of the app, it's
    /// asked to exit, like Exit in its tray menu (so it first asks about unsaved changes), and this waits until it
    /// has. Without this, a new version would only bring up the old one's window, and the update would never run.
    /// </summary>
    /// <param name="olderVersion">The version that was asked to exit, or null if none was.</param>
    public static HandoverResult TryHandOverFromOlderVersion(out Version? olderVersion)
    {
        olderVersion = null;
        if (FindRunningInstance() is not { } running
            || AppInfo.GetVersionOfCopy(running.ExePath) is not { } version
            || version >= AppInfo.FileVersion)
        {
            return HandoverResult.NotNewer;
        }

        try
        {
            // A version from before the handover existed doesn't have these: it can only be brought up
            if (!EventWaitHandle.TryOpenExisting(ExitEventName, out var exitEvent))
            {
                return HandoverResult.NotNewer;
            }

            using (exitEvent)
            using (var exitDeclinedEvent = EventWaitHandle.OpenExisting(ExitDeclinedEventName))
            using (var processExit = OpenProcessExit(running.ProcessId))
            {
                if (processExit is null)
                {
                    return HandoverResult.Done;   // exited on its own just now
                }

                // Without the "still waiting" sign the older one would stay after the user agreed to exit: then
                // it's brought up instead, as for a version from before the handover
                var waiting = CreateWaitingEvent();
                if (waiting is null)
                {
                    return HandoverResult.NotNewer;
                }

                try
                {
                    olderVersion = version;
                    exitDeclinedEvent.Reset();   // left over from a request nobody waited for anymore
                    PInvoke.AllowSetForegroundWindow((uint)running.ProcessId);   // for its "Save your changes?" question
                    exitEvent.Set();
                    switch (WaitHandle.WaitAny([processExit, exitDeclinedEvent], HandoverTimeout))
                    {
                        case 0:
                            return HandoverResult.Done;
                        case 1:
                            return HandoverResult.Declined;
                    }
                }
                finally
                {
                    waiting.Dispose();   // from now on the older one stays instead of exiting
                }

                // Timed out: an exit the older one decided on just before can still be under way
                return processExit.WaitOne(HandoverGracePeriod) ? HandoverResult.Done : HandoverResult.Declined;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // Not reachable from here (or gone meanwhile): bring it up as usual
            return HandoverResult.NotNewer;
        }
    }

    /// <summary>The "still waiting" sign for the older version; null if it couldn't be created.</summary>
    private static EventWaitHandle? CreateWaitingEvent()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User is { } user ? CreateEvent(HandoverWaitingEventName, user) : null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            return null;
        }
    }

    /// <summary>
    /// For the older version, right before it exits for a newer one: true if that one is still waiting to take over.
    /// When unsure (it exists, but can't be opened from here), true: exiting is what was asked for.
    /// </summary>
    public static bool IsNewerVersionWaiting()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(HandoverWaitingEventName, out var waiting))
            {
                return false;
            }

            waiting.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The process of the running instance and its exe. Found by name: usually the same file name as this one, and
    /// the admin autostart copy always has the original name. Null if none or several are found (e.g. one just
    /// starting up): then it can't be told which one is the instance.
    /// </summary>
    private static (int ProcessId, string ExePath)? FindRunningInstance()
    {
        using var self = Process.GetCurrentProcess();
        (int, string)? found = null;

        foreach (var name in new[] { self.ProcessName, AppInfo.AssemblyName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var other in Process.GetProcessesByName(name))
            {
                using (other)
                {
                    if (other.Id == self.Id
                        || other.SessionId != self.SessionId
                        || WindowQueries.GetExecutablePath((uint)other.Id) is not { } path
                        || AppInfo.GetVersionOfCopy(path) is null)
                    {
                        continue;
                    }

                    if (found is not null)
                    {
                        return null;
                    }

                    found = (other.Id, path);
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A handle that becomes signaled when the process exits, or null if it already has. Only the right to wait:
    /// that's also granted for a process running as administrator.
    /// </summary>
    private static unsafe ProcessExitWaitHandle? OpenProcessExit(int processId)
    {
        var handle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_SYNCHRONIZE, false, (uint)processId);
        return handle.IsNull ? null : new ProcessExitWaitHandle(new SafeWaitHandle((nint)handle.Value, ownsHandle: true));
    }

    private sealed class ProcessExitWaitHandle : WaitHandle
    {
        public ProcessExitWaitHandle(SafeWaitHandle handle) => SafeWaitHandle = handle;
    }

    /// <summary>
    /// The second instance has foreground rights because the user just started it; this passes them on, so the
    /// first instance may bring its settings window to the front. Only to that process (found by name, in this
    /// session), not to every process as ASFW_ANY would.
    /// </summary>
    private static void AllowFirstInstanceToForeground()
    {
        // Both names, like FindRunningInstance: this exe may be renamed, the admin autostart copy never is
        using var self = Process.GetCurrentProcess();
        foreach (var name in new[] { self.ProcessName, AppInfo.AssemblyName }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var other in Process.GetProcessesByName(name))
            {
                using (other)
                {
                    if (other.Id != self.Id && other.SessionId == self.SessionId)
                    {
                        PInvoke.AllowSetForegroundWindow((uint)other.Id);
                    }
                }
            }
        }
    }

    public void ListenForActivation(Action onActivate) => _activateRegistration = Listen(_activateEvent, onActivate);

    /// <summary>A newer version asks this one to exit. If it stays after all, call <see cref="DeclineExit"/>.</summary>
    public void ListenForExitRequest(Action onRequested) => _exitRegistration = Listen(_exitEvent, onRequested);

    /// <summary>Tells the waiting newer version that this instance stays (the user cancelled the exit).</summary>
    public void DeclineExit() => _exitDeclinedEvent.Set();

    // The callback runs on a thread-pool thread
    private static RegisteredWaitHandle Listen(EventWaitHandle signal, Action callback) =>
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => callback(), state: null, Timeout.Infinite, executeOnlyOnce: false);

    public void Dispose()
    {
        _activateRegistration?.Unregister(null);
        _exitRegistration?.Unregister(null);
        _activateEvent.Dispose();
        _exitEvent.Dispose();
        _exitDeclinedEvent.Dispose();
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
