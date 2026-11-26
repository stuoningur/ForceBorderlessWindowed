using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace ForceBorderlessWindowed.Controls;

public enum InfoBarSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

/// <summary>
/// In-page status message modelled after the WinUI InfoBar: severity icon + title + message,
/// an optional action on the right (<see cref="ActionContent"/>) and optional content below (Content).
/// Appears with the Fluent "direct entrance" motion and is announced by screen readers.
/// The look is an implicit style in Styles.xaml.
/// </summary>
public class InfoBar : ContentControl
{
    // Slides down into place (curve and durations: FluentMotion)
    private const double SlideDistance = -8;

    static InfoBar()
    {
        // Closed by default, like WinUI; the binding on IsOpen decides
        VisibilityProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(Visibility.Collapsed));
        FocusableProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(false));
    }

    public InfoBar()
    {
        RenderTransform = new TranslateTransform();
    }

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(InfoBar), new PropertyMetadata(false, OnIsOpenChanged));

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity), typeof(InfoBarSeverity), typeof(InfoBar), new PropertyMetadata(InfoBarSeverity.Informational));

    public InfoBarSeverity Severity
    {
        get => (InfoBarSeverity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(InfoBar), new PropertyMetadata(null, OnTextChanged));

    public string? Title
    {
        get => (string?)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message), typeof(string), typeof(InfoBar), new PropertyMetadata(null, OnTextChanged));

    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>Shown on the right, e.g. a "Resume" button.</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(InfoBar), new PropertyMetadata(null));

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>Title and message in one string, for screen readers.</summary>
    public string AccessibleText =>
        string.Join(" ", new[] { Title, Message }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (InfoBar)d;
        var isOpen = (bool)e.NewValue;
        bar.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

        if (isOpen)
        {
            bar.PlayEntrance();
            bar.Announce();
        }
    }

    // A status that changes while the bar stays open (e.g. "borderless" → "keeps resetting") is announced too
    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var bar = (InfoBar)d;
        if (bar.IsOpen)
        {
            bar.Announce();
        }
    }

    /// <summary>
    /// Only when the bar appears in an already visible window, not when a page is first shown.
    /// Skipped when the user turned off animations in Windows (Settings → Accessibility → Visual effects).
    /// </summary>
    private void PlayEntrance()
    {
        if (!IsLoaded || !SystemParameters.ClientAreaAnimation || RenderTransform is not TranslateTransform transform)
        {
            return;
        }

        BeginAnimation(OpacityProperty, FluentMotion.Spline(0, 1, FluentMotion.FadeDuration));
        transform.BeginAnimation(TranslateTransform.YProperty, FluentMotion.Spline(SlideDistance, 0, FluentMotion.SlideDuration));
    }

    /// <summary>
    /// Screen readers don't notice that a part of the window became visible by themselves.
    /// The UIA notification event makes Narrator/NVDA read the message without moving the focus.
    /// </summary>
    private void Announce()
    {
        if (!IsLoaded || string.IsNullOrEmpty(AccessibleText))
        {
            return;
        }

        var peer = UIElementAutomationPeer.FromElement(this) ?? UIElementAutomationPeer.CreatePeerForElement(this);
        peer?.RaiseNotificationEvent(
            AutomationNotificationKind.Other,
            Severity >= InfoBarSeverity.Warning
                ? AutomationNotificationProcessing.ImportantMostRecent
                : AutomationNotificationProcessing.MostRecent,
            AccessibleText,
            "InfoBar");
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new InfoBarAutomationPeer(this);

    private sealed class InfoBarAutomationPeer(InfoBar owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(InfoBar);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.StatusBar;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? ((InfoBar)Owner).AccessibleText : name;
        }

        protected override AutomationLiveSetting GetLiveSettingCore() =>
            ((InfoBar)Owner).Severity >= InfoBarSeverity.Warning ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite;
    }
}
