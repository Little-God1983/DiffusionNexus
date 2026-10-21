using System.Text;

namespace DiffusionNexus.Service.Services.Imaging;

/// <summary>
/// Carries the metadata of an original image file into a freshly encoded replacement of the
/// same format, and reads the encoder settings worth keeping. ImageSharp's <c>Image.Save(path)</c>
/// kept the metadata it had loaded and reused the source JPEG quality; SkiaSharp's encoders write
/// pixels and a colour profile only. Without this, rewriting a file in place (the colour fixer)
/// strips ComfyUI/A1111 generation parameters, EXIF orientation (pixels are stored unrotated, so
/// a dropped tag turns phone photos sideways) and drops a q95 JPEG to q75.
/// <para>
/// Colour profiles are deliberately not carried: Skia writes its own (PNG <c>sRGB</c>/<c>iCCP</c>,
/// JPEG APP2 ICC) from the bitmap's colour space, which the decoder keeps, and two profiles in
/// one file make it invalid. WebP metadata is not carried; only the lossless/lossy choice is.
/// </para>
/// </summary>
internal static class ImageMetadataCarrier
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>
    /// Ancillary PNG chunks that describe the content rather than the encoding: text (generation
    /// parameters, workflows), EXIF, pixel density and modification time.
    /// </summary>
    private static readonly HashSet<string> CarriedPngChunks = ["tEXt", "zTXt", "iTXt", "eXIf", "pHYs", "tIME"];

    /// <summary>PNG chunks the spec allows only once; a source copy is skipped when the encoder already wrote one.</summary>
    private static readonly HashSet<string> SingletonPngChunks = ["eXIf", "pHYs", "tIME"];

    /// <summary>JPEG segments carried: APP1 (EXIF, XMP), APP13 (IPTC / Photoshop) and COM (comments).</summary>
    private static bool IsCarriedJpegMarker(byte marker) => marker is 0xE1 or 0xED or 0xFE;

    /// <summary>
    /// Copies the carried chunks of <paramref name="source"/> into <paramref name="encoded"/>,
    /// placed before the first IDAT. Returns <paramref name="encoded"/> unchanged when either is
    /// not a PNG or nothing is worth carrying.
    /// </summary>
    internal static byte[] CarryPng(byte[] encoded, byte[]? source)
    {
        if (source is null || !IsPng(source) || !IsPng(encoded))
            return encoded;

        var encodedChunks = PngChunks(encoded).ToList();
        var encodedTypes = encodedChunks.Select(c => c.Type).ToHashSet(StringComparer.Ordinal);
        var carried = PngChunks(source)
            .Where(c => CarriedPngChunks.Contains(c.Type))
            .Where(c => !(SingletonPngChunks.Contains(c.Type) && encodedTypes.Contains(c.Type)))
            .ToList();
        if (carried.Count == 0)
            return encoded;

        var firstIdat = encodedChunks.FirstOrDefault(c => c.Type == "IDAT");
        if (firstIdat.Type is null)
            return encoded;

        using var output = new MemoryStream(encoded.Length + carried.Sum(c => c.Length));
        output.Write(encoded, 0, firstIdat.Offset);
        foreach (var chunk in carried)
            output.Write(source, chunk.Offset, chunk.Length);
        output.Write(encoded, firstIdat.Offset, encoded.Length - firstIdat.Offset);
        return output.ToArray();
    }

    /// <summary>
    /// Copies the carried segments of <paramref name="source"/> into <paramref name="encoded"/>,
    /// right after the encoder's APP0 (JFIF) segment, or after SOI when there is none. Returns
    /// <paramref name="encoded"/> unchanged when either is not a JPEG or nothing is worth carrying.
    /// </summary>
    internal static byte[] CarryJpeg(byte[] encoded, byte[]? source)
    {
        if (source is null || !IsJpeg(source) || !IsJpeg(encoded))
            return encoded;

        var carried = JpegSegments(source).Where(s => IsCarriedJpegMarker(s.Marker)).ToList();
        if (carried.Count == 0)
            return encoded;

        var firstEncoded = JpegSegments(encoded).FirstOrDefault();
        int insertAt = firstEncoded.Marker == 0xE0 ? firstEncoded.Offset + firstEncoded.Length : 2;

        using var output = new MemoryStream(encoded.Length + carried.Sum(s => s.Length));
        output.Write(encoded, 0, insertAt);
        foreach (var segment in carried)
            output.Write(source, segment.Offset, segment.Length);
        output.Write(encoded, insertAt, encoded.Length - insertAt);
        return output.ToArray();
    }

    /// <summary>
    /// Estimates the quality a JPEG was saved at by finding the libjpeg quality whose scaled
    /// standard luminance table is closest to the file's first quantisation table. Exact for
    /// libjpeg-family encoders (Skia, Photoshop "save for web", most cameras' defaults are close).
    /// Returns null when the file is not a JPEG or has no 8-bit luminance table.
    /// </summary>
    internal static int? EstimateJpegQuality(byte[]? jpeg)
    {
        if (jpeg is null || !IsJpeg(jpeg))
            return null;

        int[]? table = null;
        foreach (var segment in JpegSegments(jpeg).Where(s => s.Marker == 0xDB))
        {
            int p = segment.Offset + 4;
            int end = segment.Offset + segment.Length;
            while (p < end && table is null)
            {
                int precision = jpeg[p] >> 4;
                int id = jpeg[p] & 0x0F;
                int size = precision == 0 ? 64 : 128;
                if (p + 1 + size > end) break;
                if (id == 0)
                {
                    table = new int[64];
                    for (int i = 0; i < 64; i++)
                    {
                        // DQT stores the table in zig-zag order; map back to natural order.
                        int value = precision == 0 ? jpeg[p + 1 + i] : (jpeg[p + 1 + 2 * i] << 8) | jpeg[p + 2 + 2 * i];
                        table[ZigZag[i]] = value;
                    }
                }
                p += 1 + size;
            }
            if (table is not null) break;
        }
        if (table is null)
            return null;

        int bestQuality = 75;
        long bestError = long.MaxValue;
        for (int quality = 1; quality <= 100; quality++)
        {
            int scale = quality < 50 ? 5000 / quality : 200 - quality * 2;
            long error = 0;
            for (int i = 0; i < 64; i++)
            {
                int expected = Math.Clamp((StandardLuminance[i] * scale + 50) / 100, 1, 255);
                error += Math.Abs(expected - table[i]);
            }
            if (error < bestError)
            {
                bestError = error;
                bestQuality = quality;
            }
        }
        return bestQuality;
    }

    /// <summary>True when <paramref name="webp"/> is a WebP whose image data is VP8L (lossless).</summary>
    internal static bool IsLosslessWebp(byte[]? webp)
    {
        if (webp is null || webp.Length < 16
            || Encoding.ASCII.GetString(webp, 0, 4) != "RIFF" || Encoding.ASCII.GetString(webp, 8, 4) != "WEBP")
            return false;

        int p = 12;
        while (p + 8 <= webp.Length)
        {
            string fourCc = Encoding.ASCII.GetString(webp, p, 4);
            if (fourCc == "VP8L") return true;
            if (fourCc == "VP8 ") return false;
            long size = BitConverter.ToUInt32(webp, p + 4);
            p += 8 + (int)Math.Min(size + (size & 1), int.MaxValue - p - 8);
        }
        return false;
    }

    internal static bool IsPng(byte[] data) => data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngSignature);

    internal static bool IsJpeg(byte[] data) => data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8;

    private readonly record struct PngChunk(string Type, int Offset, int Length);

    private readonly record struct JpegSegment(byte Marker, int Offset, int Length);

    /// <summary>Enumerates whole chunks (length, type, data, CRC) up to IEND; stops at the first malformed one.</summary>
    private static IEnumerable<PngChunk> PngChunks(byte[] png)
    {
        int p = 8;
        while (p + 12 <= png.Length)
        {
            long dataLength = ((long)png[p] << 24) | ((long)png[p + 1] << 16) | ((long)png[p + 2] << 8) | png[p + 3];
            long total = 12 + dataLength;
            if (p + total > png.Length) yield break;
            string type = Encoding.ASCII.GetString(png, p + 4, 4);
            yield return new PngChunk(type, p, (int)total);
            if (type == "IEND") yield break;
            p += (int)total;
        }
    }

    /// <summary>Enumerates the marker segments between SOI and SOS; stops at SOS or the first malformed one.</summary>
    private static IEnumerable<JpegSegment> JpegSegments(byte[] jpeg)
    {
        int p = 2;
        while (p + 4 <= jpeg.Length && jpeg[p] == 0xFF)
        {
            byte marker = jpeg[p + 1];
            if (marker == 0xFF) { p++; continue; }   // fill byte
            if (marker == 0xDA || marker == 0xD9) yield break;
            int length = (jpeg[p + 2] << 8) | jpeg[p + 3];
            if (length < 2 || p + 2 + length > jpeg.Length) yield break;
            yield return new JpegSegment(marker, p, 2 + length);
            p += 2 + length;
        }
    }

    /// <summary>The IJG standard luminance quantisation table (JPEG spec Annex K), natural order.</summary>
    private static readonly int[] StandardLuminance =
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>Zig-zag position k → natural (row-major) index.</summary>
    private static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10,
        17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34,
        27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36,
        29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46,
        53, 60, 61, 54, 47, 55, 62, 63,
    ];
}
