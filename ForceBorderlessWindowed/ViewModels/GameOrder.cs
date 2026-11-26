using System.Globalization;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>
/// The order games are shown in, on the Games tab and in the tray menu: A–Z by the shown name, ignoring case, with
/// numbers compared as numbers ("Game 2" before "Game 10"). Games with the same name: the one without a window title
/// filter first, then by filter, then by ID, so the order never changes by itself.
/// </summary>
public static class GameOrder
{
    private static readonly StringComparer Names =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    public static int Compare(string title, string? titleFilter, Guid id, string otherTitle, string? otherTitleFilter, Guid otherId)
    {
        var result = Names.Compare(title, otherTitle);
        if (result == 0)
        {
            result = (titleFilter, otherTitleFilter) switch
            {
                (null, null) => 0,
                (null, _) => -1,
                (_, null) => 1,
                _ => Names.Compare(titleFilter, otherTitleFilter),
            };
        }

        return result != 0 ? result : id.CompareTo(otherId);
    }
}
