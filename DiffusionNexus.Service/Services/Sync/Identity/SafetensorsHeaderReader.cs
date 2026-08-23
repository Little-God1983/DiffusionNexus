using System.Buffers.Binary;
using System.Text.Json;

namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>The identity-relevant fields of a safetensors JSON header's __metadata__ block.</summary>
public sealed record SafetensorsHeaderInfo(
    string? BaseModelVersion,   // __metadata__["ss_base_model_version"]
    string? Architecture,       // __metadata__["modelspec.architecture"]
    string? ModelNameHint);     // __metadata__["ss_sd_model_name"]

/// <summary>
/// Reads the JSON header a safetensors file carries in its first bytes, without ever touching
/// the tensor payload that follows it. Later tasks use the extracted <c>__metadata__</c> fields
/// to identify a model's base model straight from the file, independent of any sidecar.
/// </summary>
public static class SafetensorsHeaderReader
{
    // spec §4.5: cap 16 MB, never the tensors.
    public const long MaxHeaderBytes = 16 * 1024 * 1024;

    private const string SafetensorsExtension = ".safetensors";
    private const int LengthPrefixBytes = 8;

    /// <summary>Best-effort read of the safetensors header; null on ANY failure, never throws.</summary>
    public static SafetensorsHeaderInfo? TryRead(string filePath)
    {
        try
        {
            if (!string.Equals(Path.GetExtension(filePath), SafetensorsExtension, StringComparison.OrdinalIgnoreCase))
                return null;

            // FileShare.ReadWrite (not the house FileShare.Read from FileHasher.cs:23) because a
            // trainer mid-checkpoint holds the file write-shared; a read-only share would fail to
            // open it and we'd never get a chance at the header.
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920);

            Span<byte> lengthBuffer = stackalloc byte[LengthPrefixBytes];
            stream.ReadExactly(lengthBuffer);
            var headerLength = BinaryPrimitives.ReadUInt64LittleEndian(lengthBuffer);

            if (headerLength == 0 || headerLength > MaxHeaderBytes)
                return null;
            if (LengthPrefixBytes + (long)headerLength > stream.Length)
                return null;

            var headerBytes = new byte[headerLength];
            stream.ReadExactly(headerBytes);

            using var document = JsonDocument.Parse(headerBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            string? baseModelVersion = null;
            string? architecture = null;
            string? modelNameHint = null;

            if (document.RootElement.TryGetProperty("__metadata__", out var metadata) &&
                metadata.ValueKind == JsonValueKind.Object)
            {
                baseModelVersion = ReadStringProperty(metadata, "ss_base_model_version");
                architecture = ReadStringProperty(metadata, "modelspec.architecture");
                modelNameHint = ReadStringProperty(metadata, "ss_sd_model_name");
            }

            return new SafetensorsHeaderInfo(baseModelVersion, architecture, modelNameHint);
        }
        catch
        {
            // No logging here — deliberately: the caller (the identity step) owns the log line.
            return null;
        }
    }

    private static string? ReadStringProperty(JsonElement obj, string propertyName) =>
        obj.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
