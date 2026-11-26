using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using ForceBorderlessWindowed.Models;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>The "Hotkeys" tab: global hotkeys and the settings window's shortcuts. Applied on Save like the other tabs.</summary>
public partial class HotkeysViewModel : ObservableObject
{
    private readonly IHotkeyService _service;
    private HotkeySettings _saved;

    public HotkeysViewModel(HotkeySettings saved, IHotkeyService service)
    {
        _service = service;
        _saved = saved;
        _showMessages = saved.ShowMessages;
        _playSounds = saved.PlaySounds;

        Global =
        [
            new(HotkeyAction.TogglePause, Strings.Hotkeys_TogglePause, Strings.Hotkeys_TogglePause_Description,
                "", saved.TogglePause),   // Pause
            new(HotkeyAction.AddForegroundGame, Strings.Hotkeys_AddForegroundGame, Strings.Hotkeys_AddForegroundGame_Description,
                "", saved.AddForegroundGame),   // Add
            new(HotkeyAction.ApplyToForeground, Strings.Hotkeys_ApplyToForeground, Strings.Hotkeys_ApplyToForeground_Description,
                "\uE740", saved.ApplyToForeground),   // FullScreen
        ];
        WindowShortcuts =
        [
            new(HotkeyAction.Save, Strings.Hotkeys_Save, Strings.Hotkeys_Save_Description,
                "", saved.Save),   // Save
            new(HotkeyAction.RemoveGame, Strings.Hotkeys_RemoveGame, Strings.Hotkeys_RemoveGame_Description,
                "", saved.RemoveGame),   // Delete
        ];

        foreach (var editor in All)
        {
            editor.PropertyChanged += OnEditorChanged;
        }

        _service.FailedHotkeysChanged += OnFailedHotkeysChanged;
        Validate();
        UpdateWarnings();
    }

    /// <summary>Raised when a hotkey was changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<HotkeyEditorViewModel> Global { get; }

    public IReadOnlyList<HotkeyEditorViewModel> WindowShortcuts { get; }

    private IEnumerable<HotkeyEditorViewModel> All => Global.Concat(WindowShortcuts);

    public HotkeySettings ToSettings() =>
        All.Aggregate(new HotkeySettings { ShowMessages = ShowMessages, PlaySounds = PlaySounds },
            (settings, editor) => settings.With(editor.Action, editor.Value));

    private bool _playSounds;

    /// <summary>Sounds for the global hotkeys (see HotkeySounds). Saved with the hotkeys.</summary>
    public bool PlaySounds
    {
        get => _playSounds;
        set
        {
            if (SetProperty(ref _playSounds, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    private bool _showMessages;

    /// <summary>Messages over the game for the global hotkeys (see IGameOverlay). Saved with the hotkeys.</summary>
    public bool ShowMessages
    {
        get => _showMessages;
        set
        {
            if (SetProperty(ref _showMessages, value))
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>The first row that can't be saved, in the order shown, or null if all are fine.</summary>
    public InputErrorLocation? FindFirstInputError() =>
        All.FirstOrDefault(e => e.Error is not null) is { } editor
            ? new InputErrorLocation(SettingsTab.Hotkeys, null, SettingsField.Hotkey, editor.Action)
            : null;

    /// <summary>
    /// Saved global hotkeys that Windows refused (another program has them), naming each one; null if all work.
    /// A row that was changed since isn't listed: the message is about the saved combination.
    /// </summary>
    public string? RegistrationProblem
    {
        get
        {
            var failed = Global
                .Where(e => _service.FailedHotkeys.Contains(e.Action) && e.Value is not null && e.Value == _saved.Get(e.Action))
                .Select(e => $"{e.Header} ({HotkeyRules.Format(e.Value!)})")
                .ToList();

            return failed.Count switch
            {
                0 => null,
                1 => string.Format(CultureInfo.CurrentCulture, Formats.Hotkeys_RegistrationFailedOne, failed[0]),
                _ => string.Format(CultureInfo.CurrentCulture, Formats.Hotkeys_RegistrationFailedMany, string.Join(", ", failed)),
            };
        }
    }

    public bool HasRegistrationProblem => RegistrationProblem is not null;

    /// <summary>After a save: the registration message now compares against the new saved hotkeys.</summary>
    public void MarkSaved(HotkeySettings saved)
    {
        _saved = saved;
        OnRegistrationProblemChanged();
        UpdateWarnings();
    }

    /// <summary>
    /// When the settings window is activated: the refused hotkeys are tried again (the other program may be gone,
    /// then the message disappears) and the rows are checked again.
    /// </summary>
    public void Recheck()
    {
        _service.RetryFailed();
        UpdateWarnings();
    }

    /// <summary>
    /// Problems that don't block saving, shown below the row. Another program has the combination right now (it
    /// may be closed later), or it's a shortcut programs commonly use themselves (global hotkeys only: a window
    /// shortcut only applies in this window).
    /// </summary>
    private void UpdateWarnings()
    {
        foreach (var editor in All)
        {
            editor.Warning = editor.Error is null && editor.Value is { } hotkey
                ? IsTakenByOtherProgram(editor, hotkey) ? Strings.Hotkeys_Warning_InUse
                    : editor.IsGlobal && HotkeyRules.IsCommonShortcut(hotkey) ? Strings.Hotkeys_Warning_CommonShortcut
                    : null
                : null;
        }
    }

    /// <summary>
    /// A saved global hotkey isn't checked: the message at the top covers it, and it is registered by us.
    /// A window shortcut that another program has as a global hotkey never reaches this window either.
    /// </summary>
    private bool IsTakenByOtherProgram(HotkeyEditorViewModel editor, Hotkey hotkey) =>
        (!editor.IsGlobal || hotkey != _saved.Get(editor.Action)) && !_service.IsAvailable(hotkey);

    /// <summary>The service lives as long as the app; see <see cref="SettingsViewModel.Detach"/>.</summary>
    public void Detach() => _service.FailedHotkeysChanged -= OnFailedHotkeysChanged;

    private void OnFailedHotkeysChanged(object? sender, EventArgs e) => OnRegistrationProblemChanged();

    private void OnRegistrationProblemChanged()
    {
        OnPropertyChanged(nameof(RegistrationProblem));
        OnPropertyChanged(nameof(HasRegistrationProblem));
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(HotkeyEditorViewModel.Value))
        {
            return;
        }

        Validate();
        UpdateWarnings();
        OnRegistrationProblemChanged();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Each row's error: a combination that isn't allowed for it, or one that another row has too.</summary>
    private void Validate()
    {
        foreach (var editor in All)
        {
            if (editor.Value is not { } hotkey)
            {
                editor.Error = null;
                continue;
            }

            var other = All.FirstOrDefault(o => o != editor && o.Value == hotkey);
            editor.Error = HotkeyRules.Check(editor.Action, hotkey) switch
            {
                HotkeyProblem.NeedsModifier => Strings.Hotkeys_Error_NeedsModifier,
                HotkeyProblem.NavigationKey => Strings.Hotkeys_Error_NavigationKey,
                HotkeyProblem.Invalid => Strings.Hotkeys_Error_Invalid,
                _ when other is not null => string.Format(CultureInfo.CurrentCulture, Formats.Hotkeys_Error_Duplicate, other.Header),
                _ => null,
            };
        }
    }
}
