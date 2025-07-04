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
            modelWithoutMeta.NoMetaData.Should().BeTrue();
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

            var metadataService = new ModelMetadataService(new CompositeMetadataProvider(new LocalFileMetadataProvider()));
            var service = new JsonInfoFileReaderService(_testDirectoryPath, metadataService);
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

        void IDisposable.Dispose()
        {
            if (Directory.Exists(_testDirectoryPath))
            {
                Directory.Delete(_testDirectoryPath, true);
            }
        }
    }
