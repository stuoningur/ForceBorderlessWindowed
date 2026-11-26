using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForceBorderlessWindowed.Services;

/// <summary>
/// Enums as names, like <see cref="JsonStringEnumConverter"/>, but an unknown name (from a newer version of the
/// app, or a typo in a hand-edited file) doesn't make the whole file unreadable: it becomes a value the enum
/// doesn't define, which SettingsService.Repair replaces with the default and logs, like an unknown number.
/// Otherwise one wrong word in one game would reset every game and every setting.
/// </summary>
internal sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    private readonly JsonStringEnumConverter _names = new();

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert),
            _names.CreateConverter(typeToConvert, options))!;
}

internal sealed class LenientEnumConverter<T>(JsonConverter names) : JsonConverter<T>
    where T : struct, Enum
{
    private readonly JsonConverter<T> _names = (JsonConverter<T>)names;

    /// <summary>
    /// All bits set: defined by none of the app's enums, and outside every combination the [Flags] ones allow
    /// (ModifierKeys), so Repair and HotkeyRules always see it as invalid.
    /// </summary>
    private static readonly T Unknown = (T)Enum.ToObject(typeof(T), -1);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
        {
            reader.Skip();   // a converter must leave the reader at the end of its value
            return Unknown;
        }

        try
        {
            return _names.Read(ref reader, typeToConvert, options);
        }
        catch (JsonException)
        {
            return Unknown;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        _names.Write(writer, value, options);
}
