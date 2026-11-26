using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>An entry in the theme ComboBox.</summary>
/// <remarks>ToString is what screen readers announce for the entry (instead of the record's member dump).</remarks>
public sealed record ThemeOption(AppTheme Theme, string DisplayName)
{
    public override string ToString() => DisplayName;
}
