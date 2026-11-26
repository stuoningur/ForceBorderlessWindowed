using System.Text.Json.Serialization;

using ForceBorderlessWindowed.Models;

namespace ForceBorderlessWindowed.Services;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(LenientEnumConverterFactory)])]   // enums as names, unknown names repaired instead of failing
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Dictionary<string, SavedWindowPlacement>))]
[JsonSerializable(typeof(List<ChangedWindowRecord>))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}