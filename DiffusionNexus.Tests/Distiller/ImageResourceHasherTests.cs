using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models;
using DiffusionNexus.UI.Services.Distiller;
using DiffusionNexus.UI.Services.Lora;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Distiller;

public class ImageResourceHasherTests
{
    [Fact]
    public void ComputeAutoV2_is_first_10_hex_of_sha256()
    {
        var path = Path.Combine(Path.GetTempPath(), $"h_{System.Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllText(path, "content");
            var expected = System.Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant()[..10];

            ImageResourceHasher.ComputeAutoV2(path).Should().Be(expected);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ComputeAsync_hashes_lora_found_in_catalog()
    {
        // The file's STEM must equal the trace's LoRA name ("styleB"); use a unique dir, real name.
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lora_{System.Guid.NewGuid():N}")).FullName;
        var loraFile = Path.Combine(dir, "styleB.safetensors");
        File.WriteAllText(loraFile, "weights");
        try
        {
            var catalog = new Mock<ILoraCatalog>();
            catalog.Setup(c => c.GetInstalledLorasAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<AvailableLora> { new("styleB", loraFile, null, null) });

            var hasher = new ImageResourceHasher(catalog.Object, _ => Task.FromResult<string?>(null));

            var loras = new List<LoraInfo> { new() { Name = "styleB" } };
            var result = await hasher.ComputeAsync(checkpointStem: null, loras, CancellationToken.None);

            result.LoraHashes.Should().ContainKey("styleB");
            result.LoraHashes["styleB"].Should().Be(ImageResourceHasher.ComputeAutoV2(loraFile));
            result.ModelHash.Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
