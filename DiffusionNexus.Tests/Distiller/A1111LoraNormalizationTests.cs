using System.Collections.Generic;
using System.IO;
using System.Linq;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Distiller;
using FluentAssertions;

namespace DiffusionNexus.Tests.Distiller;

public class A1111LoraNormalizationTests
{
    // The exact golden string the AI2Go "Save Metadata (Civitai)" node writes (tests/test_a1111.py).
    private const string AI2GoGolden =
        "a red fox, 8k <lora:styleLora:0.8>\n" +
        "Negative prompt: blurry\n" +
        "Steps: 30, Sampler: DPM++ 2M Karras, CFG scale: 6.5, Seed: 12345, Size: 1024x1024, " +
        "Model hash: a1b2c3d4e5, Model: myCkpt, Lora hashes: \"styleLora: 1122aabbcc\", Version: ComfyUI";

    private static string WritePngWithParameters(string parameters)
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"seed_{System.Guid.NewGuid():N}.png");
        File.WriteAllBytes(basePath, MinimalPng());
        var outPath = Path.Combine(Path.GetTempPath(), $"a1111_{System.Guid.NewGuid():N}.png");
        PngMetadataWriter.CopyWithMetadata(basePath, outPath, new() { ["parameters"] = parameters });
        File.Delete(basePath);
        return outPath;
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

    [Fact]
    public void Parses_ai2go_golden_with_clean_prompt_and_extracted_lora()
    {
        var path = WritePngWithParameters(AI2GoGolden);
        try
        {
            var data = new ImageMetadataParser().Parse(path);

            data.HasData.Should().BeTrue();
            data.PositivePrompt.Should().Be("a red fox, 8k");     // <lora:...> removed
            data.PositivePrompt.Should().NotContain("<lora:");
            data.NegativePrompt.Should().Be("blurry");
            data.Loras.Select(l => (l.Name, l.StrengthModel)).Should().Equal(("styleLora", 0.8));
            data.Steps.Should().Be(30);
            data.Cfg.Should().Be(6.5);
            data.Seed.Should().Be(12345);
            data.SamplerName.Should().Be("DPM++ 2M Karras");
            data.Checkpoint.Should().Be("myCkpt");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Round_trip_through_formatter_does_not_duplicate_lora_tokens()
    {
        var path = WritePngWithParameters(AI2GoGolden);
        try
        {
            var data = new ImageMetadataParser().Parse(path);

            var reformatted = A1111MetadataFormatter.Build(
                data, data.PositivePrompt ?? "", data.NegativePrompt, data.Loras, hashes: null);

            // Exactly one <lora:styleLora:...> token, not two.
            System.Text.RegularExpressions.Regex.Matches(reformatted, @"<lora:styleLora:").Count.Should().Be(1);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
