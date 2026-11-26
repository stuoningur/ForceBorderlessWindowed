namespace ForceBorderlessWindowed.ViewModels;

/// <summary>An entry in the language ComboBox. CultureName null = Windows default.</summary>
/// <remarks>ToString is what screen readers announce for the entry (instead of the record's member dump).</remarks>
public sealed record LanguageOption(string? CultureName, string DisplayName)
{
    public override string ToString() => DisplayName;
}