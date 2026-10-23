using System.Buffers.Binary;
using DiffusionNexus.Service.Services.Sync.Identity;
using FluentAssertions;
using static DiffusionNexus.Tests.Sync.Service.Identity.SafetensorsFixture;

namespace DiffusionNexus.Tests.Sync.Service.Identity;

/// <summary>
/// Covers <see cref="SafetensorsHeaderReader"/> — the exact-path, never-throws safetensors
/// JSON-header probe later tasks use to identify a model's base model from the file itself.
/// </summary>
public class SafetensorsHeaderReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dn-shr-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task TryReadAsync_ExtractsTheThreeMetadataKeys()
    {
        var bytes = Safetensors(Meta(
            ("ss_base_model_version", "sdxl_base_v1-0"),
            ("modelspec.architecture", "stable-diffusion-xl-v1-base/lora"),
            ("ss_sd_model_name", "ponyDiffusionV6XL")));
        var path = WriteFile("model.safetensors", bytes);

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.BaseModelVersion.Should().Be("sdxl_base_v1-0");
        info.Architecture.Should().Be("stable-diffusion-xl-v1-base/lora");
        info.ModelNameHint.Should().Be("ponyDiffusionV6XL");
    }

    /// <summary>
    /// B1. <c>.sft</c> is the standard short alias for the same container —
    /// <see cref="DiffusionNexus.Service.Services.Sync.Identity.FilenameBaseModelHeuristic"/>'s own
    /// known-model-extension list already treats it as one — so a readable header under that
    /// extension must not fall through to the filename guess.
    /// </summary>
    [Fact]
    public async Task TryReadAsync_SftExtensionExtractsTheThreeMetadataKeys()
    {
        var bytes = Safetensors(Meta(
            ("ss_base_model_version", "sdxl_base_v1-0"),
            ("modelspec.architecture", "stable-diffusion-xl-v1-base/lora"),
            ("ss_sd_model_name", "ponyDiffusionV6XL")));
        var path = WriteFile("model.sft", bytes);

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.BaseModelVersion.Should().Be("sdxl_base_v1-0");
        info.Architecture.Should().Be("stable-diffusion-xl-v1-base/lora");
        info.ModelNameHint.Should().Be("ponyDiffusionV6XL");
    }

    [Fact]
    public async Task TryReadAsync_HeaderWithoutMetadataBlockIsASuccessfulEmptyRead()
    {
        var bytes = Safetensors("""{"tensor.weight":{"dtype":"F16","shape":[4],"data_offsets":[0,8]}}""");
        var path = WriteFile("nometa.safetensors", bytes);

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.BaseModelVersion.Should().BeNull();
        info.Architecture.Should().BeNull();
        info.ModelNameHint.Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_TruncatedFileReturnsNull()
    {
        var buffer = new byte[40];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 500UL);
        var path = WriteFile("truncated.safetensors", buffer);

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_OversizedHeaderReturnsNull()
    {
        var buffer = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)(SafetensorsHeaderReader.MaxHeaderBytes + 1));
        var path = WriteFile("oversized.safetensors", buffer);

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_GarbageJsonReturnsNull()
    {
        var bytes = Safetensors("not json{{");
        var path = WriteFile("garbage.safetensors", bytes);

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_WrongExtensionReturnsNull()
    {
        var bytes = Safetensors(Meta(
            ("ss_base_model_version", "sdxl_base_v1-0"),
            ("modelspec.architecture", "stable-diffusion-xl-v1-base/lora"),
            ("ss_sd_model_name", "ponyDiffusionV6XL")));
        var path = WriteFile("model.ckpt", bytes);

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_MissingFileReturnsNull()
    {
        var path = Path.Combine(_dir, "gone.safetensors");

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    [Fact]
    public async Task TryReadAsync_EmptyLengthReturnsNull()
    {
        var buffer = new byte[8];
        var path = WriteFile("empty.safetensors", buffer);

        (await SafetensorsHeaderReader.TryReadAsync(path)).Should().BeNull();
    }

    /// <summary>
    /// B2. A cancellation must surface as <see cref="OperationCanceledException"/> rather than being
    /// swallowed into <c>null</c> by the catch-all — the identify step needs to tell "cancelled" apart
    /// from "unreadable".
    /// </summary>
    [Fact]
    public async Task TryReadAsync_CancellationPropagates()
    {
        var bytes = Safetensors(Meta(("ss_base_model_version", "sdxl_base_v1-0")));
        var path = WriteFile("cancelled.safetensors", bytes);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions
            .Awaiting(() => SafetensorsHeaderReader.TryReadAsync(path, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>("a cancelled read is not an unreadable file");
    }

    /// <summary>
    /// The tensor key names are the only thing in a safetensors file that says what it actually is,
    /// and the reader was already parsing and discarding them. A VAE extracted from a checkpoint
    /// has no __metadata__ block at all, so the keys are the ONLY signal it carries.
    /// </summary>
    [Fact]
    public async Task ReadsTheTensorKeyNames()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, Safetensors(
            Tensors("encoder.down.0.block.0.norm1.weight", "post_quant_conv.weight")));

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.Keys.Should().BeEquivalentTo(
            ["encoder.down.0.block.0.norm1.weight", "post_quant_conv.weight"]);
    }

    /// <summary>
    /// A checkpoint has thousands of tensors and the sample exists to bound memory, not to be
    /// complete: a container's keys are homogeneous, so the first 64 answer the same question the
    /// full set would.
    /// </summary>
    [Fact]
    public async Task SamplesTheTensorKeysRatherThanKeepingThemAll()
    {
        var keys = Enumerable.Range(0, SafetensorsHeaderReader.MaxSampledTensorKeys + 40)
            .Select(i => $"block.{i}.weight")
            .ToArray();
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, Safetensors(Tensors(keys)));

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.Keys.Should().HaveCount(SafetensorsHeaderReader.MaxSampledTensorKeys);
    }

    /// <summary>The metadata block is not a tensor and must never appear among the keys.</summary>
    [Fact]
    public async Task TheMetadataBlockIsNotATensorKey()
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.safetensors");
        await File.WriteAllBytesAsync(path, Safetensors(
            MetaAndTensors([("modelspec.architecture", "flux-1-dev")], "lora_unet_0.lora_up.weight")));

        var info = await SafetensorsHeaderReader.TryReadAsync(path);

        info.Should().NotBeNull();
        info!.Keys.Should().ContainSingle().Which.Should().Be("lora_unet_0.lora_up.weight");
        info.Architecture.Should().Be("flux-1-dev", "the existing metadata rungs must keep working");
    }
}
