using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

public enum WindowEventKind
{
    /// <summary>A window became visible (e.g. a game just opened its window).</summary>
    Shown,

    /// <summary>A window was moved or resized (e.g. a game put its border back).</summary>
    Moved,

    /// <summary>A window was destroyed.</summary>
    Destroyed,

    /// <summary>A window became the foreground window (activated, alt-tab...).</summary>
    Foreground,

    /// <summary>A window was minimized or restored from minimized.</summary>
    MinimizeChanged,
}

/// <summary>
/// Notifies about windows appearing, moving and disappearing (SetWinEventHook).
/// "Out of context": Windows queues the events to our thread, and the callback runs in this process only.
/// No code is injected into other processes and no game memory is read or written; the app only changes
/// the styles and placement of top-level windows through normal window APIs. Whether a particular
/// anti-cheat accepts that is up to the game; it hasn't been verified for any of them.
/// Must be created, used and disposed on the same thread, and that thread needs a message loop.
/// </summary>
internal sealed class WindowEventHooks : IDisposable
{
    private readonly Action<WindowEventKind, nint> _callback;

    // Kept in a field on purpose: Windows only holds a native pointer to the delegate.
    // A delegate that only lived in a local variable could be garbage collected → crash on the next event.
    private readonly WINEVENTPROC _proc;

    private readonly List<HWINEVENTHOOK> _globalHooks = [];

    /// <summary>"Moved" hooks, one per watched process ID.</summary>
    private readonly Dictionary<uint, HWINEVENTHOOK> _moveHooks = [];

    public WindowEventHooks(Action<WindowEventKind, nint> callback)
    {
        _callback = callback;
        _proc = OnEvent;

        try
        {
            // System-wide, these are rare enough to be cheap.
            // SHOW and DESTROY are adjacent event numbers (0x8001-0x8002), so one hook covers both.
            AddGlobalHook(PInvoke.EVENT_OBJECT_DESTROY, PInvoke.EVENT_OBJECT_SHOW);

            // For mouse lock / taskbar hiding, which only apply while the game is in the foreground
            AddGlobalHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND);
            AddGlobalHook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND);   // adjacent: 0x16-0x17
        }
        catch
        {
            // The caller never gets this object, so nobody would dispose it: hooks registered before the failure
            // would stay active while _proc gets garbage collected, and the next event would call freed code.
            Dispose();
            throw;
        }
    }

    private void AddGlobalHook(uint eventMin, uint eventMax)
    {
        // SKIPOWNPROCESS: our own windows (settings, tray menu) are never games
        var hook = PInvoke.SetWinEventHook(eventMin, eventMax, HMODULE.Null, _proc, 0, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (hook.IsNull)
        {
            throw new InvalidOperationException($"SetWinEventHook failed for events 0x{eventMin:X}-0x{eventMax:X}");
        }

        _globalHooks.Add(hook);
    }

    /// <summary>
    /// Watches moves and resizes only in these processes (the running games); hooks for other processes are removed.
    /// System-wide, the "location changed" event fires for every mouse movement, text cursor and animation in
    /// every app: hundreds of wakeups per second while gaming, all of them thrown away by the filter.
    /// A game's first window is still noticed right away through "shown".
    /// </summary>
    public void WatchMovesIn(IReadOnlySet<uint> processIds)
    {
        foreach (var (processId, hook) in _moveHooks.Where(pair => !processIds.Contains(pair.Key)).ToList())
        {
            PInvoke.UnhookWinEvent(hook);
            _moveHooks.Remove(processId);
        }

        foreach (var processId in processIds.Where(id => !_moveHooks.ContainsKey(id)))
        {
            var hook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE,
                HMODULE.Null, _proc, processId, 0, PInvoke.WINEVENT_OUTOFCONTEXT);

            // Not fatal (e.g. the process just exited): the poll still catches changes, only later
            if (!hook.IsNull)
            {
                _moveHooks[processId] = hook;
            }
        }
    }

    private void OnEvent(HWINEVENTHOOK hook, uint eventId, HWND hwnd, int idObject, int idChild, uint threadId, uint time)
    {
        // These events also fire for parts of windows (caret, cursor, scrollbars, child controls...).
        // Only the window itself is interesting. This filter runs for every event we receive, keep it cheap.
        if (hwnd.IsNull || idObject != (int)OBJECT_IDENTIFIER.OBJID_WINDOW || idChild != PInvoke.CHILDID_SELF)
        {
            return;
        }

        var kind = eventId switch
        {
            PInvoke.EVENT_OBJECT_SHOW => WindowEventKind.Shown,
            PInvoke.EVENT_OBJECT_LOCATIONCHANGE => WindowEventKind.Moved,
            PInvoke.EVENT_OBJECT_DESTROY => WindowEventKind.Destroyed,
            PInvoke.EVENT_SYSTEM_FOREGROUND => WindowEventKind.Foreground,
            PInvoke.EVENT_SYSTEM_MINIMIZESTART or PInvoke.EVENT_SYSTEM_MINIMIZEEND => WindowEventKind.MinimizeChanged,
            _ => (WindowEventKind?)null,
        };

        if (kind is { } k)
        {
            _callback(k, hwnd);
        }
    }

    public void Dispose()
    {
        foreach (var hook in _globalHooks.Concat(_moveHooks.Values))
        {
            PInvoke.UnhookWinEvent(hook);
        }

        _globalHooks.Clear();
        _moveHooks.Clear();
    }
}
