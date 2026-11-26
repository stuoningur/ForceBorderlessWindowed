using System.Windows;

namespace ForceBorderlessWindowed.Controls;

/// <summary>
/// A Segoe Fluent Icons glyph shown in front of a tab header (SelectorTabItemStyle).
/// The header itself stays plain text, so it's still the tab's screen reader name and access key source.
/// </summary>
public static class TabIcon
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached(
        "Glyph", typeof(string), typeof(TabIcon), new PropertyMetadata(null));

    public static string? GetGlyph(DependencyObject element) => (string?)element.GetValue(GlyphProperty);

    public static void SetGlyph(DependencyObject element, string? value) => element.SetValue(GlyphProperty, value);
}
