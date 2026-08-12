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
