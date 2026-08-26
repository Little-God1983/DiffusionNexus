using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Null-tolerant reader for Civitai stat counters. The API returns JSON
/// <c>null</c> for stats fields on freshly published models (counts not yet
/// computed server-side), and the default number converter throws on null —
/// one such model kills deserialization of the entire paged response. Stats
/// are informational only, so no shape may ever fail the surrounding payload:
/// numbers pass through, numeric strings are parsed, null and any other shape
/// read as zero.
/// </summary>
public sealed class TolerantInt32JsonConverter : JsonConverter<int>
{
    public override int Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                if (reader.TryGetInt32(out var value)) return value;
                // Fractional or out-of-int-range — saturating cast keeps a usable count.
                return (int)Math.Clamp(reader.GetDouble(), int.MinValue, int.MaxValue);

            case JsonTokenType.String:
                return int.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, int value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}

/// <summary>
/// Tolerant reader for nullable identifier-like fields such as
/// <c>meta.seed</c> on gallery images. Seeds pass through JavaScript float64
/// on Civitai's side, so live responses carry values beyond
/// <c>Int64.MaxValue</c> (a real by-hash payload served
/// <c>"seed":12859270413054550000</c>) — the default converter throws and one
/// such image kills deserialization of the entire model version. Unlike the
/// stat converters, an unusable value reads as <c>null</c> rather than 0: the
/// field is already nullable, and a clamped or rounded seed would be a wrong
/// value presented as a real one.
/// </summary>
public sealed class TolerantNullableInt64JsonConverter : JsonConverter<long?>
{
    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                // Out-of-range or fractional numbers fall through to null.
                return reader.TryGetInt64(out var value) ? value : null;

            case JsonTokenType.String:
                return long.TryParse(reader.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}

/// <summary>
/// Floating-point companion of <see cref="TolerantInt32JsonConverter"/> for
/// stat fields like <c>rating</c> — same rules, reading null as 0.
/// </summary>
public sealed class TolerantDoubleJsonConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.GetDouble();

            case JsonTokenType.String:
                return double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : 0;

            default:
                reader.Skip();
                return 0;
        }
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
