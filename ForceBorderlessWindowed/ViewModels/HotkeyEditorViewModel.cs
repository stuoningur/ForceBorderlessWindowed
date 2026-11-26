using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>One row on the Hotkeys tab.</summary>
public partial class HotkeyEditorViewModel : ObservableObject
{
    public HotkeyEditorViewModel(HotkeyAction action, string header, string description, string glyph, Hotkey? value)
    {
        Action = action;
        Header = header;
        Description = description;
        Glyph = glyph;
        Value = value;
    }

    public HotkeyAction Action { get; }

    public string Header { get; }

    public string Description { get; }

    /// <summary>
    /// What screen readers announce for the row: the Hotkeys tab shows the rows in an ItemsControl, which names
    /// each item by its ToString(), which would otherwise be this class's full name.
    /// </summary>
    public override string ToString() => Header;

    /// <summary>Segoe Fluent Icons glyph for the row.</summary>
    public string Glyph { get; }

    /// <summary>Global hotkeys can be turned off (✕); the window shortcuts can only be reset to their default.</summary>
    public bool IsGlobal => HotkeySettings.IsGlobal(Action);

    public bool IsWindowShortcut => !IsGlobal;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearCommand), nameof(ResetCommand))]
    public partial Hotkey? Value { get; set; }

    /// <summary>Why <see cref="Value"/> can't be saved, shown right below the field. Set by <see cref="HotkeysViewModel"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(IsWarning))]
    public partial string? Error { get; set; }

    /// <summary>
    /// A problem that doesn't block saving, e.g. another program has the combination right now.
    /// Set by <see cref="HotkeysViewModel"/>; only shown while there's no <see cref="Error"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message), nameof(IsWarning))]
    public partial string? Warning { get; set; }

    /// <summary>What's shown below the field: the error, else the warning.</summary>
    public string? Message => Error ?? Warning;

    public bool IsWarning => Error is null && Warning is not null;

    [RelayCommand(CanExecute = nameof(CanClear))]
    private void Clear() => Value = null;

    private bool CanClear() => IsGlobal && Value is not null;

    [RelayCommand(CanExecute = nameof(CanReset))]
    private void Reset() => Value = HotkeySettings.GetDefault(Action);

    private bool CanReset() => !IsGlobal && Value != HotkeySettings.GetDefault(Action);
}
