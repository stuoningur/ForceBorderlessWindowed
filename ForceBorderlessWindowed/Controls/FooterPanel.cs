using System.Windows;
using System.Windows.Controls;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// Footer layout: actions on the right, messages to their left. If the messages would get narrower than
/// <see cref="MinInlineWidth"/> there (narrow window, long German text, "Saved" next to the buttons), they move to
/// their own full-width row above the actions instead of being squeezed into a column one word wide.
/// Decided in the normal layout pass, so nothing jumps while the window is resized.
/// First child: the actions (stays first in tab order, like before), second child: the messages.
/// </summary>
public class FooterPanel : Panel
{
    public static readonly DependencyProperty MinInlineWidthProperty = DependencyProperty.Register(
        nameof(MinInlineWidth), typeof(double), typeof(FooterPanel),
        new FrameworkPropertyMetadata(400.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>
    /// The narrowest the messages may get next to the actions before they move above them. Counts the whole bar:
    /// icon, padding and its own button take about 170–200 px, so 400 leaves the text roughly 200 px (3 lines in German).
    /// </summary>
    public double MinInlineWidth
    {
        get => (double)GetValue(MinInlineWidthProperty);
        set => SetValue(MinInlineWidthProperty, value);
    }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(FooterPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Gap between messages and actions: horizontal side by side, vertical when stacked.</summary>
    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Result of the last measure: messages on their own row above the actions.</summary>
    private bool _stacked;

    private UIElement? Actions => Children.Count > 0 ? Children[0] : null;
    private UIElement? Messages => Children.Count > 1 ? Children[1] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        _stacked = false;
        var actions = Actions;
        var messages = Messages;
        actions?.Measure(availableSize);
        var actionsSize = actions?.DesiredSize ?? default;

        if (messages is null)
        {
            return actionsSize;
        }

        // Natural width first: a short message that fits on one line stays inline whatever the minimum
        messages.Measure(new Size(double.PositiveInfinity, availableSize.Height));
        if (messages.DesiredSize.Width <= 0)
        {
            return actionsSize; // no bar open
        }

        var inlineWidth = Math.Max(0, availableSize.Width - actionsSize.Width - Spacing);
        _stacked = messages.DesiredSize.Width > inlineWidth && inlineWidth < MinInlineWidth;

        if (_stacked)
        {
            messages.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var stackedSize = messages.DesiredSize;
            return new Size(Math.Max(stackedSize.Width, actionsSize.Width),
                            stackedSize.Height + Spacing + actionsSize.Height);
        }

        messages.Measure(new Size(inlineWidth, availableSize.Height));
        var inlineSize = messages.DesiredSize;
        return new Size(inlineSize.Width + Spacing + actionsSize.Width,
                        Math.Max(inlineSize.Height, actionsSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var actions = Actions;
        var messages = Messages;
        var actionsSize = actions?.DesiredSize ?? default;
        var actionsLeft = Math.Max(0, finalSize.Width - actionsSize.Width);

        if (_stacked && messages is not null)
        {
            var messagesHeight = messages.DesiredSize.Height;
            messages.Arrange(new Rect(0, 0, finalSize.Width, messagesHeight));
            var actionsTop = messagesHeight + Spacing;
            actions?.Arrange(new Rect(actionsLeft, actionsTop, actionsSize.Width, Math.Max(0, finalSize.Height - actionsTop)));
        }
        else
        {
            // Full height for both: their own VerticalAlignment (Center) lines them up, like the DockPanel did
            messages?.Arrange(new Rect(0, 0, Math.Max(0, actionsLeft - Spacing), finalSize.Height));
            actions?.Arrange(new Rect(actionsLeft, 0, actionsSize.Width, finalSize.Height));
        }

        return finalSize;
    }
}
