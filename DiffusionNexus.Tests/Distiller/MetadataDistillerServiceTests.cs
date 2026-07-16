using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Models.Distiller;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Distiller;

public class MetadataDistillerServiceTests
{
    private static string MakePng()
    {
        // Seed a PNG that carries a ComfyUI "prompt"/"workflow" chunk so we can prove stripping.
        var basePath = Path.Combine(Path.GetTempPath(), $"seed_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var src = Path.Combine(Path.GetTempPath(), $"in_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, src, new() { ["prompt"] = "{...}", ["workflow"] = "{...}" });
        File.Delete(basePath);
        return src;
    }

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
        s.Write(len); s.Write(System.Text.Encoding.ASCII.GetBytes(type)); s.Write(data); s.Write(new byte[4]);
    }

    private static MetadataDistillerService NewService()
    {
        var catalog = new Mock<ILoraCatalog>();
        catalog.Setup(c => c.GetInstalledLorasAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailableLora>());
        return new MetadataDistillerService(new ImageResourceHasher(catalog.Object, _ => Task.FromResult<IReadOnlyList<string>>([])));
    }

    [Fact]
    public async Task DistillAsync_writes_cleaned_copy_with_parameters_and_strips_workflow()
    {
        var src = MakePng();
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { Steps = 20, Cfg = 7, Seed = 1, SamplerName = "euler", Scheduler = "normal", Width = 512, Height = 512, HasData = true };
            var item = new DistillItem(src, data, "a cat, masterpiece", "blurry", []);
            var del = new PromptRuleSet { Kind = RuleKind.Delete, DeleteWords = ["masterpiece"] };
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = true };

            var result = await NewService().DistillAsync([item], [del], options, progress: null, CancellationToken.None);

            result.Written.Should().Be(1);
            result.Failures.Should().BeEmpty();

            var outFile = Path.Combine(outDir, Path.GetFileName(src));
            File.Exists(outFile).Should().BeTrue();
            var chunks = PngChunkReader.ReadTextChunks(outFile);
            chunks.Should().NotContainKey("prompt");
            chunks.Should().NotContainKey("workflow");
            chunks["parameters"].Should().Contain("a cat");
            chunks["parameters"].Should().NotContain("masterpiece"); // rule applied
        }
        finally { Directory.Delete(outDir, true); File.Delete(src); }
    }

    [Fact]
    public async Task DistillAsync_resize_option_downscales_and_updates_size_line()
    {
        // A real decodable PNG carrying ComfyUI chunks, 256x128 → capped at 64 → 64x32.
        var seeded = PngReencoderTests.MakeRealPng(256, 128);
        var src = Path.Combine(Path.GetTempPath(), $"in_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(seeded, src, new() { ["prompt"] = "{...}", ["workflow"] = "{...}" });
        File.Delete(seeded);
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { Steps = 20, HasData = true, Width = 256, Height = 128 };
            var item = new DistillItem(src, data, "a cat", null, []);
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = true, ResizeMaxDimension = 64 };

            var result = await NewService().DistillAsync([item], [], options, null, CancellationToken.None);

            result.Written.Should().Be(1);
            var outFile = Path.Combine(outDir, Path.GetFileName(src));

            using var decoded = SkiaSharp.SKBitmap.Decode(outFile);
            (decoded!.Width, decoded.Height).Should().Be((64, 32));

            var chunks = PngChunkReader.ReadTextChunks(outFile);
            chunks["parameters"].Should().Contain("Size: 64x32"); // Size reflects the resized output
            chunks.Should().NotContainKey("workflow");
        }
        finally { Directory.Delete(outDir, true); File.Delete(src); }
    }

    [Fact]
    public async Task DistillAsync_reencode_preserves_workflow_chunks_when_not_stripping()
    {
        var seeded = PngReencoderTests.MakeRealPng(128, 128);
        var src = Path.Combine(Path.GetTempPath(), $"in_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(seeded, src, new() { ["prompt"] = "{p}", ["workflow"] = "{w}" });
        File.Delete(seeded);
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { HasData = true, Width = 128, Height = 128 };
            var item = new DistillItem(src, data, "p", null, []);
            // Recompress forces a fresh encode (which has no text chunks) — keeping the workflow
            // must survive that by carrying the source's chunks over.
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = false, RecompressPng = true };

            var result = await NewService().DistillAsync([item], [], options, null, CancellationToken.None);

            result.Written.Should().Be(1);
            var chunks = PngChunkReader.ReadTextChunks(Path.Combine(outDir, Path.GetFileName(src)));
            chunks["workflow"].Should().Be("{w}");
            chunks["prompt"].Should().Be("{p}");
            chunks["parameters"].Should().Contain("p");
        }
        finally { Directory.Delete(outDir, true); File.Delete(src); }
    }

    [Fact]
    public async Task DistillAsync_deduplicates_output_names()
    {
        var a = MakePng();
        var outDir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"out_{System.Guid.NewGuid():N}")).FullName;
        try
        {
            var data = new ImageGenerationData { HasData = true, Width = 8, Height = 8 };
            var item = new DistillItem(a, data, "p", null, []);
            var options = new DistillOptions { OutputFolder = outDir, StripWorkflow = false };

            // Run twice targeting the same output folder → second must not overwrite the first.
            await NewService().DistillAsync([item], [], options, null, CancellationToken.None);
            await NewService().DistillAsync([item], [], options, null, CancellationToken.None);

            Directory.GetFiles(outDir).Length.Should().Be(2);
        }
        finally { Directory.Delete(outDir, true); File.Delete(a); }
    }
}
