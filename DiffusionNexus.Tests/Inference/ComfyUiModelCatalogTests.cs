using DiffusionNexus.Inference.Models;
using DiffusionNexus.Inference.StableDiffusionCpp;
using FluentAssertions;

namespace DiffusionNexus.Tests.Inference;

public class ComfyUiModelCatalogTests : IDisposable
{
    private readonly string _root;

    public ComfyUiModelCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "dn-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string CreateFile(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[] { 0x42 });
        return full;
    }

    [Fact]
    public void Ctor_NullRoots_Throws()
    {
        var act = () => new ComfyUiModelCatalog((IEnumerable<string>)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NoValidRoots_Throws()
    {
        var act = () => new ComfyUiModelCatalog(new[] { "", "  ", null! });
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Ctor_DeduplicatesRoots_CaseInsensitively()
    {
        var sut = new ComfyUiModelCatalog(new[] { _root, _root.ToUpperInvariant() });

        sut.ModelsRoots.Should().HaveCount(1);
    }

    [Fact]
    public void ListAvailable_NoFilesPresent_ReturnsEmpty()
    {
        var sut = new ComfyUiModelCatalog(_root);

        sut.ListAvailable().Should().BeEmpty();
    }

    [Fact]
    public void ListAvailable_PartialFiles_DoesNotIncludeZImageTurbo()
    {
        CreateFile(Path.Combine("DiffusionModels", "z_image_turbo_bf16.safetensors"));
        CreateFile(Path.Combine("VAE", "ae.safetensors"));
        // missing qwen_3_4b.safetensors

        var sut = new ComfyUiModelCatalog(_root);

        sut.ListAvailable().Should().BeEmpty();
    }

    [Fact]
    public void ListAvailable_AllZImageTurboFilesPresent_ReturnsDescriptor()
    {
        var unet = CreateFile(Path.Combine("DiffusionModels", "z_image_turbo_bf16.safetensors"));
        var clip = CreateFile(Path.Combine("TextEncoders", "qwen_3_4b.safetensors"));
        var vae = CreateFile(Path.Combine("VAE", "ae.safetensors"));

        var sut = new ComfyUiModelCatalog(_root);

        var list = sut.ListAvailable();
        list.Should().ContainSingle();
        var d = list[0];
        d.Key.Should().Be(ModelKeys.ZImageTurbo);
        d.Kind.Should().Be(ModelKind.ZImageTurbo);
        d.DiffusionModelPath.Should().Be(unet);
        d.VaePath.Should().Be(vae);
        d.TextEncoders[TextEncoderSlot.Llm].Should().Be(clip);
        d.DefaultSteps.Should().Be(9);
        d.DefaultCfg.Should().Be(1.0f);
        d.DefaultWidth.Should().Be(1024);
        d.DefaultHeight.Should().Be(1024);
    }

    [Fact]
    public void ListAvailable_IsCachedAcrossCalls()
    {
        CreateFile(Path.Combine("DiffusionModels", "z_image_turbo_bf16.safetensors"));
        CreateFile(Path.Combine("TextEncoders", "qwen_3_4b.safetensors"));
        CreateFile(Path.Combine("VAE", "ae.safetensors"));

        var sut = new ComfyUiModelCatalog(_root);

        var first = sut.ListAvailable();
        var second = sut.ListAvailable();

        ReferenceEquals(first, second).Should().BeTrue();
    }

    [Fact]
    public void Invalidate_ResetsCacheAndSearchedLocationCount()
    {
        var sut = new ComfyUiModelCatalog(_root);
        _ = sut.ListAvailable();
        sut.SearchedLocationCount.Should().BeGreaterThan(0);

        sut.Invalidate();

        sut.SearchedLocationCount.Should().Be(0);
        var second = sut.ListAvailable();
        second.Should().NotBeNull();
    }

    [Fact]
    public void TryGet_ReturnsDescriptor_WhenKeyMatches()
    {
        CreateFile(Path.Combine("DiffusionModels", "z_image_turbo_bf16.safetensors"));
        CreateFile(Path.Combine("TextEncoders", "qwen_3_4b.safetensors"));
        CreateFile(Path.Combine("VAE", "ae.safetensors"));

        var sut = new ComfyUiModelCatalog(_root);

        sut.TryGet(ModelKeys.ZImageTurbo).Should().NotBeNull();
        sut.TryGet("does-not-exist").Should().BeNull();
    }

    [Fact]
    public void FirstRootWins_WhenSameFileExistsInMultipleRoots()
    {
        var second = Path.Combine(Path.GetTempPath(), "dn-catalog-2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(second);
        try
        {
            // primary (_root) has all three files
            var primaryUnet = CreateFile(Path.Combine("DiffusionModels", "z_image_turbo_bf16.safetensors"));
            CreateFile(Path.Combine("TextEncoders", "qwen_3_4b.safetensors"));
            CreateFile(Path.Combine("VAE", "ae.safetensors"));

            // secondary also has the unet
            var secondaryUnet = Path.Combine(second, "DiffusionModels", "z_image_turbo_bf16.safetensors");
            Directory.CreateDirectory(Path.GetDirectoryName(secondaryUnet)!);
            File.WriteAllBytes(secondaryUnet, new byte[] { 0x99 });

            var sut = new ComfyUiModelCatalog(new[] { _root, second });

            sut.TryGet(ModelKeys.ZImageTurbo)!.DiffusionModelPath.Should().Be(primaryUnet);
        }
        finally
        {
            try { Directory.Delete(second, true); } catch { }
        }
    }

    [Fact]
    public void NonExistentRoot_IsSilentlyIgnored()
    {
        var missing = Path.Combine(Path.GetTempPath(), "dn-missing-" + Guid.NewGuid().ToString("N"));

        var sut = new ComfyUiModelCatalog(new[] { missing });

        sut.ListAvailable().Should().BeEmpty();
    }
}
