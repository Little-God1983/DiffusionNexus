using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// The precedence rule, in one place because three callers depend on it: discovery, the identify
/// step, and the sorter's own resolver.
/// </summary>
public sealed class AssetKindResolverTests
{
    private static SafetensorsHeaderInfo Header(params string[] keys) => new(null, null, null, keys);

    /// <summary>
    /// The whole point of reading the weights. The issue named this exact hazard: a LoRA called
    /// "vae_finetune_lora" is a LoRA, and the name rung must never get to see it.
    /// </summary>
    [Fact]
    public void AHeaderProvingALoraBeatsAFileNameSayingVae()
        => AssetKindResolver.Resolve(
                Header("lora_unet_blocks_0.lora_up.weight"),
                "vae_finetune_lora.safetensors")
            .Should().Be(ModelType.LORA);

    [Fact]
    public void AHeaderProvingAVaeBeatsAnUninformativeName()
        => AssetKindResolver.Resolve(Header("post_quant_conv.weight"), "BRFHE7KV2VWXY8N3D4SXR4XCT0.safetensors")
            .Should().Be(ModelType.VAE);

    /// <summary>
    /// A .pth pickle has no readable header, so the name is all there is — which is why the name
    /// rung still exists and why every real upscaler in the reference library is a .pth.
    /// </summary>
    [Fact]
    public void WithNoReadableHeaderTheNameDecides()
        => AssetKindResolver.Resolve(header: null, "4x-UltraSharp.pth").Should().Be(ModelType.Upscaler);

    /// <summary>
    /// A header that parsed but recognizes nothing is not an answer — fall through to the name
    /// rather than treat "the keys said nothing" as "it is a LoRA".
    /// </summary>
    [Fact]
    public void AnUnrecognizedHeaderFallsThroughToTheName()
        => AssetKindResolver.Resolve(Header("model.diffusion_model.input_blocks.0.0.weight"),
                "Wan2_2_VAE_bf16.safetensors")
            .Should().Be(ModelType.VAE);

    [Fact]
    public void WhenNothingSaysAnythingItIsALora()
        => AssetKindResolver.Resolve(header: null, "MyChar_Pony_v2.safetensors").Should().Be(ModelType.LORA);

    [Fact]
    public async Task ResolveAsyncReadsARealFileHeader()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("post_quant_conv.weight")));

        try
        {
            (await AssetKindResolver.ResolveAsync(path)).Should().Be(ModelType.VAE);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The real open-failure path: a .safetensors container that cannot be opened at all. This must
    /// not throw into a discovery loop — Tasks 6/8/10 call this over whole user libraries, where a
    /// file locked by a running backend, mid-copy onto a NAS, or deleted mid-scan is routine.
    /// TryReadAsync's catch-all answers null.
    /// <para>
    /// It must also not let the NAME answer instead. An unreadable container is not evidence: we
    /// learned nothing about this file, and "we learned nothing" is not grounds for the one verdict
    /// the user cannot undo. A wrong support-asset stamp makes a real LoRA both invisible in the
    /// Viewer (<c>ModelFileSyncService.IsLoraFamily</c>) and unselectable by any bulk sync
    /// (<c>SyncStateRepository</c>'s <c>LoraFamily</c> filter) — recoverable only by hand-editing the
    /// database. Discovery is often the first thing ever to open the file, so there is no earlier
    /// verdict to fall back on either. LORA leaves the row exactly where the next readable pass can
    /// still correct it.
    /// </para>
    /// <para>
    /// This test previously asserted <c>VAE</c> — the whole-branch review's Critical #1. Same
    /// fixture, inverted expectation.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ResolveAsyncStaysALoraWhenASafetensorsFileCannotBeOpened()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "Wan2_2_VAE_bf16.safetensors");

        (await AssetKindResolver.ResolveAsync(missing)).Should().Be(ModelType.LORA);
    }

    /// <summary>
    /// The case the guard above must NOT swallow: a .safetensors whose header we genuinely read,
    /// which matched no rung. That is evidence saying nothing, not an absence of evidence, so the
    /// name still decides — and it has to, because <see cref="AssetKindHeaderMap"/> deliberately has
    /// no upscaler rung (an upscaler's keys are ordinary conv stacks), so a safetensors upscaler can
    /// only ever be named from its file name.
    /// </summary>
    [Fact]
    public async Task ResolveAsyncStillNamesASafetensorsWhoseHeaderWasReadButProvedNothing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_4x-UltraSharp.safetensors");
        await File.WriteAllBytesAsync(path, SafetensorsFixture.Safetensors(
            SafetensorsFixture.Tensors("body.0.rdb1.conv1.weight", "conv_first.weight")));

        try
        {
            (await AssetKindResolver.ResolveAsync(path)).Should().Be(ModelType.Upscaler);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A .pth pickle short-circuits earlier still — TryReadAsync rejects the extension before it
    /// opens anything — which is why upscalers, which ship almost exclusively as .pth, can only
    /// ever be named from their file name.
    /// </summary>
    [Fact]
    public async Task APickleIsNamedFromItsFileNameWithoutAnyFileAccess()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}", "4xLSDIRplus.pth");

        (await AssetKindResolver.ResolveAsync(missing)).Should().Be(ModelType.Upscaler);
    }
}
