using System.Buffers.Binary;
using System.Text;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// Reads the EXIF UserComment of a JPEG — where A1111/Forge (via piexif) and Civitai's own
/// generator put the "parameters" text that a PNG carries in a text chunk (#628). No image
/// library: the APP1 segment is walked by hand, like <see cref="PngChunkReader"/> does for PNG.
/// </summary>
internal static class JpegExifReader
{
    private const ushort ExifIfdPointerTag = 0x8769;
    private const ushort UserCommentTag = 0x9286;

    /// <summary>The UserComment text, or null when the file has none or it cannot be read.</summary>
    public static string? ReadUserComment(string filePath)
    {
        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return FindExifTiff(bytes) is { } tiff ? ReadUserCommentFromTiff(tiff) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or IOException)
        {
            return null;
        }
    }

    /// <summary>The TIFF block of the first APP1 segment that starts with <c>Exif\0\0</c>.</summary>
    private static ReadOnlyMemory<byte>? FindExifTiff(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8) return null;

        var pos = 2;
        while (pos + 4 <= bytes.Length)
        {
            if (bytes[pos] != 0xFF) return null;
            var marker = bytes[pos + 1];
            // Start of scan or end of image: the metadata segments are all before it.
            if (marker is 0xDA or 0xD9) return null;
            var length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(pos + 2));
            var payloadStart = pos + 4;
            var payloadEnd = Math.Min(pos + 2 + length, bytes.Length);
            if (length < 2 || payloadStart > payloadEnd) return null;

            if (marker == 0xE1
                && payloadEnd - payloadStart >= 6
                && bytes.AsSpan(payloadStart, 6).SequenceEqual("Exif\0\0"u8))
            {
                return bytes.AsMemory(payloadStart + 6, payloadEnd - payloadStart - 6);
            }
            pos += 2 + length;
        }
        return null;
    }

    private static string? ReadUserCommentFromTiff(ReadOnlyMemory<byte> memory)
    {
        var tiff = memory.Span;
        if (tiff.Length < 8) return null;
        bool bigEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I') bigEndian = false;
        else if (tiff[0] == 'M' && tiff[1] == 'M') bigEndian = true;
        else return null;

        var exifIfd = FindEntry(tiff, bigEndian, (int)U32(tiff, 4, bigEndian), ExifIfdPointerTag);
        if (exifIfd is not { } pointer) return null;
        var comment = FindEntry(tiff, bigEndian, (int)U32(tiff, pointer + 8, bigEndian), UserCommentTag);
        if (comment is not { } entry) return null;

        var count = (int)U32(tiff, entry + 4, bigEndian);
        // A value of 4 bytes or fewer sits in the entry itself; a comment never fits there.
        if (count <= 8) return null;
        var offset = (int)U32(tiff, entry + 8, bigEndian);
        if (offset < 0 || offset + count > tiff.Length) return null;

        return Decode(tiff.Slice(offset, count));
    }

    /// <summary>Offset of the IFD entry carrying <paramref name="tag"/> (start of its 12 bytes), or null.</summary>
    private static int? FindEntry(ReadOnlySpan<byte> tiff, bool bigEndian, int ifdOffset, ushort tag)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > tiff.Length) return null;
        var count = U16(tiff, ifdOffset, bigEndian);
        for (var i = 0; i < count; i++)
        {
            var entry = ifdOffset + 2 + (12 * i);
            if (entry + 12 > tiff.Length) return null;
            if (U16(tiff, entry, bigEndian) == tag) return entry;
        }
        return null;
    }

    /// <summary>
    /// The 8-byte charset prefix decides the encoding. For <c>UNICODE</c> the byte order comes from
    /// the text, not the TIFF header: piexif writes UTF-16 big-endian even into an <c>II</c> TIFF, and
    /// every Civitai JPEG sampled on 2026-10-27 is that combination.
    /// </summary>
    private static string? Decode(ReadOnlySpan<byte> comment)
    {
        var prefix = comment[..8];
        var text = comment[8..];
        string decoded;
        if (prefix.SequenceEqual("UNICODE\0"u8))
        {
            if (text.Length >= 2 && text[0] == 0xFE && text[1] == 0xFF) decoded = Encoding.BigEndianUnicode.GetString(text[2..]);
            else if (text.Length >= 2 && text[0] == 0xFF && text[1] == 0xFE) decoded = Encoding.Unicode.GetString(text[2..]);
            else if (text.Length >= 2 && text[0] == 0 && text[1] != 0) decoded = Encoding.BigEndianUnicode.GetString(text);
            else decoded = Encoding.Unicode.GetString(text);
        }
        else
        {
            decoded = Encoding.UTF8.GetString(text);
        }

        decoded = decoded.TrimEnd('\0');
        return decoded.Length == 0 ? null : decoded;
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s[at..]) : BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool bigEndian) =>
        bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s[at..]) : BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
}
