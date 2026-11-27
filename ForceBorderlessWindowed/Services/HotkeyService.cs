using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

using System.Globalization;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;

using Microsoft.Extensions.Logging;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Registers the global hotkeys with Windows (RegisterHotKey) and runs their actions.
/// Windows sends WM_HOTKEY to a window, so this owns a hidden message-only window on the UI thread.
/// Re-registers whenever the saved hotkeys change.
/// </summary>
internal sealed partial class HotkeyService(
    ISettingsService settings,
    IGameIconService icons,
    IOneOffWindows oneOffWindows,
    IGameOverlay overlay,
    ILogger<HotkeyService> logger) : IHotkeyService, IDisposable
{
    private static readonly HotkeyAction[] GlobalActions =
        [HotkeyAction.TogglePause, HotkeyAction.AddForegroundGame, HotkeyAction.ApplyToForeground];

    /// <summary>
    /// Hotkey id for <see cref="IsAvailable"/>: the last one an app may use (0x0000–0xBFFF), far from the
    /// <see cref="HotkeyAction"/> values, so a WM_HOTKEY for it in that brief moment is ignored.
    /// </summary>
    private const int ProbeId = 0xBFFF;

    /// <summary>Parent for message-only windows: never visible, no taskbar entry, not in EnumWindows.</summary>
    private static readonly nint HwndMessage = -3;

    private static readonly string WindowsFolder =
        Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.Windows))
        + Path.DirectorySeparatorChar;

    private readonly ILogger<HotkeyService> _logger = logger;
    private HwndSource? _window;

    /// <summary>The hotkeys that are registered right now; null = none (not started, suspended or disposed).</summary>
    private HotkeySettings? _registered;

    private int _suspendCount;
    private HashSet<HotkeyAction> _failed = [];

    public IReadOnlySet<HotkeyAction> FailedHotkeys => _failed;

    public event EventHandler? FailedHotkeysChanged;

    /// <summary>Called by App on the UI thread once the app is up.</summary>
    public void Start()
    {
        _window = new HwndSource(new HwndSourceParameters("ForceBorderlessWindowed.Hotkeys")
        {
            ParentWindow = HwndMessage,
            WindowStyle = 0,
        });
        _window.AddHook(WndProc);

        settings.SettingsChanged += OnSettingsChanged;
        Register();
    }

    public IDisposable Suspend()
    {
        if (_suspendCount++ == 0)
        {
            UnregisterAll();
        }

        return new Suspension(this);
    }

    private void Resume()
    {
        if (--_suspendCount == 0)
        {
            Register();
        }
    }

    private sealed class Suspension(HotkeyService owner) : IDisposable
    {
        private HotkeyService? _owner = owner;

        // Only the first Dispose counts, so a double dispose can't resume someone else's suspension
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Resume();
    }

    // Every save raises this (e.g. Pause too): only re-register when the hotkeys themselves changed
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_suspendCount == 0 && settings.Current.Hotkeys != _registered)
        {
            Register();
        }
    }

    public bool IsAvailable(Hotkey hotkey)
    {
        if (_window is null)
        {
            return true;   // not started: nothing to check with
        }

        // Ours right now: registering it again would fail, but that's no conflict
        if (_registered is { } registered && GlobalActions.Any(a => !_failed.Contains(a) && registered.Get(a) == hotkey))
        {
            return true;
        }

        if (!TryRegister(ProbeId, hotkey))
        {
            var error = Marshal.GetLastPInvokeError();
            LogProbeTaken(hotkey, error);
            return false;
        }

        PInvoke.UnregisterHotKey(Handle, ProbeId);
        return true;
    }

    public void RetryFailed()
    {
        // While suspended, the resume registers them anyway
        if (_failed.Count > 0 && _suspendCount == 0)
        {
            LogRetry(_failed.Count);
            Register();
        }
    }

    private void Register()
    {
        UnregisterAll();
        if (_window is null || _suspendCount > 0)
        {
            return;
        }

        var hotkeys = settings.Current.Hotkeys;
        var failed = new HashSet<HotkeyAction>();

        foreach (var action in GlobalActions)
        {
            if (hotkeys.Get(action) is not { } hotkey)
            {
                continue;
            }

            if (!TryRegister((int)action, hotkey))
            {
                failed.Add(action);
                LogRegisterFailed(action, HotkeyRules.Format(hotkey), Marshal.GetLastPInvokeError());
            }
        }

        _registered = hotkeys;

        if (!failed.SetEquals(_failed))
        {
            _failed = failed;
            FailedHotkeysChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UnregisterAll()
    {
        if (_window is null || _registered is null)
        {
            return;
        }

        foreach (var action in GlobalActions)
        {
            // Fails harmlessly for the ones that weren't registered
            PInvoke.UnregisterHotKey(Handle, (int)action);
        }

        _registered = null;
    }

    private bool TryRegister(int id, Hotkey hotkey)
    {
        // NOREPEAT: holding the keys down toggles Pause once, not over and over
        var modifiers = ToNative(hotkey.Modifiers) | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key);
        return PInvoke.RegisterHotKey(Handle, id, modifiers, virtualKey);
    }

    private HWND Handle => (HWND)_window!.Handle;

    private static HOT_KEY_MODIFIERS ToNative(ModifierKeys modifiers)
    {
        HOT_KEY_MODIFIERS result = 0;
        if (modifiers.HasFlag(ModifierKeys.Control))
        {
            result |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        }

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            result |= HOT_KEY_MODIFIERS.MOD_ALT;
        }

        if (modifiers.HasFlag(ModifierKeys.Shift))
        {
            result |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        }

        if (modifiers.HasFlag(ModifierKeys.Windows))
        {
            result |= HOT_KEY_MODIFIERS.MOD_WIN;
        }

        return result;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == (int)PInvoke.WM_HOTKEY && Enum.IsDefined((HotkeyAction)(int)wParam))
        {
            handled = true;
            var action = (HotkeyAction)(int)wParam;

            // Queued instead of run inside the window procedure: an exception then reaches the app's
            // normal handler (error dialog + log), and the message is answered right away
            _window!.Dispatcher.BeginInvoke(() => Run(action));
        }

        return 0;
    }

    private void Run(HotkeyAction action)
    {
        LogPressed(action);
        switch (action)
        {
            case HotkeyAction.TogglePause:
                // Same as the tray toggle: tray checkmark, tooltip and settings window follow through SettingsChanged
                if (TrySave(settings.Current with { IsPaused = !settings.Current.IsPaused }))
                {
                    overlay.Show(settings.Current.IsPaused ? Strings.Overlay_Paused : Strings.Overlay_Resumed,
                        settings.Current.IsPaused ? OverlayKind.Info : OverlayKind.Success);
                }

                break;

            case HotkeyAction.AddForegroundGame:
                AddForegroundGame();
                break;

            case HotkeyAction.ApplyToForeground:
                ApplyToForeground();
                break;
        }
    }

    /// <summary>
    /// Makes the foreground window borderless just once (or puts it back), without adding it to the games. The
    /// watcher does the work on its thread; a sound says whether it worked, like for adding a game.
    /// </summary>
    private void ApplyToForeground()
    {
        if (FindForegroundProgram() is not var (window, program))
        {
            PlayFailure();
            overlay.Show(Strings.Overlay_NoProgram, OverlayKind.Warning);
            return;
        }

        var name = GameNames.GetFriendlyName(program.ExecutablePath) ?? program.ExecutableName;
        oneOffWindows.ToggleOneOff(window, result =>
        {
            LogOneOff(program.ExecutableName, result);
            var (message, kind) = result switch
            {
                OneOffResult.Added => (Formats.Overlay_OneOffAdded, OverlayKind.Success),
                OneOffResult.Restored => (Formats.Overlay_OneOffRestored, OverlayKind.Success),
                OneOffResult.AlreadyAGame => (Formats.Overlay_AlreadyAdded, OverlayKind.Info),
                OneOffResult.NoFrame => (Formats.Overlay_OneOffNoFrame, OverlayKind.Warning),
                OneOffResult.AccessDenied => (Formats.Overlay_OneOffAccessDenied, OverlayKind.Warning),
                OneOffResult.Failed => (Formats.Overlay_OneOffFailed, OverlayKind.Warning),
                _ => (Formats.Overlay_OneOffPaused, OverlayKind.Warning),
            };

            // A game's window is handled anyway: no error sound for that
            if (kind == OverlayKind.Warning)
            {
                PlayFailure();
            }
            else
            {
                PlaySuccess();
            }
            overlay.Show(string.Format(CultureInfo.CurrentCulture, message, name), kind);
        });
    }

    /// <summary>
    /// Adds the program in the foreground to the games list and saves right away; the watcher then makes it
    /// borderless. There's no window to show the result in, so a sound says whether it worked.
    /// </summary>
    private void AddForegroundGame()
    {
        if (FindForegroundProgram() is not var (_, program))
        {
            PlayFailure();
            overlay.Show(Strings.Overlay_NoProgram, OverlayKind.Warning);
            return;
        }

        // Already a game that takes this program (also a turned-off one: adding it again wouldn't turn it on).
        // A game with the same exe name but other filters (another exe file, another window title) doesn't count.
        // The "it worked" sound: the game is in the list, which is what the user wanted; the message says the rest.
        var current = settings.Current;
        if (current.Games.FirstOrDefault(g => GameMatcher.Matches(g, program.ExecutableName, program.ExecutablePath, program.WindowTitle))
            is { } existing)
        {
            LogAlreadyAdded(program.ExecutableName);
            PlaySuccess();
            overlay.Show(Format(Formats.Overlay_AlreadyAdded, existing.DisplayName ?? existing.ExecutableName), OverlayKind.Info);
            return;
        }

        var profile = GameProfile.FromRunningProgram(program, GameNames.GetFriendlyName(program.ExecutablePath));

        icons.Remember(program.ExecutableName, program.ExecutablePath);
        if (!TrySave(current with { Games = [.. current.Games, profile] }))
        {
            return;
        }

        LogAdded(program.ExecutableName);
        PlaySuccess();

        // Added while paused: it's in the list, but nothing happens to it yet
        overlay.Show(Format(current.IsPaused ? Formats.Overlay_AddedPaused : Formats.Overlay_Added, profile.DisplayName ?? profile.ExecutableName),
            OverlayKind.Success);
    }

    /// <summary>
    /// Saves for a hotkey. A hotkey is usually pressed in a game: a failed save (settings file locked, refused as
    /// administrator) is told with the failure sound and a message over the game like every other hotkey problem,
    /// not with the error dialog, which would take the focus from the game. Logged with the details.
    /// </summary>
    private bool TrySave(AppSettings newSettings)
    {
        try
        {
            settings.Save(newSettings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(ex);
            PlayFailure();
            overlay.Show(Strings.Overlay_NotSaved, OverlayKind.Warning);
            return false;
        }
    }

    // The hotkey sounds, unless turned off on the Hotkeys tab. Any thread (the one-off result arrives on the watcher's).
    private void PlaySuccess()
    {
        if (settings.Current.Hotkeys.PlaySounds)
        {
            HotkeySounds.Success();
        }
    }

    private void PlayFailure()
    {
        if (settings.Current.Hotkeys.PlaySounds)
        {
            HotkeySounds.Failure();
        }
    }

    private static string Format(System.Text.CompositeFormat format, string name) =>
        string.Format(CultureInfo.CurrentCulture, format, name);

    /// <summary>
    /// The foreground window (the one that owns a dialog or popup in front) and its program, or null for this app,
    /// Windows' own programs (Explorer, the desktop) and protected processes.
    /// </summary>
    private (nint Window, RunningProgram Program)? FindForegroundProgram()
    {
        var foreground = PInvoke.GetForegroundWindow();
        if (foreground.IsNull)
        {
            return null;
        }

        // A game's dialog or launcher popup: use the window that owns it, but never a hidden owner window
        var window = WindowQueries.GetOwningMainWindow(foreground);

        var processId = WindowQueries.GetProcessId(window);
        if (processId == 0 || processId == Environment.ProcessId)
        {
            LogNoForegroundProgram("this app or no process");
            return null;
        }

        var path = WindowQueries.GetExecutablePath(processId);
        if (path is null)
        {
            LogNoForegroundProgram("exe path not readable");
            return null;
        }

        if (path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase))
        {
            LogNoForegroundProgram("a Windows program");
            return null;
        }

        return (window, new RunningProgram((int)processId, Path.GetFileName(path), WindowQueries.GetWindowTitle(window), path));
    }

    public void Dispose()
    {
        if (_window is null)
        {
            return;
        }

        settings.SettingsChanged -= OnSettingsChanged;
        UnregisterAll();
        _window.RemoveHook(WndProc);
        _window.Dispose();
        _window = null;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotkey {Action} ({Keys}) could not be registered, error {Error}: probably used by another program")]
    private partial void LogRegisterFailed(HotkeyAction action, string keys, int error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey {Keys} is taken (checked while recording), error {Error}")]
    private partial void LogProbeTaken(Hotkey keys, int error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Trying {Count} refused hotkey(s) again")]
    private partial void LogRetry(int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey pressed: {Action}")]
    private partial void LogPressed(HotkeyAction action);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey added {Exe} to the games")]
    private partial void LogAdded(string exe);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey: {Exe} is already in the games")]
    private partial void LogAlreadyAdded(string exe);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey: one-off borderless for {Exe} -> {Result}")]
    private partial void LogOneOff(string exe, OneOffResult result);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Hotkey: the settings could not be saved, nothing changed")]
    private partial void LogSaveFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Hotkey: foreground window not added ({Reason})")]
    private partial void LogNoForegroundProgram(string reason);
}
