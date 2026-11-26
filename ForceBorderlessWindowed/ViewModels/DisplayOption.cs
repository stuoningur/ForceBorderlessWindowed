using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.ViewModels;

/// <summary>An entry in the display ComboBox of the placement editor.</summary>
/// <param name="Id">Device path, only for <see cref="DisplayTarget.Specific"/>.</param>
/// <param name="Name">Monitor name, only for <see cref="DisplayTarget.Specific"/> (saved for "not connected").</param>
/// <param name="Label">Text in the ComboBox, e.g. "2: LG 27GL850 – 2560 × 1440".</param>
/// <param name="ShortLabel">Text in the summary, e.g. "LG 27GL850" or "primary display".</param>
public sealed record DisplayOption(DisplayTarget Target, string? Id, string? Name, string Label, string ShortLabel)
{
    /// <summary>What screen readers announce for the entry (instead of the record's member dump).</summary>
    public override string ToString() => Label;

    /// <summary>Same choice, ignoring the texts (which change with resolution or language).</summary>
    public bool Matches(DisplayTarget target, string? id) =>
        Target == target
        && (target != DisplayTarget.Specific || string.Equals(Id, id, StringComparison.OrdinalIgnoreCase));
}
