using SkiaSharp;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Image files SkiaSharp can read but not write, built byte by byte for tests that need them
/// on disk: a GIF, an animated WebP and a JPEG EXIF orientation tag.
/// </summary>
public static class TestImageFiles
{
    /// <summary>
    /// A width x height GIF of colour 0 whose LZW stream sends a clear code before every pixel,
    /// so every code stays 3 bits wide and the bytes can be packed by hand.
    /// </summary>
    public static byte[] UncompressedGif(int width, int height)
    {
        var codes = new List<int>();
        for (int i = 0; i < width * height; i++) { codes.Add(4); codes.Add(0); }
        codes.Add(5);
        var data = new List<byte>();
        int bitBuffer = 0, bitCount = 0;
        foreach (var code in codes)
        {
            bitBuffer |= code << bitCount;
            bitCount += 3;
            while (bitCount >= 8) { data.Add((byte)(bitBuffer & 0xFF)); bitBuffer >>= 8; bitCount -= 8; }
        }
        if (bitCount > 0) data.Add((byte)bitBuffer);

        var gif = new List<byte>();
        gif.AddRange("GIF89a"u8.ToArray());
        gif.AddRange([(byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x80, 0x00, 0x00]);
        gif.AddRange([0x20, 0x60, 0xA0, 0xFF, 0xFF, 0xFF]);                  // two colours
        gif.AddRange([0x2C, 0, 0, 0, 0, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0x00]);
        gif.Add(0x02);                                                       // LZW minimum code size
        for (int i = 0; i < data.Count; i += 255)
        {
            int block = Math.Min(255, data.Count - i);
            gif.Add((byte)block);
            gif.AddRange(data.GetRange(i, block));
        }
        gif.Add(0x00);
        gif.Add(0x3B);
        return gif.ToArray();
    }

    /// <summary>
    /// A two-frame animated WebP of solid colours. Skia cannot write animation, so each frame is
    /// a lossless still from Skia whose VP8L chunk is wrapped in the VP8X / ANIM / ANMF container.
    /// </summary>
    public static byte[] AnimatedWebp(int width, int height)
    {
        var body = new List<byte>();
        body.AddRange("WEBP"u8.ToArray());
        AddChunk(body, "VP8X", [0x02, 0, 0, 0, .. UInt24(width - 1), .. UInt24(height - 1)]); // animation flag
        AddChunk(body, "ANIM", [0, 0, 0, 0, 0, 0]);                                           // background, loop forever
        foreach (var colour in new[] { SKColors.SteelBlue, SKColors.Gold })
        {
            // Offset 0,0, full canvas, 100 ms, no blending, no disposal.
            AddChunk(body, "ANMF",
                [0, 0, 0, 0, 0, 0, .. UInt24(width - 1), .. UInt24(height - 1), .. UInt24(100), 0x02, .. Vp8lChunk(width, height, colour)]);
        }

        var file = new List<byte>();
        file.AddRange("RIFF"u8.ToArray());
        file.AddRange(BitConverter.GetBytes(body.Count));
        file.AddRange(body);
        return file.ToArray();
    }

    /// <summary>Inserts an APP1 Exif segment holding only IFD0/Orientation right after the SOI marker.</summary>
    public static byte[] InsertExifOrientation(byte[] jpeg, short orientation)
    {
        byte[] tiff =
        [
            (byte)'I', (byte)'I', 0x2A, 0x00, 0x08, 0x00, 0x00, 0x00, // little-endian TIFF header, IFD0 at 8
            0x01, 0x00,                                               // one entry
            0x12, 0x01, 0x03, 0x00, 0x01, 0x00, 0x00, 0x00,           // tag 0x0112 Orientation, SHORT, count 1
            (byte)orientation, 0x00, 0x00, 0x00,                      // value
            0x00, 0x00, 0x00, 0x00,                                   // no next IFD
        ];
        var payload = "Exif\0\0"u8.ToArray().Concat(tiff).ToArray();
        int length = payload.Length + 2;
        var segment = new byte[] { 0xFF, 0xE1, (byte)(length >> 8), (byte)length }.Concat(payload);
        return jpeg.Take(2).Concat(segment).Concat(jpeg.Skip(2)).ToArray();
    }

    /// <summary>The complete VP8L chunk (header, payload, padding) of a lossless WebP still.</summary>
    private static byte[] Vp8lChunk(int width, int height, SKColor colour)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(colour);
        using var pixmap = bitmap.PeekPixels()!;
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))!;
        var webp = data.ToArray();
        for (int offset = 12; offset + 8 <= webp.Length;)
        {
            int size = BitConverter.ToInt32(webp, offset + 4);
            int padded = 8 + size + (size & 1);
            if (webp.AsSpan(offset, 4).SequenceEqual("VP8L"u8))
                return webp[offset..(offset + padded)];
            offset += padded;
        }
        throw new InvalidOperationException("Skia's lossless WebP has no VP8L chunk.");
    }

    private static void AddChunk(List<byte> body, string fourCc, byte[] payload)
    {
        body.AddRange(System.Text.Encoding.ASCII.GetBytes(fourCc));
        body.AddRange(BitConverter.GetBytes(payload.Length));
        body.AddRange(payload);
        if ((payload.Length & 1) == 1) body.Add(0);
    }

    private static byte[] UInt24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];
}
