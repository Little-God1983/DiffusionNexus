using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// JSON enum converter that silently falls back to <c>default(T)</c> when the
/// incoming string doesn't match any defined enum value. Civitai introduces new
/// values periodically (e.g. <c>fp8</c> on <see cref="CivitaiFloatingPoint"/>);
/// the stock <c>JsonStringEnumConverter</c> throws on unknown strings, which
/// kills the entire <c>/api/v1/models</c> deserialization and leaves the browser
/// stuck with a partial result set. With this converter, the unrecognized
/// metadata field reads as null/default and the rest of the response is fine.
/// </summary>
public sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return default;
            case JsonTokenType.String:
            {
                var raw = reader.GetString();
                if (!string.IsNullOrEmpty(raw)
                    && Enum.TryParse<T>(raw, ignoreCase: true, out var parsed))
                {
                    return parsed;
                }
                return default;
            }
            case JsonTokenType.Number:
            {
                if (reader.TryGetInt32(out var n)
                    && Enum.IsDefined(typeof(T), n))
                {
                    return (T)Enum.ToObject(typeof(T), n);
                }
                return default;
            }
            default:
                reader.Skip();
                return default;
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Factory that wires <see cref="TolerantEnumConverter{T}"/> for any enum type.
/// Register on a <see cref="JsonSerializerOptions"/> instance to make every enum
/// deserialization fall back to default on unknown values.
/// </summary>
public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(TolerantEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}
