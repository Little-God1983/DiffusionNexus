using System.IO;
using System.IO.Compression;
using System.Text;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PngChunkReaderZTxtTests
{
    [Fact]
    public void ReadTextChunks_decompresses_zTXt_chunk()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ztxt_{System.Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllBytes(path, BuildPngWithZTxt("prompt", "hello ztxt"));

            var chunks = PngChunkReader.ReadTextChunks(path);

            chunks.Should().ContainKey("prompt");
            chunks["prompt"].Should().Be("hello ztxt");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    // Minimal PNG: signature + IHDR(13 bytes, dummy) + zTXt + IEND. CRCs are dummy (reader skips them).
    private static byte[] BuildPngWithZTxt(string keyword, string text)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        WriteChunk(ms, "IHDR", new byte[13]); // 13 zero bytes is enough for the reader (it skips IHDR)

        // zTXt data = keyword + 0x00 + compressionMethod(0) + zlib(text)
        using var comp = new MemoryStream();
        comp.Write(Encoding.Latin1.GetBytes(keyword));
        comp.WriteByte(0x00);
        comp.WriteByte(0x00); // compression method: 0 = zlib/deflate
        var raw = Encoding.Latin1.GetBytes(text);
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw);
        WriteChunk(ms, "zTXt", comp.ToArray());

        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]); // dummy CRC — PngChunkReader does not verify it
    }
}
