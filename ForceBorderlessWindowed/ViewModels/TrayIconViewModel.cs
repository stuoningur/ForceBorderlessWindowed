using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.ExceptionServices;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

public partial class TrayIconViewModel : ObservableObject
{
    private readonly IAppShell _shell;
    private readonly ISettingsService _settings;
    private readonly IGameStatusProvider _statusProvider;
    private readonly IProcessDiscoveryService _discovery;
    private readonly IGameIconService _icons;

    /// <summary>Running programs from the last time the menu opened; for "running" without a status (e.g. paused).</summary>
    private IReadOnlyList<RunningProgram> _running = [];

    /// <summary>The UI thread's context: status changes arrive on the watcher thread.</summary>
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

    public TrayIconViewModel(
        IAppShell shell, ISettingsService settings, IGameStatusProvider statusProvider,
        IProcessDiscoveryService discovery, IGameIconService icons)
    {
        _discovery = discovery;
        _icons = icons;
        _shell = shell;
        _settings = settings;
        _statusProvider = statusProvider;
        IsPaused = settings.Current.IsPaused;
        RefreshGames();

        // Pause can also be changed elsewhere ("Resume" in the settings window): keep the checkmark in sync.
        // No loop: OnIsPausedChanged only saves when the value differs from the saved one.
        // No unsubscribing needed: this ViewModel lives as long as the app, like the services.
        settings.SettingsChanged += (_, _) =>
        {
            IsPaused = settings.Current.IsPaused;

            // Queued: a click on a game's item saves from inside the menu's click handling, and replacing the items
            // (including the clicked one) right there would pull the menu item out from under WPF
            RunOnUiThread(RefreshGames);
        };
        statusProvider.StatusesChanged += (_, _) => RunOnUiThread(() =>
        {
            OnPropertyChanged(nameof(ToolTipText));
            OnPropertyChanged(nameof(HasProblem));
            RefreshGames();
        });
    }

    /// <summary>The "Games" submenu, A–Z like the Games tab (see GameOrder); running games show it with badge and status.</summary>
    public ObservableCollection<TrayGameItem> Games { get; } = [];

    /// <summary>
    /// Called when the tray menu opens: which games run (the watcher only knows the ones it handles, and nothing
    /// while paused), and their icons. The slow part (asking every process, reading icons from files) runs in the
    /// background; the menu shows right away and updates a moment later.
    /// </summary>
    public async Task RefreshRunningAsync()
    {
        var games = _settings.Current.Games;
        _running = await Task.Run(() =>
        {
            // Icons from the saved paths, also for games that aren't running; read once per file (cached)
            foreach (var game in games)
            {
                if (GameProfile.PathIfItMatches(game.ExecutablePath, game.ExecutableName) is { } path)
                {
                    _icons.Remember(game.ExecutableName.Trim(), path);
                }
            }

            var programs = _discovery.GetRunningPrograms();
            foreach (var program in programs.Where(p => games.Any(g => GameMatcher.Matches(g, p.ExecutableName, p.ExecutablePath, p.WindowTitle))))
            {
                _icons.Remember(program.ExecutableName, program.ExecutablePath);
            }

            return programs;
        });

        RefreshGames();
    }

    private void RefreshGames()
    {
        var statuses = _statusProvider.GetStatuses();
        var paused = _settings.Current.IsPaused;
        var items = _settings.Current.Games
            .Select(game =>
            {
                GameWindowStatus? status = statuses.TryGetValue(game.Id, out var s) ? s : null;
                var running = status is not null
                    || _running.Any(p => GameMatcher.Matches(game, p.ExecutableName, p.ExecutablePath, p.WindowTitle));
                var title = !string.IsNullOrWhiteSpace(game.DisplayName) ? game.DisplayName.Trim() : game.ExecutableName;
                return (Game: game, Title: title, Filter: GameMatcher.NormalizeTitle(game.WindowTitleContains),
                    Status: status, Running: running);
            })
            .Order(Comparer<(GameProfile Game, string Title, string? Filter, GameWindowStatus? Status, bool Running)>.Create(
                (a, b) => GameOrder.Compare(a.Title, a.Filter, a.Game.Id, b.Title, b.Filter, b.Game.Id)))
            .Select(entry => new TrayGameItem(
                entry.Game.Id,
                entry.Title,
                entry.Filter,
                _icons.GetIcon(entry.Game.ExecutableName.Trim()),
                ItemText(entry.Game.Enabled, entry.Running, paused, entry.Status),
                Badge(entry.Game.Enabled, entry.Running, paused, entry.Status),
                entry.Game.Enabled,
                OnGameToggled))
            .ToList();

        Games.Clear();
        foreach (var item in items)
        {
            Games.Add(item);
        }

        if (Games.Count == 0)
        {
            Games.Add(TrayGameItem.Placeholder(Strings.Tray_Games_None));
        }
    }

    /// <summary>At the right of a game: off, paused, what the watcher did, or just "running".</summary>
    private static string? ItemText(bool enabled, bool running, bool paused, GameWindowStatus? status) =>
        !enabled ? Strings.Tray_GameStatus_Off
        : running && paused ? Strings.Tray_GameStatus_Paused
        : StatusText(status) ?? (running ? Strings.Tray_GameStatus_Running : null);

    /// <summary>Same colors and glyphs as the Games list, plus grey ones for running games with nothing to report.</summary>
    private static TrayBadge Badge(bool enabled, bool running, bool paused, GameWindowStatus? status) =>
        !running ? TrayBadge.None
        : paused ? TrayBadge.Paused
        : !enabled ? TrayBadge.Neutral
        : status switch
        {
            GameWindowStatus.Borderless => TrayBadge.Success,
            GameWindowStatus.FightPaused => TrayBadge.Warning,
            GameWindowStatus.Failed or GameWindowStatus.AccessDenied => TrayBadge.Error,
            _ => TrayBadge.Neutral,
        };

    private static string? StatusText(GameWindowStatus? status) => status switch
    {
        GameWindowStatus.Borderless => Strings.Tray_GameStatus_Borderless,
        GameWindowStatus.NoFrame => Strings.Tray_GameStatus_NoFrame,
        GameWindowStatus.NotResponding => Strings.Tray_GameStatus_NotResponding,
        GameWindowStatus.FightPaused => Strings.Tray_GameStatus_FightPaused,
        GameWindowStatus.Failed => Strings.Tray_GameStatus_Failed,
        GameWindowStatus.AccessDenied => Strings.Tray_GameStatus_AccessDenied,
        _ => null,
    };

    /// <summary>
    /// A game's checkmark in the tray: saved right away, like Pause. Only that game's "enabled" changes, on top of the
    /// saved settings; an open settings window takes it over without losing its own unsaved edits.
    /// A failed save puts the checkmark back and shows the error, like a failed Pause.
    /// </summary>
    private void OnGameToggled(TrayGameItem item, bool enabled)
    {
        var current = _settings.Current;
        try
        {
            _settings.Save(current with
            {
                Games = [.. current.Games.Select(g => g.Id == item.Id ? g with { Enabled = enabled } : g)],
            });
        }
        catch (Exception ex)
        {
            RunOnUiThread(() =>
            {
                RefreshGames();
                ExceptionDispatchInfo.Throw(ex);   // App.OnDispatcherUnhandledException logs and shows it
            });
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    public partial bool IsPaused { get; set; }

    /// <summary>
    /// App name plus a short status, as the Windows guidelines suggest for notification area icons,
    /// e.g. "Force Borderless Windowed / 1 game borderless". Shown on hover, without opening the menu.
    /// As administrator the first line says so: Windows cuts tooltips off after 127 characters, and a cut
    /// should hit the end of the status, not the marker.
    /// </summary>
    public string ToolTipText =>
        $"{(Environment.IsPrivilegedProcess ? Strings.AppName_Elevated : Strings.AppName)}\n{StatusSummary}";

    private string StatusSummary
    {
        get
        {
            if (IsPaused)
            {
                return Strings.Tray_Status_Paused;
            }

            var statuses = _statusProvider.GetStatuses().Values;
            var borderless = statuses.Count(s => s == GameWindowStatus.Borderless);
            var noFrame = statuses.Count(s => s == GameWindowStatus.NoFrame);
            var waiting = statuses.Count(s => s == GameWindowStatus.NotResponding);
            // A game without a frame of its own (fullscreen or its own borderless mode) is left alone on purpose, and one
            // that isn't responding is usually loading: no problem
            var problems = statuses.Count(s => s is not (GameWindowStatus.Borderless or GameWindowStatus.NoFrame
                or GameWindowStatus.NotResponding));

            var lines = new List<string>(4);
            if (borderless > 0)
            {
                lines.Add(borderless == 1
                    ? Strings.Tray_Status_BorderlessOne
                    : string.Format(CultureInfo.CurrentCulture, Formats.Tray_Status_BorderlessMany, borderless));
            }

            // Detected, but nothing done on purpose or nothing possible yet: says so, instead of "waiting for games"
            if (noFrame > 0)
            {
                lines.Add(noFrame == 1
                    ? Strings.Tray_Status_NoFrameOne
                    : string.Format(CultureInfo.CurrentCulture, Formats.Tray_Status_NoFrameMany, noFrame));
            }

            if (waiting > 0)
            {
                lines.Add(waiting == 1
                    ? Strings.Tray_Status_WaitingOne
                    : string.Format(CultureInfo.CurrentCulture, Formats.Tray_Status_WaitingMany, waiting));
            }

            if (problems > 0)
            {
                lines.Add(problems == 1
                    ? Strings.Tray_Status_ProblemOne
                    : string.Format(CultureInfo.CurrentCulture, Formats.Tray_Status_ProblemMany, problems));
            }

            return lines.Count == 0 ? Strings.Tray_Status_Idle : string.Join("\n", lines);
        }
    }

    /// <summary>
    /// The tray toggle takes effect immediately, there is no Save button in a menu. The menu item gets here through
    /// its binding, which would swallow an exception: a failed save (settings file locked, or refused as administrator)
    /// would leave the checkmark on while nothing is paused. So the checkmark goes back to the saved state, and the
    /// error goes to the usual error message like a failed Pause from the hotkey or the settings window.
    /// </summary>
    partial void OnIsPausedChanged(bool value)
    {
        if (value == _settings.Current.IsPaused)
        {
            return;
        }

        try
        {
            _settings.Save(_settings.Current with { IsPaused = value });
        }
        catch (Exception ex)
        {
            // Queued: the binding is still busy writing the new value, and only after that does it follow a change back
            RunOnUiThread(() =>
            {
                IsPaused = _settings.Current.IsPaused;
                ExceptionDispatchInfo.Throw(ex);   // App.OnDispatcherUnhandledException logs and shows it
            });
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (_uiContext is null)
        {
            action();
        }
        else
        {
            _uiContext.Post(_ => action(), null);
        }
    }

    [RelayCommand]
    private void ShowSettings() => _shell.ShowSettingsWindow();

    /// <summary>The first game (in list order) whose window has a problem: what "Show details…" opens.</summary>
    private Guid? FirstProblemGame
    {
        get
        {
            var statuses = _statusProvider.GetStatuses();
            return _settings.Current.Games
                .Where(g => statuses.TryGetValue(g.Id, out var s)
                    && s is GameWindowStatus.FightPaused or GameWindowStatus.Failed or GameWindowStatus.AccessDenied)
                .Select(g => (Guid?)g.Id)
                .FirstOrDefault();
        }
    }

    /// <summary>A game needs attention: the menu offers "Show details…" (hidden otherwise).</summary>
    public bool HasProblem => FirstProblemGame is not null;

    /// <summary>Opens the settings window on that game, where the status bar explains the problem and what to do.</summary>
    [RelayCommand]
    private void ShowProblemDetails()
    {
        if (FirstProblemGame is { } id)
        {
            _shell.ShowGameSettings(id);
        }
        else
        {
            _shell.ShowSettingsWindow();
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => _shell.OpenLogFolder();

    [RelayCommand]
    private void OpenSettingsFolder() => _shell.OpenSettingsFolder();

    [RelayCommand]
    private void Exit() => _shell.ExitApplication();
}
