using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Reads image dimensions from file headers using pure binary parsing.
/// Supports PNG, JPEG, WebP, BMP, and GIF — no SkiaSharp or ImageSharp dependency.
/// </summary>
// TODO: Linux Implementation — verify endianness assumptions on Linux (should be fine with BinaryPrimitives).
public sealed class ImageHeaderReader : IImageDimensionReader
{
    private static readonly ImageDimensions Invalid = new(0, 0);

    /// <inheritdoc />
    public ImageDimensions ReadDimensions(string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (!File.Exists(filePath))
            return Invalid;

        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext))
            return Invalid;

        return ext.ToLowerInvariant() switch
        {
            ".png" => ReadPngDimensions(filePath),
            ".jpg" or ".jpeg" => ReadJpegDimensions(filePath),
            ".webp" => ReadWebPDimensions(filePath),
            ".bmp" => ReadBmpDimensions(filePath),
            ".gif" => ReadGifDimensions(filePath),
            _ => Invalid
        };
    }

    /// <summary>
    /// Reads width and height from the IHDR chunk of a PNG file.
    /// </summary>
    internal static ImageDimensions ReadPngDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            // PNG signature: 8 bytes
            if (stream.Length < 24)
                return Invalid;

            stream.Seek(8, SeekOrigin.Begin);

            // IHDR chunk: 4-byte length, 4-byte type "IHDR", then width (4 BE) and height (4 BE)
            _ = reader.ReadBytes(4); // chunk length
            var typeBytes = reader.ReadBytes(4);
            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            if (type != "IHDR")
                return Invalid;

            int width = ReadInt32BigEndian(reader);
            int height = ReadInt32BigEndian(reader);
            return new ImageDimensions(width, height);
        }
        catch
        {
            return Invalid;
        }
    }

    /// <summary>
    /// Reads width and height from the first SOF marker in a JPEG file.
    /// Scans SOF0 (0xC0) through SOF2 (0xC2), skipping non-SOF markers.
    /// </summary>
    internal static ImageDimensions ReadJpegDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 4)
                return Invalid;

            // SOI marker: FF D8
            if (reader.ReadByte() != 0xFF || reader.ReadByte() != 0xD8)
                return Invalid;

            while (stream.Position < stream.Length - 1)
            {
                if (reader.ReadByte() != 0xFF)
                    continue;

                byte marker = reader.ReadByte();

                // Skip padding 0xFF bytes
                while (marker == 0xFF && stream.Position < stream.Length)
                    marker = reader.ReadByte();

                // SOF markers: C0-C3, C5-C7, C9-CB, CD-CF (skip C4=DHT, C8=JPG, CC=DAC)
                if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC))
                {
                    if (stream.Position + 7 > stream.Length)
                        return Invalid;

                    _ = reader.ReadBytes(2); // segment length
                    _ = reader.ReadByte();   // precision

                    int height = ReadUInt16BigEndian(reader);
                    int width = ReadUInt16BigEndian(reader);
                    return new ImageDimensions(width, height);
                }

                // Skip this segment
                if (stream.Position + 2 > stream.Length)
                    break;

                int segmentLength = ReadUInt16BigEndian(reader);
                if (segmentLength < 2)
                    break;

                stream.Seek(segmentLength - 2, SeekOrigin.Current);
            }

            return Invalid;
        }
        catch
        {
            return Invalid;
        }
    }

    /// <summary>
    /// Reads width and height from a WebP file header (VP8, VP8L, or VP8X).
    /// </summary>
    internal static ImageDimensions ReadWebPDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 30)
                return Invalid;

            // RIFF header: "RIFF" + 4-byte size + "WEBP"
            var riff = reader.ReadBytes(4);
            if (System.Text.Encoding.ASCII.GetString(riff) != "RIFF")
                return Invalid;

            _ = reader.ReadBytes(4); // file size
            var webp = reader.ReadBytes(4);
            if (System.Text.Encoding.ASCII.GetString(webp) != "WEBP")
                return Invalid;

            var chunkType = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4));
            _ = reader.ReadBytes(4); // chunk size

            return chunkType switch
            {
                "VP8 " => ReadWebPVP8(reader),
                "VP8L" => ReadWebPVP8L(reader),
                "VP8X" => ReadWebPVP8X(reader),
                _ => Invalid
            };
        }
        catch
        {
            return Invalid;
        }
    }

    /// <summary>
    /// Reads width and height from the BITMAPINFOHEADER of a BMP file.
    /// Handles negative height (top-down bitmap).
    /// </summary>
    internal static ImageDimensions ReadBmpDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 26)
                return Invalid;

            // BM signature
            if (reader.ReadByte() != (byte)'B' || reader.ReadByte() != (byte)'M')
                return Invalid;

            // Skip to DIB header at offset 14
            stream.Seek(14, SeekOrigin.Begin);
            _ = reader.ReadInt32(); // DIB header size

            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            return new ImageDimensions(width, Math.Abs(height));
        }
        catch
        {
            return Invalid;
        }
    }

    /// <summary>
    /// Reads width and height from the GIF Logical Screen Descriptor.
    /// </summary>
    internal static ImageDimensions ReadGifDimensions(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 10)
                return Invalid;

            // GIF signature: "GIF87a" or "GIF89a"
            var sig = reader.ReadBytes(6);
            var sigStr = System.Text.Encoding.ASCII.GetString(sig);
            if (sigStr is not ("GIF87a" or "GIF89a"))
                return Invalid;

            int width = reader.ReadUInt16();  // little-endian
            int height = reader.ReadUInt16(); // little-endian
            return new ImageDimensions(width, height);
        }
        catch
        {
            return Invalid;
        }
    }

    #region VP8 Sub-readers

    private static ImageDimensions ReadWebPVP8(BinaryReader reader)
    {
        // VP8 bitstream: skip 3-byte frame tag + 3-byte start code (0x9D012A)
        if (reader.BaseStream.Position + 10 > reader.BaseStream.Length)
            return Invalid;

        _ = reader.ReadBytes(3); // frame tag
        var startCode = reader.ReadBytes(3);
        if (startCode[0] != 0x9D || startCode[1] != 0x01 || startCode[2] != 0x2A)
            return Invalid;

        int width = reader.ReadUInt16() & 0x3FFF;
        int height = reader.ReadUInt16() & 0x3FFF;
        return new ImageDimensions(width, height);
    }

    private static ImageDimensions ReadWebPVP8L(BinaryReader reader)
    {
        // VP8L: 1-byte signature (0x2F), then 4-byte bitstream with width/height packed
        if (reader.BaseStream.Position + 5 > reader.BaseStream.Length)
            return Invalid;

        byte sig = reader.ReadByte();
        if (sig != 0x2F)
            return Invalid;

        uint bits = reader.ReadUInt32();
        int width = (int)(bits & 0x3FFF) + 1;
        int height = (int)((bits >> 14) & 0x3FFF) + 1;
        return new ImageDimensions(width, height);
    }

    private static ImageDimensions ReadWebPVP8X(BinaryReader reader)
    {
        // VP8X: flags (4 bytes), then 3-byte width-1 and 3-byte height-1 (little-endian)
        if (reader.BaseStream.Position + 10 > reader.BaseStream.Length)
            return Invalid;

        _ = reader.ReadBytes(4); // flags
        int width = ReadUInt24LittleEndian(reader) + 1;
        int height = ReadUInt24LittleEndian(reader) + 1;
        return new ImageDimensions(width, height);
    }

    #endregion

    #region Binary Helpers

    private static int ReadInt32BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(4);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToInt32(bytes, 0);
    }

    private static int ReadUInt16BigEndian(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(2);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        return BitConverter.ToUInt16(bytes, 0);
    }

    private static int ReadUInt24LittleEndian(BinaryReader reader)
    {
        byte b0 = reader.ReadByte();
        byte b1 = reader.ReadByte();
        byte b2 = reader.ReadByte();
        return b0 | (b1 << 8) | (b2 << 16);
    }

    #endregion
}
