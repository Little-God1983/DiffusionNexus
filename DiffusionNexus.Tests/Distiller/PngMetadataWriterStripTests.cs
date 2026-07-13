using System.Collections.Generic;
using System.IO;
using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class PngMetadataWriterStripTests
{
    private static string TempPng(Dictionary<string, string> chunks)
    {
        // Build a valid-enough PNG by writing chunks via the writer itself from a bare base.
        var basePath = Path.Combine(Path.GetTempPath(), $"base_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var outPath = Path.Combine(Path.GetTempPath(), $"src_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, outPath, chunks); // seeds the chunks
        File.Delete(basePath);
        return outPath;
    }

    // 8-byte signature + IHDR(13) + IEND, real CRCs not required by the reader; writer needs a valid IHDR-first PNG.
    private static byte[] MinimalPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(ms, "IHDR", new byte[13]);
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void WriteChunk(Stream s, string type, byte[] data)
    {
        var len = System.BitConverter.GetBytes(data.Length);
        if (System.BitConverter.IsLittleEndian) System.Array.Reverse(len);
        s.Write(len);
        s.Write(System.Text.Encoding.ASCII.GetBytes(type));
        s.Write(data);
        s.Write(new byte[4]);
    }

    [Fact]
    public void Strip_true_removes_prompt_and_workflow_and_keeps_only_new_parameters()
    {
        var src = TempPng(new() { ["prompt"] = "{...}", ["workflow"] = "{...}" });
        var dst = Path.Combine(Path.GetTempPath(), $"dst_{System.Guid.NewGuid():N}.png");
        try
        {
            PngMetadataWriter.CopyWithMetadata(src, dst, new() { ["parameters"] = "clean" }, stripExisting: true);

            var chunks = PngChunkReader.ReadTextChunks(dst);
            chunks.Should().ContainKey("parameters");
            chunks["parameters"].Should().Be("clean");
            chunks.Should().NotContainKey("prompt");
            chunks.Should().NotContainKey("workflow");
        }
        finally { foreach (var p in new[] { src, dst }) if (File.Exists(p)) File.Delete(p); }
    }

    [Fact]
    public void Strip_false_keeps_prompt_and_workflow_but_replaces_parameters()
    {
        var src = TempPng(new() { ["prompt"] = "{keepme}", ["workflow"] = "{keepme}", ["parameters"] = "old" });
        var dst = Path.Combine(Path.GetTempPath(), $"dst_{System.Guid.NewGuid():N}.png");
        try
        {
            PngMetadataWriter.CopyWithMetadata(src, dst, new() { ["parameters"] = "new" }, stripExisting: false);

            var chunks = PngChunkReader.ReadTextChunks(dst);
            chunks["prompt"].Should().Be("{keepme}");
            chunks["workflow"].Should().Be("{keepme}");
            chunks["parameters"].Should().Be("new"); // replaced, not duplicated
        }
        finally { foreach (var p in new[] { src, dst }) if (File.Exists(p)) File.Delete(p); }
    }
}
