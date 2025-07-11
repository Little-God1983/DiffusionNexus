using FluentAssertions;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class JsonInfoFileReaderServiceTests : IDisposable
{
    private readonly string _testDirectoryPath;

    public JsonInfoFileReaderServiceTests()
    {
        _testDirectoryPath = Path.Combine(Path.GetTempPath(), "LoraAutoSortTests");
        SetupTestDirectory();
    }

    private void SetupTestDirectory()
    {
        if (Directory.Exists(_testDirectoryPath))
        {
            Directory.Delete(_testDirectoryPath, true);
        }
        Directory.CreateDirectory(_testDirectoryPath);
    }

    [Fact]
    public void GroupFilesByPrefix_ShouldGroupRelatedFiles()
    {
        // Arrange
        var files = new[]
        {
                "test_model.safetensors",
                "test_model.civitai.info",
                "test_model.preview.png",
                "other_model.safetensors"
            };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().HaveCount(2);
        var testModel = result.FirstOrDefault(m => m.SafeTensorFileName == "test_model");
        testModel.Should().NotBeNull();
        testModel!.AssociatedFilesInfo.Should().HaveCount(3);
        testModel.AssociatedFilesInfo.Select(f => f.Name).Should().Contain(new[] {
                "test_model.safetensors",
                "test_model.civitai.info",
                "test_model.preview.png"
            });

        var otherModel = result.FirstOrDefault(m => m.SafeTensorFileName == "other_model");
        otherModel.Should().NotBeNull();
        otherModel!.AssociatedFilesInfo.Should().HaveCount(1);
        otherModel.AssociatedFilesInfo.First().Name.Should().Be("other_model.safetensors");
    }

    [Fact]
    public void GroupFilesByPrefix_ShouldSetNoMetaDataFlagCorrectly()
    {
        // Arrange
        var files = new[]
        {
                "model_with_meta.safetensors",
                "model_with_meta.civitai.info",
                "model_without_meta.safetensors"
            };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        var modelWithMeta = result.First(m => m.SafeTensorFileName == "model_with_meta");
        var modelWithoutMeta = result.First(m => m.SafeTensorFileName == "model_without_meta");

        modelWithMeta.NoMetaData.Should().BeFalse();
        modelWithoutMeta.NoMetaData.Should().BeFalse();
    }

    [Fact]
    public async Task GetModelData_WithValidData_ShouldProcessCorrectly()
    {
        // Arrange
        var modelFiles = new[]
        {
                ("test_model.safetensors", ""),
                ("test_model.civitai.info", @"{
                    ""baseModel"": ""SD 1.5"",
                    ""type"": ""LORA"",
                    ""tags"": [""character"", ""style""]
                }")
            };

        foreach (var (fileName, content) in modelFiles)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, fileName), content);
        }

        // Fix for CS1503: Argument 2: cannot convert from 'method group' to 'System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task<DiffusionNexus.Service.Classes.ModelClass>>'

        // The issue arises because the method group `new LocalFileMetadataProvider().GetModelMetadataAsync`
        // does not match the expected delegate type `Func<string, CancellationToken, Task<ModelClass>>`.
        // To fix this, we need to explicitly create a delegate that matches the expected signature.

        var service = new JsonInfoFileReaderService(
            _testDirectoryPath,
            (filePath, progress, cancellationToken) => new LocalFileMetadataProvider().GetModelMetadataAsync(filePath, cancellationToken)
        );
        var progress = new Progress<ProgressReport>();
        var cts = new CancellationTokenSource();

        // Act
        var result = await service.GetModelData(progress, cts.Token);

        // Assert
        result.Should().NotBeEmpty();
        var model = result.First();
        model.NoMetaData.Should().BeFalse();
        model.DiffusionBaseModel.Should().Be("SD 1.5");
        model.ModelType.Should().Be(DiffusionTypes.LORA);
    }
    [Fact]
    public void GroupFilesByPrefix_WithEmptyDirectory_ShouldReturnEmptyList()
    {
        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().BeEmpty();
    }

    [Fact]
    public void GroupFilesByPrefix_WithSubdirectories_ShouldIncludeAllFiles()
    {
        // Arrange
        var subdirPath = Path.Combine(_testDirectoryPath, "subdir");
        Directory.CreateDirectory(subdirPath);

        var files = new[]
        {
        (_testDirectoryPath, "model1.safetensors"),
        (subdirPath, "model2.safetensors"),
        (subdirPath, "model2.civitai.info")
    };

        foreach (var (dir, file) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().HaveCount(2);
        result.Select(m => m.SafeTensorFileName).Should().Contain(new[] { "model1", "model2" });
    }

    [Fact]
    public async Task GetModelData_WithMalformedJsonFile_ShouldSkipAndContinue()
    {
        // Arrange
        var modelFiles = new[]
        {
        ("valid_model.safetensors", ""),
        ("valid_model.civitai.info", @"{
            ""baseModel"": ""SD 1.5"",
            ""type"": ""LORA"",
            ""tags"": [""character"", ""style""]
        }"),
        ("invalid_model.safetensors", ""),
        ("invalid_model.civitai.info", @"{ This is not valid JSON }")
    };

        foreach (var (fileName, content) in modelFiles)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, fileName), content);
        }

        var service = new JsonInfoFileReaderService(_testDirectoryPath, (filePath, progress, cancellationToken) => new LocalFileMetadataProvider().GetModelMetadataAsync(filePath, cancellationToken));
        var cts = new CancellationTokenSource();

        // Act
        var result = await service.GetModelData(null, cts.Token);

        // Assert
        result.Should().HaveCount(2); // Both models should be returned
        var validModel = result.FirstOrDefault(m => m.SafeTensorFileName == "valid_model");
        var invalidModel = result.FirstOrDefault(m => m.SafeTensorFileName == "invalid_model");

        validModel.Should().NotBeNull();
        validModel!.DiffusionBaseModel.Should().Be("SD 1.5");
        validModel.ModelType.Should().Be(DiffusionTypes.LORA);

        invalidModel.Should().NotBeNull();
        invalidModel!.NoMetaData.Should().BeFalse();
    }

    [Fact]
    public async Task GetModelData_WhenCancelled_ShouldThrowOperationCanceledException()
    {
        // Arrange
        var files = new[]
        {
        "model1.safetensors",
        "model1.civitai.info",
    };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        var service = new JsonInfoFileReaderService(_testDirectoryPath, (filePath, progress, cancellationToken) => new LocalFileMetadataProvider().GetModelMetadataAsync(filePath, cancellationToken));
        var cts = new CancellationTokenSource();
        cts.Cancel(); // Cancel before execution

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await service.GetModelData(null, cts.Token));
    }


    void IDisposable.Dispose()
    {
        if (Directory.Exists(_testDirectoryPath))
        {
            Directory.Delete(_testDirectoryPath, true);
        }
    }

    [Fact]
    public void GroupFilesByPrefix_WithSpecialCharactersInFilenames_ShouldGroupCorrectly()
    {
        // Arrange
        var files = new[]
        {
        "special-chars_model!@#.safetensors",
        "special-chars_model!@#.civitai.info",
        "another_model.safetensors"
    };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().HaveCount(2);
        var specialModel = result.FirstOrDefault(m => m.SafeTensorFileName == "special-chars_model!@#");
        specialModel.Should().NotBeNull();
        specialModel!.AssociatedFilesInfo.Should().HaveCount(2);
    }

    [Fact]
    public void GroupFilesByPrefix_WithDuplicateModelNames_ShouldHandleCorrectly()
    {
        // Arrange
        var files = new[]
        {
        "model.safetensors",
        "model.civitai.info",
        "model.yaml", // Another extension for the same model
    };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().HaveCount(1);
        var model = result.First();
        model.SafeTensorFileName.Should().Be("model");
        model.AssociatedFilesInfo.Should().HaveCount(3);
        model.AssociatedFilesInfo.Select(f => f.Extension)
            .Should().Contain(new[] { ".safetensors", ".info", ".yaml" });
    }

    [Fact]
    public void GroupFilesByPrefix_WithMissingSafeTensorFile_ShouldSkipModel()
    {
        // Arrange
        var files = new[]
        {
        "valid_model.safetensors",
        "valid_model.civitai.info",
        "invalid_model.civitai.info", // Missing safetensors file
    };

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, file), "");
        }

        // Act
        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        // Assert
        result.Should().HaveCount(1);
        result[0].SafeTensorFileName.Should().Be("valid_model");
    }

    [Fact]
    public void GroupFilesByPrefix_ShouldPreserveCaseOfFileName()
    {
        var fileName = "TestModel.safetensors";
        File.WriteAllText(Path.Combine(_testDirectoryPath, fileName), string.Empty);

        var result = JsonInfoFileReaderService.GroupFilesByPrefix(_testDirectoryPath);

        result.Should().HaveCount(1);
        result[0].SafeTensorFileName.Should().Be("TestModel");
    }

    [Fact]
    public async Task GetModelData_ShouldReportProgress()
    {
        // Arrange
        var modelFiles = new[]
        {
        ("model1.safetensors", ""),
        ("model1.civitai.info", @"{""baseModel"": ""SD 1.5"", ""type"": ""LORA""}"),
        ("model2.safetensors", ""),
        ("model2.civitai.info", @"{""baseModel"": ""SDXL"", ""type"": ""LoCon""}")
    };

        foreach (var (fileName, content) in modelFiles)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, fileName), content);
        }

        var service = new JsonInfoFileReaderService(_testDirectoryPath, (filePath, progress, cancellationToken) => new LocalFileMetadataProvider().GetModelMetadataAsync(filePath, cancellationToken));

        int progressCount = 0;
        var progress = new Progress<ProgressReport>(report => progressCount++);
        var cts = new CancellationTokenSource();

        // Act
        var result = await service.GetModelData(progress, cts.Token);

        // Assert
        result.Should().HaveCount(2);
        progressCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetModelData_WithMultipleModelsAndTags_ShouldProcessAllCorrectly()
    {
        // Arrange
        var modelFiles = new[]
        {
        ("model1.safetensors", ""),
        ("model1.civitai.info", @"{
            ""baseModel"": ""SD 1.5"",
            ""type"": ""LORA"",
            ""tags"": [""character"", ""style""]
        }"),
        ("model2.safetensors", ""),
        ("model2.civitai.info", @"{
            ""baseModel"": ""SDXL"",
            ""type"": ""LoCon"",
            ""tags"": [""landscape"", ""photorealistic""]
        }")
    };

        foreach (var (fileName, content) in modelFiles)
        {
            File.WriteAllText(Path.Combine(_testDirectoryPath, fileName), content);
        }

        var service = new JsonInfoFileReaderService(_testDirectoryPath, (filePath, progress, cancellationToken) => new LocalFileMetadataProvider().GetModelMetadataAsync(filePath, cancellationToken));
        var cts = new CancellationTokenSource();

        // Act
        var result = await service.GetModelData(null, cts.Token);

        // Assert
        result.Should().HaveCount(2);

        var model1 = result.First(m => m.SafeTensorFileName == "model1");
        model1.DiffusionBaseModel.Should().Be("SD 1.5");
        model1.ModelType.Should().Be(DiffusionTypes.LORA);
        model1.Tags.Should().Contain(new[] { "character", "style" });

        var model2 = result.First(m => m.SafeTensorFileName == "model2");
        model2.DiffusionBaseModel.Should().Be("SDXL");
        model2.ModelType.Should().Be(DiffusionTypes.LOCON);
        model2.Tags.Should().Contain(new[] { "landscape", "photorealistic" });
    }
}
