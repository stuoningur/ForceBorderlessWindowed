using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// The error of an input field, shown right below it, as the Windows forms guidance asks for.
/// Hidden while <see cref="Text"/> is empty. While it isn't, the <see cref="Target"/> field gets a red border,
/// and the error is put in front of the field's help text, so screen readers read it together with the field.
/// The field's own help text comes back unchanged when the error is gone.
/// </summary>
public class FieldError : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(FieldError), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(Control), typeof(FieldError), new PropertyMetadata(null, OnTargetChanged));

    /// <summary>A warning instead of an error (doesn't block saving): caution color and warning glyph.</summary>
    public static readonly DependencyProperty IsWarningProperty = DependencyProperty.Register(
        nameof(IsWarning), typeof(bool), typeof(FieldError), new PropertyMetadata(false, OnChanged));

    public FieldError()
    {
        Visibility = Visibility.Collapsed;
    }

    public bool IsWarning
    {
        get => (bool)GetValue(IsWarningProperty);
        set => SetValue(IsWarningProperty, value);
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>The field this error belongs to.</summary>
    public Control? Target
    {
        get => (Control?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is Control oldTarget)
        {
            ClearMarks(oldTarget);
        }

        OnChanged(d, e);
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var error = (FieldError)d;
        var hasError = !string.IsNullOrEmpty(error.Text);
        error.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;

        if (error.Target is not { } target)
        {
            return;
        }

        if (hasError)
        {
            // The field's own help text (e.g. how a shortcut field works) is kept the first time and read after
            // the error, then put back as it was (also a binding) once the error is gone
            if (target.ReadLocalValue(OriginalHelpTextProperty) == DependencyProperty.UnsetValue)
            {
                target.SetValue(OriginalHelpTextProperty, new SavedHelpText(
                    target.ReadLocalValue(AutomationProperties.HelpTextProperty), AutomationProperties.GetHelpText(target)));
            }

            var ownHelp = ((SavedHelpText)target.GetValue(OriginalHelpTextProperty)).Text;
            target.SetResourceReference(BorderBrushProperty, error.IsWarning ? "SystemFillColorCautionBrush" : "SystemFillColorCriticalBrush");
            AutomationProperties.SetHelpText(target, string.IsNullOrEmpty(ownHelp) ? error.Text : $"{error.Text} {ownHelp}");
        }
        else
        {
            ClearMarks(target);
        }
    }

    /// <summary>Removes the red border and the error from the help text; leaves an unmarked field untouched.</summary>
    private static void ClearMarks(Control target)
    {
        if (target.ReadLocalValue(OriginalHelpTextProperty) is not SavedHelpText saved)
        {
            return;
        }

        target.ClearValue(BorderBrushProperty);
        target.ClearValue(OriginalHelpTextProperty);

        switch (saved.LocalValue)
        {
            case BindingExpressionBase expression:
                BindingOperations.SetBinding(target, AutomationProperties.HelpTextProperty, expression.ParentBindingBase);
                break;
            case var value when value == DependencyProperty.UnsetValue:
                target.ClearValue(AutomationProperties.HelpTextProperty);
                break;
            case var value:
                target.SetValue(AutomationProperties.HelpTextProperty, value);
                break;
        }
    }

    /// <summary>The target's help text before the error was added: as set (value or binding), and as text.</summary>
    private sealed record SavedHelpText(object LocalValue, string? Text);

    /// <summary>Set on the target while it is marked as invalid.</summary>
    private static readonly DependencyProperty OriginalHelpTextProperty = DependencyProperty.RegisterAttached(
        "OriginalHelpText", typeof(SavedHelpText), typeof(FieldError), new PropertyMetadata(null));
}
