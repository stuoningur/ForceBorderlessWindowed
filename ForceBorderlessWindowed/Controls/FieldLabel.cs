using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// Label for an input field. <see cref="Label.Target"/> gives the access key ("_Width" → Alt+W focuses the field);
/// this class additionally links the field to the label for UI Automation, so screen readers announce
/// "Width, edit" instead of just "edit".
/// </summary>
public class FieldLabel : Label
{
    static FieldLabel()
    {
        TargetProperty.OverrideMetadata(typeof(FieldLabel), new FrameworkPropertyMetadata(OnTargetChanged));
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is UIElement oldTarget && AutomationProperties.GetLabeledBy(oldTarget) == d)
        {
            oldTarget.ClearValue(AutomationProperties.LabeledByProperty);
        }

        if (e.NewValue is UIElement newTarget)
        {
            AutomationProperties.SetLabeledBy(newTarget, (UIElement)d);
        }
    }
}
