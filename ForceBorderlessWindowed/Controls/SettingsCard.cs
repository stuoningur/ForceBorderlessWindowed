using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// One setting as a row, like in the Windows 11 Settings app: icon, header and description on the left,
/// the control (Content) on the right. The look is an implicit style in Styles.xaml.
/// The header is a Label targeting the control: screen readers announce the control with the header's text,
/// and an access key in the header ("_Theme" → Alt+T) moves the focus to the control.
/// If the control is a CheckBox, clicking anywhere on the row toggles it.
/// </summary>
public class SettingsCard : ContentControl
{
    static SettingsCard()
    {
        FocusableProperty.OverrideMetadata(typeof(SettingsCard), new FrameworkPropertyMetadata(false));
    }

    /// <summary>Segoe Fluent Icons glyph, e.g. "&#xE790;".</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public string? Glyph
    {
        get => (string?)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    /// <summary>May contain an access key marker ("_Theme").</summary>
    public string? Header
    {
        get => (string?)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public static readonly DependencyProperty AlternateDescriptionProperty = DependencyProperty.Register(
        nameof(AlternateDescription), typeof(string), typeof(SettingsCard), new PropertyMetadata(null));

    /// <summary>
    /// Shown instead of <see cref="Description"/> while <see cref="IsAlternateDescriptionShown"/>, e.g. how a hotkey
    /// field works while it records. Both share one cell sized for the longer one, so the row never changes height.
    /// </summary>
    public string? AlternateDescription
    {
        get => (string?)GetValue(AlternateDescriptionProperty);
        set => SetValue(AlternateDescriptionProperty, value);
    }

    public static readonly DependencyProperty IsAlternateDescriptionShownProperty = DependencyProperty.Register(
        nameof(IsAlternateDescriptionShown), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false));

    public bool IsAlternateDescriptionShown
    {
        get => (bool)GetValue(IsAlternateDescriptionShownProperty);
        set => SetValue(IsAlternateDescriptionShownProperty, value);
    }

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(SettingsCard), new PropertyMetadata(new CornerRadius(4)));

    /// <summary>Changed for cards that are stacked into one group (see SettingsCardGroupTop/Bottom styles).</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static readonly DependencyPropertyKey IsClickablePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsClickable), typeof(bool), typeof(SettingsCard), new PropertyMetadata(false));

    public static readonly DependencyProperty IsClickableProperty = IsClickablePropertyKey.DependencyProperty;

    /// <summary>True when the content is a CheckBox: the whole row then toggles it (and shows a hover effect).</summary>
    public bool IsClickable => (bool)GetValue(IsClickableProperty);

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        SetValue(IsClickablePropertyKey, newContent is CheckBox);

        // The description is drawn by the template, and text in a template isn't part of what screen readers see.
        // As the control's help text, it is read after the control's name ("Theme, combo box, Light, dark, or …").
        // Only for a control (a panel has no automation peer of its own) that has no help text of its own.
        if (oldContent is Control oldControl
            && BindingOperations.GetBindingExpression(oldControl, AutomationProperties.HelpTextProperty)?.ParentBinding.Source == this)
        {
            BindingOperations.ClearBinding(oldControl, AutomationProperties.HelpTextProperty);
        }

        if (newContent is Control control
            && control.ReadLocalValue(AutomationProperties.HelpTextProperty) == DependencyProperty.UnsetValue)
        {
            BindingOperations.SetBinding(control, AutomationProperties.HelpTextProperty,
                new Binding(nameof(Description)) { Source = this });
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        // Clicks on the CheckBox itself are handled by the CheckBox
        if (e.Handled || Content is not ToggleButton toggle || !toggle.IsEnabled || IsInside(e.OriginalSource, toggle))
        {
            return;
        }

        toggle.IsChecked = toggle.IsChecked != true;
        toggle.Focus();
        e.Handled = true;
    }

    private static bool IsInside(object source, DependencyObject ancestor)
    {
        for (var current = source as DependencyObject; current is not null; current = GetParent(current))
        {
            if (current == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    // Text runs etc. are not Visuals: VisualTreeHelper would throw for them
    private static DependencyObject? GetParent(DependencyObject element) =>
        element is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(element)
            : LogicalTreeHelper.GetParent(element);
}
