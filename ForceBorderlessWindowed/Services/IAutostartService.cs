namespace ForceBorderlessWindowed.Services;

public enum AutostartMode
{
    Off,

    /// <summary>Registry "Run" entry: starts without administrator rights.</summary>
    Normal,

    /// <summary>
    /// Task Scheduler task with "highest privileges": starts a copy of the app in Program Files
    /// as administrator without a UAC prompt.
    /// </summary>
    Elevated,
}

/// <summary>What <see cref="IAutostartService.SetModeAsync"/> could do.</summary>
public enum AutostartChangeResult
{
    Changed,

    /// <summary>The UAC prompt was declined, or the change failed or timed out (logged). Nothing was changed.</summary>
    NotChanged,

    /// <summary>
    /// "Start as administrator" couldn't be set up: the app's copy in Program Files is running for another
    /// Windows user right now, and a running copy can't be replaced. Nothing was changed.
    /// </summary>
    CopyInUse,
}

/// <summary>Registers the app to start when the user logs on to Windows.</summary>
public interface IAutostartService
{
    /// <summary>
    /// False if the signed-in user isn't an administrator. A task "with highest privileges" would then run with
    /// the user's normal rights, even if another (admin) account approved setting it up: no point offering it.
    /// </summary>
    bool CanStartElevated { get; }

    /// <summary>
    /// The current registration for this exe. The Run key is read from the registry every time, because the
    /// user can change it outside the app (Task Manager). The elevated task is checked once in the background
    /// at startup (it needs schtasks.exe) and then tracked by <see cref="SetModeAsync"/>.
    /// Async: right after startup that check may still be running.
    /// </summary>
    Task<AutostartMode> GetModeAsync();

    /// <summary>
    /// Changes the registration. Creating or deleting the elevated task needs administrator rights, so that
    /// shows a UAC prompt (unless the app already runs as admin).
    /// </summary>
    Task<AutostartChangeResult> SetModeAsync(AutostartMode mode);

    /// <summary>
    /// True if "start as administrator" is set up, but its copy in Program Files is an older version than this
    /// exe (or missing): the next sign-in would start the old version. Happens after an update.
    /// </summary>
    Task<bool> IsElevatedCopyOutdatedAsync();

    /// <summary>Replaces the copy in Program Files with this exe. Shows a UAC prompt, unless the app already runs as admin.</summary>
    Task<AutostartChangeResult> UpdateElevatedCopyAsync();
}
