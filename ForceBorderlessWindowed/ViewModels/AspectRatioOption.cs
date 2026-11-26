using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>An entry in the aspect ratio ComboBox.</summary>
/// <param name="ShortLabel">"4:3", for the summaries.</param>
/// <remarks>ToString is what screen readers announce for the entry (instead of the record's member dump).</remarks>
public sealed record AspectRatioOption(AspectRatio Ratio, string DisplayName, string ShortLabel)
{
    public override string ToString() => DisplayName;
}
