using System.Buffers;
using System.Globalization;
using System.Text;
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

/// <summary>
/// Tolerant reader for the free-text generation parameters on <c>meta</c> of gallery images
/// (<c>"Hires upscale"</c>, <c>"Denoising strength"</c>, <c>"Clip skip"</c>, ...). Their shape
/// is whatever the posting tool wrote: one image sends <c>"Hires upscale":"2"</c>, another
/// <c>"Hires upscale":1.6</c> — and the default string converter throws on the number, which
/// failed deserialization of the entire model version (#627). Numbers and booleans read as
/// their JSON text, unchanged (<c>1.6</c> → <c>"1.6"</c>), so nothing is reformatted;
/// objects and arrays read as null.
/// </summary>
public sealed class TolerantMetaStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
                return reader.HasValueSequence
                    ? Encoding.UTF8.GetString(reader.ValueSequence.ToArray())
                    : Encoding.UTF8.GetString(reader.ValueSpan);

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

/// <summary>
/// Last line of defence for <see cref="CivitaiModelImage.Meta"/>: an image whose meta this DTO
/// cannot read (<c>"steps":"20"</c>, <c>"cfgScale":"7"</c>, a resource weight sent as a string,
/// meta sent as a raw parameter string, ...) reads as having no meta. The meta is the posting
/// tool's free-form output, and one such image used to fail the entire model version — files,
/// hashes and all (#627). <see cref="TolerantMetaStringJsonConverter"/> keeps the known text
/// fields readable; this catches every shape nobody has seen yet.
/// </summary>
public sealed class TolerantImageMetaJsonConverter : JsonConverter<CivitaiImageMeta?>
{
    public override CivitaiImageMeta? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }

        // Buffer the object first so a failure part-way through cannot leave the reader
        // inside it. The options are the caller's, without this converter: it is applied to
        // the property, not registered, so the inner read is the ordinary typed one.
        using var document = JsonDocument.ParseValue(ref reader);
        try
        {
            return document.RootElement.Deserialize<CivitaiImageMeta>(options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, CivitaiImageMeta? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else JsonSerializer.Serialize(writer, value, options);
    }
}
