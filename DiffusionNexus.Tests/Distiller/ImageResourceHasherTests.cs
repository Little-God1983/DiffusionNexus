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

            var hasher = new ImageResourceHasher(catalog.Object, _ => Task.FromResult<IReadOnlyList<string>>([]));

            var loras = new List<LoraInfo> { new() { Name = "styleB" } };
            var result = await hasher.ComputeAsync(checkpointStem: null, loras, CancellationToken.None);

            result.LoraHashes.Should().ContainKey("styleB");
            result.LoraHashes["styleB"].Should().Be(ImageResourceHasher.ComputeAutoV2(loraFile));
            result.ModelHash.Should().BeNull();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static Mock<ILoraCatalog> EmptyCatalog()
    {
        var catalog = new Mock<ILoraCatalog>();
        catalog.Setup(c => c.GetInstalledLorasAsync(It.IsAny<IReadOnlyCollection<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AvailableLora>());
        return catalog;
    }

    [Fact]
    public async Task ComputeAsync_uses_db_stored_autov2_for_checkpoint()
    {
        var tracked = new List<TrackedModelFile> { new("krea2TurboFP8_krea2TURBO.safetensors", null, "2d3523507c", null) };
        var hasher = new ImageResourceHasher(EmptyCatalog().Object,
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            _ => Task.FromResult<IReadOnlyList<TrackedModelFile>>(tracked));

        var result = await hasher.ComputeAsync("krea2TurboFP8_krea2TURBO", [], CancellationToken.None);

        result.ModelHash.Should().Be("2d3523507c");
    }

    [Fact]
    public async Task ComputeAsync_derives_checkpoint_autov2_from_db_sha256()
    {
        var sha = new string('a', 64); // full SHA-256; AutoV2 is its first 10 hex chars
        var tracked = new List<TrackedModelFile> { new("base.safetensors", null, null, sha) };
        var hasher = new ImageResourceHasher(EmptyCatalog().Object,
            _ => Task.FromResult<IReadOnlyList<string>>([]),
            _ => Task.FromResult<IReadOnlyList<TrackedModelFile>>(tracked));

        var result = await hasher.ComputeAsync("base", [], CancellationToken.None);

        result.ModelHash.Should().Be(sha[..10]);
    }

    [Fact]
    public async Task ComputeAsync_hashes_db_localpath_when_no_stored_hash()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ckpt_{System.Guid.NewGuid():N}")).FullName;
        var file = Path.Combine(dir, "base.safetensors");
        File.WriteAllText(file, "weights");
        try
        {
            var tracked = new List<TrackedModelFile> { new("base.safetensors", file, null, null) };
            var hasher = new ImageResourceHasher(EmptyCatalog().Object,
                _ => Task.FromResult<IReadOnlyList<string>>([]),
                _ => Task.FromResult<IReadOnlyList<TrackedModelFile>>(tracked));

            var result = await hasher.ComputeAsync("base", [], CancellationToken.None);

            result.ModelHash.Should().Be(ImageResourceHasher.ComputeAutoV2(file));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task ComputeAsync_scans_all_model_roots_when_checkpoint_not_in_db()
    {
        // Checkpoint lives under a NON-primary root's diffusion_models/ — the old single-root scan
        // would miss it. The hasher must search every root it is given.
        var primary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"root1_{System.Guid.NewGuid():N}")).FullName;
        var secondary = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"root2_{System.Guid.NewGuid():N}")).FullName;
        var dm = Directory.CreateDirectory(Path.Combine(secondary, "diffusion_models")).FullName;
        var file = Path.Combine(dm, "krea2TurboFP8_krea2TURBO.safetensors");
        File.WriteAllText(file, "unet-weights");
        try
        {
            var hasher = new ImageResourceHasher(EmptyCatalog().Object,
                _ => Task.FromResult<IReadOnlyList<string>>([primary, secondary]),
                resolveTrackedModelFiles: null);

            var result = await hasher.ComputeAsync("krea2TurboFP8_krea2TURBO", [], CancellationToken.None);

            result.ModelHash.Should().Be(ImageResourceHasher.ComputeAutoV2(file));
        }
        finally { Directory.Delete(primary, true); Directory.Delete(secondary, true); }
    }

    [Fact]
    public async Task ComputeAsync_finds_checkpoint_directly_in_a_category_root()
    {
        // Real-world regression: ComfyUI's extra_model_paths.yaml routes `unet` to a shared library's
        // "DiffusionModels/" folder, and the resolver hands us THAT folder as a root with the file
        // sitting directly inside it (no checkpoints/diffusion_models/unet subfolder). The old
        // fixed-subfolder scan probed <root>/{checkpoints,diffusion_models,unet} only and missed it.
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"DiffusionModels_{System.Guid.NewGuid():N}")).FullName;
        var file = Path.Combine(root, "krea2TurboFP8_krea2TURBO.safetensors");
        File.WriteAllText(file, "unet-weights");
        try
        {
            var hasher = new ImageResourceHasher(EmptyCatalog().Object,
                _ => Task.FromResult<IReadOnlyList<string>>([root]),
                resolveTrackedModelFiles: null);

            var result = await hasher.ComputeAsync("krea2TurboFP8_krea2TURBO", [], CancellationToken.None);

            result.ModelHash.Should().Be(ImageResourceHasher.ComputeAutoV2(file));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ComputeAsync_finds_checkpoint_in_arbitrarily_named_subfolder()
    {
        // A shared-library base_path root whose category folder has a non-canonical name ("DiffusionModels",
        // not "diffusion_models"). Recursive search must find it regardless of the folder's name.
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lib_{System.Guid.NewGuid():N}")).FullName;
        var category = Directory.CreateDirectory(Path.Combine(root, "DiffusionModels")).FullName;
        var file = Path.Combine(category, "some_dit_model.safetensors");
        File.WriteAllText(file, "weights");
        try
        {
            var hasher = new ImageResourceHasher(EmptyCatalog().Object,
                _ => Task.FromResult<IReadOnlyList<string>>([root]),
                resolveTrackedModelFiles: null);

            var result = await hasher.ComputeAsync("some_dit_model", [], CancellationToken.None);

            result.ModelHash.Should().Be(ImageResourceHasher.ComputeAutoV2(file));
        }
        finally { Directory.Delete(root, true); }
    }
}
