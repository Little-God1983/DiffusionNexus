using System.IO;
using System.Text;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Writes PNG tEXt chunks into PNG files, replacing any existing image metadata.
/// Used to "bake" gallery metadata (captions) into images as A1111-style parameters.
/// Companion to <see cref="PngChunkReader"/>.
/// </summary>
internal static class PngMetadataWriter
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Copies a PNG file to <paramref name="destPath"/>, stripping all existing tEXt/iTXt chunks
    /// and inserting new tEXt chunks from <paramref name="metadata"/>.
    /// Non-PNG files are copied without modification.
    /// </summary>
    /// <param name="sourcePath">Path to the source PNG file.</param>
    /// <param name="destPath">Path to write the modified PNG file.</param>
    /// <param name="metadata">Key-value pairs to embed as tEXt chunks.</param>
    public static void CopyWithMetadata(string sourcePath, string destPath, Dictionary<string, string> metadata)
    {
        ArgumentNullException.ThrowIfNull(sourcePath);
        ArgumentNullException.ThrowIfNull(destPath);
        ArgumentNullException.ThrowIfNull(metadata);

        if (!IsPngFile(sourcePath))
        {
            File.Copy(sourcePath, destPath, overwrite: true);
            return;
        }

        using var input = File.OpenRead(sourcePath);
        using var reader = new BinaryReader(input);
        using var output = File.Create(destPath);
        using var writer = new BinaryWriter(output);

        // Validate and copy PNG signature
        var sig = reader.ReadBytes(PngSignature.Length);
        if (sig.Length < PngSignature.Length || !sig.AsSpan().SequenceEqual(PngSignature))
        {
            // Not a valid PNG — fall back to plain copy
            input.Position = 0;
            input.CopyTo(output);
            return;
        }

        writer.Write(sig);

        // Read and copy IHDR chunk first (must be first chunk)
        CopyChunk(reader, writer, input);

        // Write new tEXt chunks right after IHDR
        foreach (var (key, value) in metadata)
        {
            WriteTextChunk(writer, key, value);
        }

        // Copy remaining chunks, skipping old tEXt/iTXt
        while (input.Position < input.Length - 4)
        {
            var startPos = input.Position;

            var lengthBytes = reader.ReadBytes(4);
            if (lengthBytes.Length < 4) break;

            var lengthBE = (byte[])lengthBytes.Clone();
            if (BitConverter.IsLittleEndian) Array.Reverse(lengthBE);
            int length = BitConverter.ToInt32(lengthBE, 0);

            var typeBytes = reader.ReadBytes(4);
            if (typeBytes.Length < 4) break;

            var type = Encoding.ASCII.GetString(typeBytes);

            if (type is "tEXt" or "iTXt")
            {
                // Skip old metadata chunk (data + CRC)
                if (input.Position + length + 4 > input.Length) break;
                input.Seek(length + 4, SeekOrigin.Current);
                continue;
            }

            // Copy this chunk as-is (length + type + data + CRC)
            writer.Write(lengthBytes);
            writer.Write(typeBytes);

            if (length > 0)
            {
                var data = reader.ReadBytes(length);
                writer.Write(data);
            }

            var crc = reader.ReadBytes(4);
            writer.Write(crc);

            if (type == "IEND") break;
        }
    }

    /// <summary>
    /// Formats caption text as A1111-style generation parameters so tools like Civitai can parse it.
    /// The caption becomes the positive prompt, with minimal placeholder generation settings.
    /// Optionally reads PNG dimensions from <paramref name="imagePath"/> for an accurate Size field.
    /// </summary>
    public static string FormatAsA1111Parameters(string prompt, string? imagePath = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        var size = "512x512";
        if (imagePath is not null && IsPngFile(imagePath) && File.Exists(imagePath))
        {
            var (w, h) = ReadPngDimensions(imagePath);
            if (w > 0 && h > 0) size = $"{w}x{h}";
        }

        return $"{prompt.TrimEnd()}\nSteps: 1, Sampler: Euler, CFG scale: 7, Seed: 0, Size: {size}";
    }

    /// <summary>
    /// Reads width and height from a PNG file's IHDR chunk.
    /// Returns (0, 0) on failure.
    /// </summary>
    private static (int Width, int Height) ReadPngDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            // Skip 8-byte PNG signature
            if (stream.Length < 24) return (0, 0);
            stream.Seek(8, SeekOrigin.Begin);

            // IHDR chunk: 4-byte length + 4-byte type + 4-byte width + 4-byte height
            stream.Seek(4, SeekOrigin.Current); // length
            var type = Encoding.ASCII.GetString(reader.ReadBytes(4));
            if (type != "IHDR") return (0, 0);

            var widthBytes = reader.ReadBytes(4);
            var heightBytes = reader.ReadBytes(4);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(widthBytes);
                Array.Reverse(heightBytes);
            }

            return (BitConverter.ToInt32(widthBytes, 0), BitConverter.ToInt32(heightBytes, 0));
        }
        catch
        {
            return (0, 0);
        }
    }

    /// <summary>
    /// Checks if a file has a .png extension.
    /// </summary>
    private static bool IsPngFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Copies a single chunk (length + type + data + CRC) from reader to writer.
    /// </summary>
    private static void CopyChunk(BinaryReader reader, BinaryWriter writer, Stream input)
    {
        var lengthBytes = reader.ReadBytes(4);
        if (lengthBytes.Length < 4) return;

        var lengthBE = (byte[])lengthBytes.Clone();
        if (BitConverter.IsLittleEndian) Array.Reverse(lengthBE);
        int length = BitConverter.ToInt32(lengthBE, 0);

        var typeBytes = reader.ReadBytes(4);

        writer.Write(lengthBytes);
        writer.Write(typeBytes);

        if (length > 0)
        {
            var data = reader.ReadBytes(length);
            writer.Write(data);
        }

        var crc = reader.ReadBytes(4);
        writer.Write(crc);
    }

    /// <summary>
    /// Writes a single PNG tEXt chunk: key + null separator + value.
    /// </summary>
    private static void WriteTextChunk(BinaryWriter writer, string key, string value)
    {
        var keyBytes = Encoding.Latin1.GetBytes(key);
        var valBytes = Encoding.Latin1.GetBytes(value);

        // data = key + 0x00 + value
        int dataLength = keyBytes.Length + 1 + valBytes.Length;

        // Length (big-endian)
        var lengthBytes = BitConverter.GetBytes(dataLength);
        if (BitConverter.IsLittleEndian) Array.Reverse(lengthBytes);
        writer.Write(lengthBytes);

        // Type
        var typeBytes = Encoding.ASCII.GetBytes("tEXt");
        writer.Write(typeBytes);

        // Data
        writer.Write(keyBytes);
        writer.Write((byte)0);
        writer.Write(valBytes);

        // CRC-32 over type + data
        var crcData = new byte[4 + dataLength];
        typeBytes.CopyTo(crcData, 0);
        keyBytes.CopyTo(crcData, 4);
        crcData[4 + keyBytes.Length] = 0;
        valBytes.CopyTo(crcData, 4 + keyBytes.Length + 1);

        uint crc = ComputeCrc32(crcData);
        var crcBytes = BitConverter.GetBytes(crc);
        if (BitConverter.IsLittleEndian) Array.Reverse(crcBytes);
        writer.Write(crcBytes);
    }

    /// <summary>
    /// Computes CRC-32 as specified by the PNG specification (ISO 3309).
    /// </summary>
    private static uint ComputeCrc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
