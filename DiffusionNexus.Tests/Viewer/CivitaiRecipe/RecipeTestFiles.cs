using System.Buffers.Binary;
using System.Text;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>Builds the minimal PNG and JPEG files the recipe readers are tested on (#628).</summary>
internal static class RecipeTestFiles
{
    /// <summary>A PNG with one tEXt chunk per entry (CRCs are zero; the reader does not check them).</summary>
    public static byte[] PngWithText(params (string Key, string Value)[] texts)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, 1);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), 1);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(ms, "IHDR", ihdr);
        foreach (var (key, value) in texts)
        {
            WriteChunk(ms, "tEXt", [.. Encoding.Latin1.GetBytes(key), 0, .. Encoding.Latin1.GetBytes(value)]);
        }
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(type));
        stream.Write(data);
        stream.Write(new byte[4]);
    }

    /// <summary>
    /// A JPEG holding one APP1 Exif segment: IFD0 → Exif IFD → UserComment (prefix + text).
    /// <paramref name="truncateAfter"/> cuts the TIFF block after that many bytes.
    /// </summary>
    public static byte[] JpegWithUserComment(bool bigEndianTiff, byte[] prefix, byte[] text, int? truncateAfter = null)
    {
        var comment = prefix.Concat(text).ToArray();
        var tiff = new byte[44 + comment.Length];

        void U16(int at, int v)
        {
            if (bigEndianTiff) BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(at), (ushort)v);
            else BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(at), (ushort)v);
        }
        void U32(int at, int v)
        {
            if (bigEndianTiff) BinaryPrimitives.WriteInt32BigEndian(tiff.AsSpan(at), v);
            else BinaryPrimitives.WriteInt32LittleEndian(tiff.AsSpan(at), v);
        }

        tiff[0] = tiff[1] = (byte)(bigEndianTiff ? 'M' : 'I');
        U16(2, 42);
        U32(4, 8);
        // IFD0 at 8: one entry, the Exif IFD pointer (LONG) → 26.
        U16(8, 1);
        U16(10, 0x8769); U16(12, 4); U32(14, 1); U32(18, 26);
        U32(22, 0);
        // Exif IFD at 26: one entry, UserComment (UNDEFINED) → 44.
        U16(26, 1);
        U16(28, 0x9286); U16(30, 7); U32(32, comment.Length); U32(36, 44);
        U32(40, 0);
        comment.CopyTo(tiff, 44);

        if (truncateAfter is int cut) tiff = tiff[..cut];

        using var ms = new MemoryStream();
        ms.Write([0xFF, 0xD8, 0xFF, 0xE1]);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(2 + 6 + tiff.Length));
        ms.Write(length);
        ms.Write("Exif\0\0"u8);
        ms.Write(tiff);
        ms.Write([0xFF, 0xD9]);
        return ms.ToArray();
    }
}
