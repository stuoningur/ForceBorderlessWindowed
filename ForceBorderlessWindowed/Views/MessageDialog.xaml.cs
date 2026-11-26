using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

using ForceBorderlessWindowed.Resources;

namespace ForceBorderlessWindowed.Views;

public enum MessageDialogResult
{
    /// <summary>The accent button (Enter).</summary>
    Primary,
    Secondary,

    /// <summary>The close button, Esc, or the window's X.</summary>
    Close,
}

/// <summary>
/// Themed replacement for MessageBox. Buttons are named after what they do ("Save", "Don't save")
/// instead of Yes/No, as the Windows guidelines ask for.
/// </summary>
public partial class MessageDialog : Window
{
    private MessageDialogResult _result = MessageDialogResult.Close;

    private MessageDialog()
    {
        InitializeComponent();
        AccessKeyCues.ShowOnlyWhileAltHeld(this);
        TitleBarIcon.Attach(this);
    }

    /// <param name="owner">Centered on and blocking this window; null = centered on the screen.</param>
    /// <param name="primaryText">Accent button, triggered by Enter. If there's no <paramref name="closeText"/>, Esc triggers it too.</param>
    /// <param name="details">Optional technical text in a selectable box, e.g. an exception message.</param>
    public static MessageDialogResult Show(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string? secondaryText = null,
        string? closeText = null,
        string? details = null) =>
        ShowCore(owner, title, message, primaryText, secondaryText, closeText, details, isLongText: false);

    /// <summary>
    /// A long text to read, e.g. license texts: a wider dialog with a tall, scrolling, selectable box, and only
    /// a Close button. No message line: the text introduces itself, a line above it would repeat that.
    /// </summary>
    public static void ShowLongText(Window? owner, string title, string text) =>
        ShowCore(owner, title, message: "", Strings.Common_Close, secondaryText: null, closeText: null, text, isLongText: true);

    private static MessageDialogResult ShowCore(
        Window? owner,
        string title,
        string message,
        string primaryText,
        string? secondaryText,
        string? closeText,
        string? details,
        bool isLongText)
    {
        var dialog = new MessageDialog();
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;

        if (!string.IsNullOrWhiteSpace(details))
        {
            dialog.DetailsText.Text = details;
            dialog.DetailsText.Visibility = Visibility.Visible;
        }

        if (isLongText)
        {
            // Wide enough for the usual 80-character lines; the text to read, not a side note, so primary color
            dialog.Width = 680;
            dialog.DetailsText.MaxHeight = 420;
            dialog.DetailsText.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        }

        if (message.Length == 0)
        {
            // The box is then named by the title ("Third-party licenses, edit")
            dialog.MessageText.Visibility = Visibility.Collapsed;
            AutomationProperties.SetLabeledBy(dialog.DetailsText, dialog.TitleText);
        }

        if (owner is { IsVisible: true })
        {
            dialog.Owner = owner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowInTaskbar = true;   // no owner: otherwise it could get lost behind other windows
        }

        var primary = dialog.AddButton(primaryText, MessageDialogResult.Primary);
        primary.IsDefault = true;
        primary.SetResourceReference(StyleProperty, "AccentButtonStyle");

        if (secondaryText is not null)
        {
            dialog.AddButton(secondaryText, MessageDialogResult.Secondary);
        }

        if (closeText is not null)
        {
            dialog.AddButton(closeText, MessageDialogResult.Close).IsCancel = true;
        }
        else
        {
            primary.IsCancel = true;
        }

        // A single button takes the right half, like in a WinUI ContentDialog: across the whole width it looked
        // like a banner rather than a button
        if (dialog.ButtonGrid.Children.Count == 1)
        {
            dialog.ButtonGrid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = new GridLength(8) });
            dialog.ButtonGrid.ColumnDefinitions.Insert(0, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(primary, 2);
        }

        dialog.Loaded += (_, _) => primary.Focus();
        dialog.ShowDialog();
        return dialog._result;
    }

    private Button AddButton(string text, MessageDialogResult result)
    {
        var index = ButtonGrid.Children.Count;
        if (index > 0)
        {
            ButtonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        }

        ButtonGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // AccessText: "_Save" → access key S. A plain string would work for normal buttons, but the Fluent
        // accent button template shows the underscore literally. The spoken name is set without it.
        var button = new Button
        {
            Content = new AccessText { Text = text },
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        AutomationProperties.SetName(button, AccessKeyConverter.StripAccessKey(text));
        button.Click += (_, _) =>
        {
            _result = result;
            DialogResult = true;
        };

        Grid.SetColumn(button, ButtonGrid.ColumnDefinitions.Count - 1);
        ButtonGrid.Children.Add(button);
        return button;
    }
}
