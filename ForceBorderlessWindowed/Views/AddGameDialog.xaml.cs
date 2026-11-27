using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using ForceBorderlessWindowed.ViewModels;

namespace ForceBorderlessWindowed.Views;

/// <summary>What the user chose in the "Add a game" dialog.</summary>
public enum AddGameChoice
{
    Cancel,

    /// <summary>A running program's window: <see cref="AddGameDialog.SelectedItem"/>.</summary>
    RunningProgram,

    /// <summary>Pick the exe with a file dialog (the caller shows it, after this dialog is closed).</summary>
    FromFile,

    /// <summary>Type in the exe name by hand.</summary>
    Manually,
}

/// <summary>
/// "Add a game": a searchable list of the running programs' windows, plus the other ways to add one (exe file,
/// by hand). Only says what was chosen; adding is up to the caller (GamesViewModel), like before with the Add menu.
/// </summary>
public partial class AddGameDialog : Window
{
    private readonly AddGameDialogViewModel _viewModel;

    private AddGameDialog(AddGameDialogViewModel viewModel)
    {
        InitializeComponent();
        AccessKeyCues.ShowOnlyWhileAltHeld(this);
        TitleBarIcon.Attach(this);
        DataContext = _viewModel = viewModel;
        Loaded += (_, _) => SearchBox.Focus();
        Closed += (_, _) => viewModel.Dispose();
    }

    public AddGameChoice Choice { get; private set; }

    public RunningProgramItem? SelectedItem { get; private set; }

    /// <summary>Shows the dialog over <paramref name="owner"/> and returns the choice (and the window, if one was picked).</summary>
    public static (AddGameChoice Choice, RunningProgramItem? Item) Show(Window owner, GamesViewModel games)
    {
        var dialog = new AddGameDialog(new AddGameDialogViewModel(games)) { Owner = owner };
        dialog.ShowDialog();
        return (dialog.Choice, dialog.SelectedItem);
    }

    private void Finish(AddGameChoice choice)
    {
        Choice = choice;
        SelectedItem = choice == AddGameChoice.RunningProgram ? _viewModel.SelectedItem : null;
        DialogResult = choice != AddGameChoice.Cancel;
    }

    private void OnAddClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.CanAdd)
        {
            Finish(AddGameChoice.RunningProgram);
        }
    }

    private void OnFromFileClick(object sender, RoutedEventArgs e) => Finish(AddGameChoice.FromFile);

    private void OnManuallyClick(object sender, RoutedEventArgs e) => Finish(AddGameChoice.Manually);

    /// <summary>A double click on a program that isn't in the list yet adds it, like Enter.</summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(ProgramList, source) is ListBoxItem
            && _viewModel.CanAdd)
        {
            Finish(AddGameChoice.RunningProgram);
        }
    }

    /// <summary>
    /// Up and Down in the search box move the selection in the list, so typing and picking work without the mouse
    /// and without leaving the box (Enter then adds, through the default button).
    /// </summary>
    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Down or Key.Up) || _viewModel.Items.Count == 0)
        {
            return;
        }

        var index = _viewModel.SelectedItem is { } selected ? IndexOf(selected) : -1;
        index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, _viewModel.Items.Count - 1);
        _viewModel.SelectedItem = _viewModel.Items[index];
        ProgramList.ScrollIntoView(_viewModel.SelectedItem);
        e.Handled = true;
    }

    private int IndexOf(RunningProgramItem item)
    {
        for (var i = 0; i < _viewModel.Items.Count; i++)
        {
            if (ReferenceEquals(_viewModel.Items[i], item))
            {
                return i;
            }
        }

        return -1;
    }
}
