using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

using ForceBorderlessWindowed.Controls;
using ForceBorderlessWindowed.Resources;
using ForceBorderlessWindowed.Services;
using ForceBorderlessWindowed.ViewModels;

using Microsoft.Win32;

namespace ForceBorderlessWindowed.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;
    private readonly DispatcherTimer _runningRefreshTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private readonly IHotkeyService _hotkeys;

    /// <summary>Global hotkeys are off while a hotkey field has the focus; null = not suspended.</summary>
    private IDisposable? _hotkeySuspension;

    /// <summary>While a hotkey field has the focus: records combinations that other programs have; null = not recording.</summary>
    private HotkeyRecordingHook? _recordingHook;

    public SettingsWindow(SettingsViewModel viewModel, IWindowPlacementStore placementStore, IHotkeyService hotkeys)
    {
        InitializeComponent();

        // As administrator: "Administrator: " in front, like Command Prompt and PowerShell. Shows in the title bar,
        // taskbar and Alt+Tab, and screen readers read it. Set here because elevation can't change while running.
        Title = Environment.IsPrivilegedProcess ? Strings.SettingsWindow_TitleElevated : Strings.SettingsWindow_Title;
        _viewModel = viewModel;
        _hotkeys = hotkeys;
        DataContext = viewModel;

        // Save / remove game shortcuts: from the saved settings, again after every save
        ApplyShortcuts();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.SavedHotkeys))
            {
                ApplyShortcuts();
            }
            else if (e.PropertyName == nameof(SettingsViewModel.IsSavedConfirmationVisible) && viewModel.IsSavedConfirmationVisible)
            {
                AnnounceSavedConfirmation();
            }
        };

        // Recording a hotkey: pressing the current global combination must reach the field, not pause detection
        AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocusChanged), handledEventsToo: true);
        AddHandler(Keyboard.LostKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OnFocusChanged), handledEventsToo: true);
        Closed += (_, _) => SetHotkeysSuspended(false);

        // Back in the window, e.g. after closing the program that had a hotkey: try the refused ones again
        Activated += (_, _) => viewModel.Hotkeys.Recheck();

        // Without games, the Games tab is where to start: adding one is the first thing to do
        Tabs.SelectedIndex = (int)(viewModel.Games.IsEmpty ? SettingsTab.Games : SettingsTab.General);

        // Switching tabs keeps the focus on the tab header (see OnTabHeaderMouseDown)
        Tabs.PreviewMouseLeftButtonDown += OnTabHeaderMouseDown;
        Tabs.PreviewMouseLeftButtonUp += OnTabHeaderMouseUp;
        PreviewKeyDown += OnTabSwitchKey;
        AccessKeyCues.ShowOnlyWhileAltHeld(this);
        TitleBarIcon.Attach(this);
        Tabs.SelectionChanged += OnTabSelectionChanged;

        // Save with invalid input: go to the field instead of saving
        viewModel.InputErrorFound += (_, error) => GoToField(error);

        // The display list and previews need a window handle (for "display the game is on" this window's
        // monitor is the preview). SourceInitialized: the handle exists now. LocationChanged: it may have
        // moved to another monitor.
        SourceInitialized += (_, _) => viewModel.UpdateDisplays(Handle);
        LocationChanged += (_, _) => viewModel.UpdateDisplays(Handle);

        // Monitor plugged in/out or resolution changed while the window is open.
        // SystemEvents is static: without unsubscribing, it would keep this closed window alive forever.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Closed += (_, _) =>
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            viewModel.Detach();
        };

        // Running markers: refresh only while this window is open, so nothing polls in the background
        _runningRefreshTimer.Tick += async (_, _) => await viewModel.Games.RefreshRunningAsync();
        Loaded += async (_, _) =>
        {
            _runningRefreshTimer.Start();
            var autostart = viewModel.LoadAutostartAsync();
            await viewModel.Games.RefreshRunningAsync();
            await autostart;
        };
        Closed += (_, _) => _runningRefreshTimer.Stop();

        Closing += OnClosing;

        // After OnClosing: the position is only saved if the window really closes
        placementStore.Track(this, "Settings");
    }

    private nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// "Saved and applied" only becomes visible, and screen readers don't notice that by themselves.
    /// Same UIA notification as the InfoBar: read without moving the focus (which stays on Save).
    /// </summary>
    private void AnnounceSavedConfirmation()
    {
        var peer = UIElementAutomationPeer.FromElement(SavedConfirmation) ?? UIElementAutomationPeer.CreatePeerForElement(SavedConfirmation);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.ActionCompleted,
            AutomationNotificationProcessing.MostRecent,
            Strings.Settings_SavedConfirmation,
            "SavedConfirmation");
    }

    private void ApplyShortcuts()
    {
        var hotkeys = _viewModel.SavedHotkeys;

        InputBindings.Clear();
        InputBindings.Add(new InputBinding(_viewModel.SaveCommand, new HotkeyGesture(hotkeys.Save)));

        GamesList.InputBindings.Clear();
        GamesList.InputBindings.Add(new InputBinding(_viewModel.Games.RemoveCommand, new HotkeyGesture(hotkeys.RemoveGame)));
    }

    /// <summary>
    /// A click on a tab header selects the tab and focuses the header, nothing else.
    /// WPF's TabItem would move the focus into the tab's first control instead. On the Hotkeys tab that is a
    /// hotkey field, which records every key: Ctrl+S right after the click became the pause hotkey instead of
    /// saving. On the Games tab, Del right after the click removed a game.
    /// </summary>
    private void OnTabHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindTabHeader(e.OriginalSource as DependencyObject) is not { } tab)
        {
            return;   // a click in the tab's content
        }

        tab.IsSelected = true;
        tab.Focus();
        // Shows the pressed look (IsMouseCaptured trigger in SelectorTabItemStyle) until the button goes up
        tab.CaptureMouse();
        e.Handled = true;
    }

    /// <summary>Ends the pressed look that <see cref="OnTabHeaderMouseDown"/> started.</summary>
    private static void OnTabHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.Captured is TabItem tab)
        {
            tab.ReleaseMouseCapture();
        }
    }

    /// <summary>
    /// Ctrl+Tab / Ctrl+Shift+Tab (and Ctrl+PageDown / Ctrl+PageUp): next / previous tab, focus on its header.
    /// Handled here, before TabControl: it would also move the focus into the content (see above), and before a
    /// hotkey field, which would record Ctrl+Tab instead of letting the user leave the tab. Ctrl+PageUp/Down are
    /// still recorded in a hotkey field, like any other combination. A window shortcut set to one of these wins.
    /// </summary>
    private void OnTabSwitchKey(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        var inHotkeyField = Keyboard.FocusedElement is HotkeyBox;
        var step = e.Key switch
        {
            Key.Tab when modifiers == ModifierKeys.Control => 1,
            Key.Tab when modifiers == (ModifierKeys.Control | ModifierKeys.Shift) => -1,
            Key.PageDown when modifiers == ModifierKeys.Control && !inHotkeyField => 1,
            Key.PageUp when modifiers == ModifierKeys.Control && !inHotkeyField => -1,
            _ => 0,
        };

        if (step == 0 || InputBindings.OfType<InputBinding>().Any(binding => binding.Gesture?.Matches(this, e) == true))
        {
            return;
        }

        var count = Tabs.Items.Count;
        Tabs.SelectedIndex = (Tabs.SelectedIndex + step + count) % count;
        (Tabs.ItemContainerGenerator.ContainerFromIndex(Tabs.SelectedIndex) as TabItem)?.Focus();
        e.Handled = true;
    }

    /// <summary>Set while <see cref="GoToField"/> switches tabs: there the focus is meant to go to a field.</summary>
    private bool _focusFieldAfterTabChange;

    /// <summary>
    /// Safety net for tab changes that don't come from a click or Ctrl+Tab: UI Automation's "select"
    /// (Voice Access, screen reader commands) also lets WPF move the focus into the new tab's first control.
    /// Afterwards, the focus goes back to the header. The hotkey field briefly had the focus then, which is harmless.
    /// </summary>
    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged also bubbles up from the lists and combo boxes inside the tabs
        if (e.OriginalSource != Tabs)
        {
            return;
        }

        PlayTabTransition(e);

        if (_focusFieldAfterTabChange)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Tabs.IsKeyboardFocusWithin && Keyboard.FocusedElement is not TabItem
                && Tabs.ItemContainerGenerator.ContainerFromItem(Tabs.SelectedItem) is TabItem selected)
            {
                selected.Focus();
            }
        });
    }

    /// <summary>How far the new tab's content slides in (px), like WinUI's SelectorBar sample with a slide transition.</summary>
    private const double TabSlideDistance = 28;

    /// <summary>
    /// The new tab's content fades in and slides in from the side the user moves towards: from the right for a tab
    /// further right, from the left for one further left. Only the new content moves; TabControl swaps the old one
    /// out at once. Skipped when the window isn't shown yet and when animations are off in Windows
    /// (Settings → Accessibility → Visual effects). The content host clips (Styles.xaml), so nothing slides over the edge.
    /// </summary>
    private void PlayTabTransition(SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation
            || e.AddedItems.Count == 0 || e.RemovedItems.Count == 0
            || Tabs.Template.FindName("PART_SelectedContentHost", Tabs) is not ContentPresenter host)
        {
            return;
        }

        if (host.RenderTransform is not TranslateTransform transform)
        {
            transform = new TranslateTransform();
            host.RenderTransform = transform;
        }

        var forward = Tabs.Items.IndexOf(e.AddedItems[0]) > Tabs.Items.IndexOf(e.RemovedItems[0]);
        host.BeginAnimation(OpacityProperty, FluentMotion.Spline(0, 1, FluentMotion.FadeDuration));
        transform.BeginAnimation(TranslateTransform.XProperty,
            FluentMotion.Spline(forward ? TabSlideDistance : -TabSlideDistance, 0, FluentMotion.SlideDuration));
    }

    /// <summary>The tab header that contains the element; null for anything in a tab's content.</summary>
    private TabItem? FindTabHeader(DependencyObject? element)
    {
        for (var current = element; current is not null && current != Tabs;
             current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TabItem tab && ItemsControl.ItemsControlFromItemContainer(tab) == Tabs)
            {
                return tab;
            }
        }

        return null;
    }

    // The game the details last showed, and how far they were scrolled down
    private object? _detailsGame;
    private double _detailsOffset;

    /// <summary>
    /// The game details reuse one ScrollViewer for every game: without this, the next game would open at the
    /// scroll position of the previous one, with its name and status scrolled out of view.
    /// But only for a really different game: Save briefly deselects and reselects the same game (the A–Z sort
    /// moves it in the list), and the details may be rebuilt. Then the position is kept, so you can try values
    /// further down and save again without scrolling back each time.
    /// </summary>
    private void OnGameDetailsScrollDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var scroll = (ScrollViewer)sender;
        if (e.NewValue is null)
        {
            // Nothing selected for a moment (or for good): the details are hidden, keep what we know
            return;
        }

        if (ReferenceEquals(e.NewValue, _detailsGame))
        {
            // Queued by the ScrollViewer until its next layout, so this also works on a freshly built one
            scroll.ScrollToVerticalOffset(_detailsOffset);
            return;
        }

        _detailsGame = e.NewValue;
        _detailsOffset = 0;
        scroll.ScrollToTop();
    }

    private void OnGameDetailsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // ScrollChanged bubbles: ignore the inner scroll areas (text boxes, lists). And only real moves of the
        // shown game: a fresh ScrollViewer's first layout at 0 must not overwrite the position to restore.
        if (e.OriginalSource == sender && e.VerticalChange != 0
            && ReferenceEquals(((ScrollViewer)sender).DataContext, _detailsGame))
        {
            _detailsOffset = e.VerticalOffset;
        }
    }

    // Also when the window itself loses the focus (to another program): then the hotkeys must work again
    private void OnFocusChanged(object sender, KeyboardFocusChangedEventArgs e) =>
        SetHotkeysSuspended(e.NewFocus is HotkeyBox box && IsAncestorOf(box));

    private void SetHotkeysSuspended(bool suspended)
    {
        if (suspended)
        {
            // Suspended first: our own hotkeys must not count as "another program has it" in the hook
            _hotkeySuspension ??= _hotkeys.Suspend();
            _recordingHook ??= new HotkeyRecordingHook(
                _hotkeys.IsAvailable,
                hotkey => (Keyboard.FocusedElement as HotkeyBox)?.Record(hotkey));
        }
        else
        {
            _recordingHook?.Dispose();
            _recordingHook = null;
            _hotkeySuspension?.Dispose();
            _hotkeySuspension = null;
        }
    }

    // BeginInvoke: makes sure this runs on the UI thread, whatever thread SystemEvents raised it on
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => _viewModel.UpdateDisplays(Handle, displaysChanged: true));

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>About: the license texts of the libraries inside the exe (their licenses require shipping them).</summary>
    private void OnShowLicensesClick(object sender, RoutedEventArgs e) =>
        MessageDialog.ShowLongText(this, Strings.Settings_About_Licenses, AppInfo.ReadThirdPartyNotices());

    /// <summary>Set once the unsaved changes were dealt with, so the Close that follows doesn't ask again.</summary>
    private bool _closeConfirmed;

    /// <summary>
    /// Asks before unsaved changes are thrown away: Save / Don't save / Cancel.
    /// The dialog is a View concern, so it lives here; the decision data comes from the ViewModel.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // A save (with its UAC prompt) is still running: closing now would lose what it couldn't save.
        // Not while the app exits: that can't be cancelled.
        if (_viewModel.SaveCommand.IsRunning && !_viewModel.IsShuttingDown)
        {
            e.Cancel = true;
            return;
        }

        if (_closeConfirmed || !_viewModel.ShouldConfirmClose)
        {
            return;
        }

        // Saving can wait for a UAC prompt, and Closing can't wait for that: stay open, decide, and close again
        // afterwards if that's the outcome. e.Cancel must be set before the await, because the handler returns there.
        e.Cancel = true;
        if (await ResolveUnsavedChangesAsync())
        {
            _closeConfirmed = true;

            // Queued, not called directly: without a UAC prompt everything above finishes synchronously, so we
            // can still be inside Closing here, and WPF throws on Close() during Closing
            _ = Dispatcher.BeginInvoke(Close);
        }
    }

    /// <summary>
    /// Exit in the tray menu: the same question as closing this window, so edits aren't lost silently.
    /// Brings the window to the front first, the question is about what's in it.
    /// </summary>
    /// <returns>True if the app may exit; false to keep running (cancelled, or the save didn't go through).</returns>
    public async Task<bool> ConfirmExitAsync()
    {
        if (!_viewModel.HasUnsavedChanges && !_viewModel.SaveCommand.IsRunning)
        {
            return true;
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();

        // A save with its UAC prompt is still running: let it finish, the user can exit afterwards
        if (_viewModel.SaveCommand.IsRunning)
        {
            return false;
        }

        return await ResolveUnsavedChangesAsync();
    }

    /// <summary>Save / Don't save / Cancel.</summary>
    /// <returns>True if nothing unsaved is left that the user wants to keep: saved, or "Don't save".</returns>
    private async Task<bool> ResolveUnsavedChangesAsync()
    {
        var answer = MessageDialog.Show(
            this,
            Strings.Settings_UnsavedChanges_Title,
            Strings.Settings_UnsavedChanges_Message,
            primaryText: Strings.Common_Save,
            secondaryText: Strings.Common_DontSave,
            closeText: Strings.Common_Cancel);

        switch (answer)
        {
            case MessageDialogResult.Primary:
                // Invalid input: the save shows the field and doesn't happen. Declined UAC: the rest is saved, but
                // the autostart change isn't. Either way something is left, so the window stays open.
                await _viewModel.SaveCommand.ExecuteAsync(null);
                return !_viewModel.HasUnsavedChanges;

            case MessageDialogResult.Secondary:
                return true;

            default:
                return false;
        }
    }

    /// <summary>Switches to the tab (and game) of a field and puts the focus into it: an invalid one, or one to fill in.</summary>
    /// <summary>The Games tab with this game selected (from the tray's "Show details…"). Unknown IDs just show the tab.</summary>
    public void ShowGame(Guid gameId)
    {
        Tabs.SelectedIndex = (int)SettingsTab.Games;
        if (_viewModel.Games.Profiles.FirstOrDefault(p => p.Id == gameId) is { } profile)
        {
            _viewModel.Games.SelectedProfile = profile;
        }
    }

    private void GoToField(InputErrorLocation error)
    {
        // The focus goes to the field below, not to the tab header (see OnTabSelectionChanged)
        _focusFieldAfterTabChange = true;
        try
        {
            Tabs.SelectedIndex = (int)error.Tab;
        }
        finally
        {
            _focusFieldAfterTabChange = false;
        }

        if (error.Profile is { } profile)
        {
            _viewModel.Games.SelectedProfile = profile;
        }

        Func<TextBox, bool> isField = error.Field switch
        {
            // One template for all hotkey rows: told apart by the row's ViewModel
            SettingsField.Hotkey => box => box.DataContext is HotkeyEditorViewModel editor && editor.Action == error.Hotkey,
            SettingsField.ExecutableName => box => box.Name == "ExecutableBox",
            SettingsField.Width => box => box.Name == "WidthBox",
            SettingsField.Height => box => box.Name == "HeightBox",
            _ => box => box.Name == "OffsetBox",
        };

        // The tab's content (and the game's details) only exist after the next layout pass
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindVisibleField(Tabs, isField) is { } field)
            {
                field.BringIntoView();
                field.Focus();
                field.SelectAll();
            }
        });
    }

    /// <summary>
    /// The first visible text box that matches. Searched in the visual tree, because the fields live in templates
    /// and in the PlacementEditor, each with its own name scope. Only the selected tab's content is in the tree.
    /// </summary>
    private static TextBox? FindVisibleField(DependencyObject parent, Func<TextBox, bool> isField)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { IsVisible: true } box && isField(box))
            {
                return box;
            }

            if (child is UIElement { IsVisible: false })
            {
                continue;   // e.g. the other placement choices' hidden parts
            }

            if (FindVisibleField(child, isField) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// "Add a game": the running programs' windows to pick from, plus a file dialog or typing it in by hand.
    /// The adding itself stays with the ViewModel.
    /// </summary>
    private async void OnAddGameClick(object sender, RoutedEventArgs e)
    {
        // Up to date when the dialog opens: a game started a moment ago should be in it. While it is open, the
        // 3-second refresh keeps it current.
        var games = _viewModel.Games;
        await games.RefreshRunningAsync();

        var (choice, item) = AddGameDialog.Show(this, games);
        switch (choice)
        {
            case AddGameChoice.RunningProgram when item is not null:
                games.AddRunningCommand.Execute(item);
                break;
            case AddGameChoice.FromFile:
                AddFromFile();
                break;
            case AddGameChoice.Manually:
                AddManually();
                break;
        }
    }

    /// <summary>The exe name is the field that has to be filled in (the display name is optional): the focus goes there.</summary>
    private void AddManually()
    {
        var games = _viewModel.Games;
        games.AddManuallyCommand.Execute(null);
        if (games.SelectedProfile is { } profile)
        {
            GoToField(new InputErrorLocation(SettingsTab.Games, profile, SettingsField.ExecutableName));
        }
    }

    private void AddFromFile()
    {
        if (PickExecutable() is { } path)
        {
            _viewModel.Games.AddFromFile(path);
        }
    }

    private void OnBrowseExecutableClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is GameProfileViewModel profile && PickExecutable() is { } path)
        {
            _viewModel.Games.SetExecutableFromFile(profile, path);
        }
    }

    private string? PickExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = Strings.Games_Browse_Title,
            Filter = Strings.Games_Browse_Filter,
            CheckFileExists = true,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }
}
