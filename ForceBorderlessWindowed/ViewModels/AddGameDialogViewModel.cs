using System.ComponentModel;
using System.Globalization;

using CommunityToolkit.Mvvm.ComponentModel;

using ForceBorderlessWindowed.Resources;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>
/// The "Add a game" dialog: the running programs' windows, filtered by a search text, to pick the game from. Follows
/// the Games tab's running list while it is open (refreshed every few seconds), keeping the selection.
/// </summary>
public sealed partial class AddGameDialogViewModel : ObservableObject, IDisposable
{
    private readonly GamesViewModel _games;

    public AddGameDialogViewModel(GamesViewModel games)
    {
        _games = games;
        _games.PropertyChanged += OnGamesChanged;
        UpdateItems();
    }

    /// <summary>Filters by window title and exe name, case-insensitive; empty = everything.</summary>
    [ObservableProperty]
    public partial string SearchText { get; set; } = "";

    /// <summary>The running programs that match <see cref="SearchText"/>, in the Games tab's order (by title).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyText), nameof(HasItems))]
    public partial IReadOnlyList<RunningProgramItem> Items { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanAdd))]
    public partial RunningProgramItem? SelectedItem { get; set; }

    /// <summary>A program that isn't in the list yet is selected.</summary>
    public bool CanAdd => SelectedItem is { IsAlreadyAdded: false };

    public bool HasItems => Items.Count > 0;

    /// <summary>Shown instead of the list when it is empty: nothing runs, or nothing matches the search.</summary>
    public string EmptyText =>
        _games.RunningPrograms.Count == 0
            ? Strings.Games_NoRunningPrograms
            : string.Format(CultureInfo.CurrentCulture, Formats.Games_Add_NoMatch, SearchText.Trim());

    partial void OnSearchTextChanged(string value) => UpdateItems();

    private void OnGamesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GamesViewModel.RunningPrograms))
        {
            UpdateItems();
        }
    }

    private void UpdateItems()
    {
        var search = SearchText.Trim();
        var selectedProcess = SelectedItem?.Program.ProcessId;

        Items = [.. _games.RunningPrograms.Where(item => search.Length == 0
            || item.Program.WindowTitle.Contains(search, StringComparison.CurrentCultureIgnoreCase)
            || item.Program.ExecutableName.Contains(search, StringComparison.CurrentCultureIgnoreCase))];

        // The same program stays selected across refreshes (a new item object each time). Nothing is picked for the
        // user on open: Add only gets enabled by a choice. After typing a search, the first match that can be added
        // is selected, so Enter right after typing adds the best match of what the user asked for.
        SelectedItem = Items.FirstOrDefault(i => i.Program.ProcessId == selectedProcess)
            ?? (search.Length == 0 ? null : Items.FirstOrDefault(i => !i.IsAlreadyAdded) ?? (Items.Count > 0 ? Items[0] : null));
    }

    public void Dispose() => _games.PropertyChanged -= OnGamesChanged;
}
