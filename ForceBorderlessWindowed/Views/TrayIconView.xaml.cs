using System.ComponentModel;
using System.Windows;

using ForceBorderlessWindowed.ViewModels;

using H.NotifyIcon;

using DrawingIcon = System.Drawing.Icon;

namespace ForceBorderlessWindowed.Views;

public partial class TrayIconView : TaskbarIcon
{
    private readonly TrayIconViewModel _viewModel;
    private DrawingIcon _pausedIcon;

    /// <summary>Which artwork is shown: the one for a light taskbar (dark artwork) or for a dark one.</summary>
    private bool _lightTaskbar;

    public TrayIconView(TrayIconViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _viewModel = viewModel;

        // In code instead of IconSource in XAML: IconSource loads in the background and could replace the paused
        // icon at startup. The Icon property's icon belongs to the library, which disposes it with the tray icon.
        _lightTaskbar = IsTaskbarLight();
        (Icon, _pausedIcon) = TrayIconRenderer.Create(_lightTaskbar);
        ShowPausedState();

        // Raised on the UI thread: pause changes come from settings saves, which happen there
        viewModel.PropertyChanged += OnViewModelPropertyChanged;

        CreateMenu();

        // No unsubscribing needed: the tray icon lives as long as the app
        if (Application.Current is App app)
        {
            // Deferred: the theme's colors are swapped in after ThemeMode is set, a refresh right away still found
            // the old ones
            app.ShownThemeChanged += (_, _) =>
                Dispatcher.BeginInvoke(RefreshMenuTheme, System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    /// <summary>
    /// After a theme switch: the menu isn't part of any window, and WPF only passes a theme change on to windows.
    /// The menu kept its old background (once opened, Fluent's templates keep their colors), while the game items,
    /// rebuilt on every opening, took the new text color: light text on a light menu. A new menu has the new colors.
    /// Not while it's open (the theme is switched in the settings window, so it normally isn't).
    /// </summary>
    private void RefreshMenuTheme()
    {
        if (ContextMenu is { IsOpen: true })
        {
            return;
        }

        CreateMenu();
    }

    private void CreateMenu()
    {
        var menu = (System.Windows.Controls.ContextMenu)FindResource("TrayMenu");
        menu.DataContext = _viewModel;

        // Which games run and their icons, for the Games submenu: checked each time the menu opens
        menu.Opened += (_, _) => _ = _viewModel.RefreshRunningAsync();
        ContextMenu = menu;
    }

    /// <summary>
    /// Windows changed its colors (light/dark, contrast theme): switches to the artwork for the new taskbar color.
    /// Most such changes leave the taskbar as it was, and then nothing happens.
    /// </summary>
    public void UpdateForTaskbarColor()
    {
        var lightTaskbar = IsTaskbarLight();
        if (lightTaskbar == _lightTaskbar)
        {
            return;
        }

        _lightTaskbar = lightTaskbar;
        var oldPausedIcon = _pausedIcon;
        // Setting Icon disposes the previous normal icon and shows the new one. While paused, ShowPausedState
        // replaces it with the new paused icon right away; only then is the old paused icon no longer in use.
        (Icon, _pausedIcon) = TrayIconRenderer.Create(lightTaskbar);
        ShowPausedState();
        oldPausedIcon.Dispose();
    }

    /// <summary>
    /// Whether the taskbar, where the tray icon sits, is light. It follows the "Windows mode" in Settings →
    /// Personalization → Colors, not the "app mode" the settings window follows: with "Custom" the two differ.
    /// A contrast theme colors the taskbar with its own background color instead.
    /// </summary>
    private static bool IsTaskbarLight()
    {
        if (SystemParameters.HighContrast)
        {
            var background = SystemColors.WindowColor;
            return 0.299 * background.R + 0.587 * background.G + 0.114 * background.B > 127.5;
        }

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        // Missing on older Windows 10 versions, where the taskbar was always dark
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrayIconViewModel.IsPaused))
        {
            ShowPausedState();
        }
    }

    /// <summary>
    /// Through UpdateIcon, not the Icon property: setting Icon disposes the previous icon, so switching back would
    /// show a destroyed one. UpdateIcon only hands the icon to the notification area, and the library keeps using it
    /// when the tray icon is added (ForceCreate) and when Explorer restarts.
    /// </summary>
    private void ShowPausedState() => UpdateIcon(_viewModel.IsPaused ? _pausedIcon : Icon);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        // Removes the tray icon first, then the paused icon's handle can go
        base.Dispose(disposing);
        if (disposing)
        {
            _pausedIcon.Dispose();
        }
    }
}
