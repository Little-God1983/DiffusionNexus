using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiffusionNexus.Civitai.Models;

/// <summary>
/// Shape-tolerant reader for Civitai's <c>allowCommercialUse</c> field. The API
/// historically returned a set-notation string (e.g. "{Image,Rent,Sell}") and now
/// returns a JSON array of strings (possibly empty). The field is informational
/// only, so no shape may ever fail the surrounding payload: strings pass through,
/// arrays are comma-joined ("Image,Rent,Sell", empty → null), anything else is
/// skipped and read as null.
/// </summary>
public sealed class AllowCommercialUseJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                return reader.GetString();

            case JsonTokenType.StartArray:
                var values = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        var value = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            values.Add(value);
                        }
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
                return values.Count == 0 ? null : string.Join(",", values);

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value);
        }
    }
}
