using System.Reflection;
using DiffusionNexus.Civitai;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Covers <see cref="BaseModelHeaderMap"/> — the safetensors-header-to-Civitai-label mapping.
/// Evaluation order (name hint, then architecture, then version prefix) is load-bearing: Pony,
/// Illustrious and NoobAI are all SDXL-architecture refinements, so a hint match must win over
/// an architecture match or every Pony LoRA would report as plain "SDXL 1.0".
/// </summary>
public class BaseModelHeaderMapTests
{
    private static SafetensorsHeaderInfo NameHint(string hint) => new(null, null, hint);
    private static SafetensorsHeaderInfo Architecture(string architecture) => new(null, architecture, null);
    private static SafetensorsHeaderInfo Version(string version) => new(version, null, null);

    // --- Rung 1: ss_sd_model_name hint ---
    [Theory]
    [InlineData("ponyDiffusionV6XL", "Pony")]
    [InlineData("SomeIllustriousMix", "Illustrious")]
    [InlineData("NoobAI-XL-v1", "NoobAI")]
    public void Map_NameHintRungMapsKnownHints(string hint, string expected)
    {
        BaseModelHeaderMap.Map(NameHint(hint)).Should().Be(expected);
    }

    // --- A5: kohya writes a full PATH into ss_sd_model_name, not a bare file name — only the
    // file-name portion (extension dropped) may be needle-matched, or a directory segment like
    // "...\noobs\base.safetensors" decides the answer instead of the actual base model. ---
    [Theory]
    [InlineData("E:/checkpoints/noobs/base.safetensors")]
    [InlineData(@"E:\checkpoints\noobs\base.safetensors")]
    public void Map_NameHintRungIgnoresDirectorySegments(string hint)
    {
        BaseModelHeaderMap.Map(NameHint(hint)).Should().BeNull();
    }

    [Fact]
    public void Map_NameHintRungPathDirectorySegmentFallsThroughToArchitecture()
    {
        var info = new SafetensorsHeaderInfo(null, "stable-diffusion-xl-v1-base", @"C:\models\ponys\base.safetensors");

        BaseModelHeaderMap.Map(info).Should().Be("SDXL 1.0");
    }

    [Theory]
    [InlineData("ponyDiffusionV6XL")]
    [InlineData(@"E:\Models\Lora\ponyDiffusionV6XL.safetensors")]
    [InlineData("E:/Models/Lora/ponyDiffusionV6XL.safetensors")]
    public void Map_NameHintRungMatchesFileNamePortionRegardlessOfPathStyle(string hint)
    {
        BaseModelHeaderMap.Map(NameHint(hint)).Should().Be("Pony");
    }

    // --- Rung 2: modelspec.architecture, everything from the first '/' stripped ---
    [Theory]
    [InlineData("stable-diffusion-xl-v1-base", "SDXL 1.0")]
    [InlineData("stable-diffusion-xl-v1-base/lora", "SDXL 1.0")]
    [InlineData("stable-diffusion-v1", "SD 1.5")]
    [InlineData("stable-diffusion-v2-768-v", "SD 2.1")]
    [InlineData("stable-diffusion-v2", "SD 2.0")]
    [InlineData("stable-diffusion-3-medium", "SD 3")]
    [InlineData("flux-1-dev", "Flux.1 D")]
    [InlineData("flux-1-schnell", "Flux.1 S")]
    public void Map_ArchitectureRungMapsKnownArchitectures(string architecture, string expected)
    {
        BaseModelHeaderMap.Map(Architecture(architecture)).Should().Be(expected);
    }

    // --- Rung 3: ss_base_model_version, lowercase prefix match ---
    [Theory]
    [InlineData("sdxl_base_v1-0", "SDXL 1.0")]
    [InlineData("sdxl_base_v0-9", "SDXL 0.9")]
    [InlineData("sd_v1", "SD 1.5")]
    [InlineData("sd_v1_something_else", "SD 1.5")]
    [InlineData("sd_v2", "SD 2.1")]
    public void Map_VersionPrefixRungMapsKnownPrefixes(string version, string expected)
    {
        BaseModelHeaderMap.Map(Version(version)).Should().Be(expected);
    }

    [Fact]
    public void Map_NameHintBeatsArchitecture()
    {
        var info = new SafetensorsHeaderInfo(null, "stable-diffusion-xl-v1-base/lora", "ponyDiffusionV6XL");

        BaseModelHeaderMap.Map(info).Should().Be("Pony");
    }

    [Fact]
    public void Map_ArchitectureBeatsVersionString()
    {
        var info = new SafetensorsHeaderInfo("sd_v1", "flux-1-dev", null);

        BaseModelHeaderMap.Map(info).Should().Be("Flux.1 D");
    }

    [Fact]
    public void Map_UnknownEverythingReturnsNull()
    {
        var info = new SafetensorsHeaderInfo("totally-unknown", "totally/unknown", "totally unknown");

        BaseModelHeaderMap.Map(info).Should().BeNull();
    }

    [Fact]
    public void Map_AllFieldsNullReturnsNull()
    {
        var info = new SafetensorsHeaderInfo(null, null, null);

        BaseModelHeaderMap.Map(info).Should().BeNull();
    }

    /// <summary>
    /// Reflects over <see cref="CivitaiBaseModelCatalog"/>'s private bundled snapshot (rather than
    /// duplicating the label list here) and asserts every label the map can ever return is a real
    /// Civitai display label, not a typo'd string nobody's dropdown recognizes.
    /// </summary>
    [Fact]
    public void Map_EveryOutputIsACatalogLabel()
    {
        var bundledSnapshotField = typeof(CivitaiBaseModelCatalog)
            .GetField("BundledSnapshot", BindingFlags.NonPublic | BindingFlags.Static);
        bundledSnapshotField.Should().NotBeNull(
            "CivitaiBaseModelCatalog.BundledSnapshot must exist for this check to mean anything");

        var bundledSnapshot = (IReadOnlyList<string>)bundledSnapshotField!.GetValue(null)!;

        BaseModelHeaderMap.AllLabels.Should().NotBeEmpty();
        foreach (var label in BaseModelHeaderMap.AllLabels)
        {
            bundledSnapshot.Should().Contain(label, $"'{label}' must be a real Civitai base-model label");
        }
    }
}
