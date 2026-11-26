using System.Globalization;
using System.Windows.Data;

namespace ForceBorderlessWindowed.Views;

/// <summary>
/// "_Add" → "Add": removes the access key marker, for places that need the plain text
/// (e.g. the screen reader name of a button whose content is icon + text).
/// "__" stays a literal underscore, like in WPF's AccessText.
/// </summary>
public sealed class AccessKeyConverter : IValueConverter
{
    public static AccessKeyConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string text ? StripAccessKey(text) : value;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    public static string StripAccessKey(string text) =>
        text.Replace("__", "\0", StringComparison.Ordinal)
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("\0", "_", StringComparison.Ordinal);
}
