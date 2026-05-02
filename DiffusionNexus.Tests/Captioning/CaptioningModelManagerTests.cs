using DiffusionNexus.Captioning;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Captioning;

public class CaptioningModelManagerTests : IDisposable
{
    private readonly string _modelsRoot;

    public CaptioningModelManagerTests()
    {
        _modelsRoot = Path.Combine(Path.GetTempPath(), "dn-cap-models-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_modelsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_modelsRoot, recursive: true); } catch { }
    }

    private CaptioningModelManager CreateSut() =>
        new(_modelsRoot, new HttpClient());

    [Fact]
    public void Ctor_CreatesModelsDirectory_IfMissing()
    {
        var path = Path.Combine(_modelsRoot, "subdir");
        Directory.Exists(path).Should().BeFalse();

        _ = new CaptioningModelManager(path, new HttpClient());

        Directory.Exists(path).Should().BeTrue();
    }

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B, "llava-v1.6-34b.Q4_K_M.gguf")]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B, "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf")]
    [InlineData(CaptioningModelType.Qwen3_VL_8B, "Qwen3VL-8B-Instruct-Q4_K_M.gguf")]
    public void GetModelPath_ReturnsExpectedFileName(CaptioningModelType type, string expectedFile)
    {
        var sut = CreateSut();

        sut.GetModelPath(type).Should().Be(Path.Combine(_modelsRoot, expectedFile));
    }

    [Theory]
    [InlineData(CaptioningModelType.LLaVA_v1_6_34B, "mmproj-model-f16.gguf")]
    [InlineData(CaptioningModelType.Qwen2_5_VL_7B, "mmproj-Qwen2.5-VL-7B-F16.gguf")]
    [InlineData(CaptioningModelType.Qwen3_VL_8B, "mmproj-Qwen3VL-8B-Instruct-F16.gguf")]
    public void GetClipProjectorPath_ReturnsExpectedFileName(CaptioningModelType type, string expectedFile)
    {
        var sut = CreateSut();

        sut.GetClipProjectorPath(type).Should().Be(Path.Combine(_modelsRoot, expectedFile));
    }

    [Fact]
    public void GetExpectedModelSize_ReturnsPositiveValuesForAllModels()
    {
        var sut = CreateSut();

        foreach (var type in Enum.GetValues<CaptioningModelType>())
        {
            sut.GetExpectedModelSize(type).Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void GetModelPath_InvalidEnumValue_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.GetModelPath((CaptioningModelType)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetClipProjectorPath_InvalidEnumValue_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.GetClipProjectorPath((CaptioningModelType)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetExpectedModelSize_InvalidEnumValue_Throws()
    {
        var sut = CreateSut();

        var act = () => sut.GetExpectedModelSize((CaptioningModelType)99);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void GetDisplayName_ReturnsHumanReadableNames()
    {
        CaptioningModelManager.GetDisplayName(CaptioningModelType.LLaVA_v1_6_34B).Should().Be("LLaVA v1.6 34B");
        CaptioningModelManager.GetDisplayName(CaptioningModelType.Qwen2_5_VL_7B).Should().Be("Qwen 2.5 VL 7B");
        CaptioningModelManager.GetDisplayName(CaptioningModelType.Qwen3_VL_8B).Should().Be("Qwen 3 VL 8B");
    }

    [Fact]
    public void GetDescription_ReturnsNonEmptyForAllModels()
    {
        foreach (var type in Enum.GetValues<CaptioningModelType>())
        {
            CaptioningModelManager.GetDescription(type).Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void GetModelStatus_NoFiles_ReturnsNotDownloaded()
    {
        var sut = CreateSut();

        sut.GetModelStatus(CaptioningModelType.Qwen3_VL_8B).Should().Be(CaptioningModelStatus.NotDownloaded);
    }

    [Fact]
    public void GetModelStatus_OnlyOneFilePresent_ReturnsNotDownloaded()
    {
        var sut = CreateSut();
        File.WriteAllBytes(sut.GetModelPath(CaptioningModelType.Qwen3_VL_8B), new byte[1024]);

        // CLIP projector still missing
        sut.GetModelStatus(CaptioningModelType.Qwen3_VL_8B).Should().Be(CaptioningModelStatus.NotDownloaded);
    }

    [Fact]
    public void GetModelStatus_FilesPresentButTooSmall_ReturnsCorrupted()
    {
        var sut = CreateSut();
        File.WriteAllBytes(sut.GetModelPath(CaptioningModelType.Qwen3_VL_8B), new byte[1024]);
        File.WriteAllBytes(sut.GetClipProjectorPath(CaptioningModelType.Qwen3_VL_8B), new byte[1024]);

        sut.GetModelStatus(CaptioningModelType.Qwen3_VL_8B).Should().Be(CaptioningModelStatus.Corrupted);
    }

    [Fact]
    public void GetModelInfo_ReturnsExpectedShape()
    {
        var sut = CreateSut();

        var info = sut.GetModelInfo(CaptioningModelType.Qwen2_5_VL_7B);

        info.ModelType.Should().Be(CaptioningModelType.Qwen2_5_VL_7B);
        info.Status.Should().Be(CaptioningModelStatus.NotDownloaded);
        info.FilePath.Should().Be(sut.GetModelPath(CaptioningModelType.Qwen2_5_VL_7B));
        info.FileSizeBytes.Should().Be(0);
        info.ExpectedSizeBytes.Should().BeGreaterThan(0);
        info.DisplayName.Should().Be("Qwen 2.5 VL 7B");
        info.Description.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task DownloadModelAsync_AlreadyReady_ReturnsTrueWithoutDownloading()
    {
        var sut = CreateSut();
        var modelPath = sut.GetModelPath(CaptioningModelType.Qwen2_5_VL_7B);
        var clipPath = sut.GetClipProjectorPath(CaptioningModelType.Qwen2_5_VL_7B);
        var expectedSize = sut.GetExpectedModelSize(CaptioningModelType.Qwen2_5_VL_7B);

        // Create files large enough to pass the >=80% size check
        WriteSparseFile(modelPath, expectedSize);
        WriteSparseFile(clipPath, 1_500_000_000);

        sut.GetModelStatus(CaptioningModelType.Qwen2_5_VL_7B).Should().Be(CaptioningModelStatus.Ready);

        var ok = await sut.DownloadModelAsync(CaptioningModelType.Qwen2_5_VL_7B);

        ok.Should().BeTrue();
    }

    [Fact]
    public void DeleteModel_RemovesBothFiles_IfPresent()
    {
        var sut = CreateSut();
        var modelPath = sut.GetModelPath(CaptioningModelType.Qwen3_VL_8B);
        var clipPath = sut.GetClipProjectorPath(CaptioningModelType.Qwen3_VL_8B);
        File.WriteAllBytes(modelPath, new byte[16]);
        File.WriteAllBytes(clipPath, new byte[16]);

        sut.DeleteModel(CaptioningModelType.Qwen3_VL_8B);

        File.Exists(modelPath).Should().BeFalse();
        File.Exists(clipPath).Should().BeFalse();
    }

    [Fact]
    public void DeleteModel_NoFiles_DoesNotThrow()
    {
        var sut = CreateSut();

        var act = () => sut.DeleteModel(CaptioningModelType.LLaVA_v1_6_34B);

        act.Should().NotThrow();
    }

    private static void WriteSparseFile(string path, long size)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        fs.SetLength(size);
    }
}
