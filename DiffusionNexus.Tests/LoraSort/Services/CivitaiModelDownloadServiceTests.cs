using System;
using System.IO;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using FluentAssertions;
using Moq;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiModelDownloadServiceTests
{
    [Fact]
    public async Task DownloadModelAsync_ShouldPassApiKeyToFileDownloader()
    {
        const string apiKey = "api-key";
        var mockApiClient = new Mock<ICivitaiApiClient>();
        var modelService = new CivitaiModelService(mockApiClient.Object);
        var downloader = new TestFileDownloader();
        var service = new CivitaiModelDownloadService(modelService, downloader);

        var versionJson = """
{
  "id": 2385870,
  "modelId": 2108995,
  "name": "Test Version",
  "baseModel": "SDXL",
  "model": { "type": "LORA" },
  "trainedWords": ["pony", "qwen"],
  "files": [
    {
      "downloadUrl": "https://example.com/model.safetensors",
      "name": "model.safetensors",
      "type": "Model",
      "format": "SafeTensor",
      "sizeKB": 1,
      "hashes": { "SHA256": "abc" },
      "primary": true
    }
  ]
}
""";

        mockApiClient.Setup(c => c.GetModelVersionAsync("2385870", apiKey))
                     .ReturnsAsync(versionJson);

        var targetFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(targetFolder);

        try
        {
            var result = await service.DownloadModelAsync(
                "https://civitai.com/models/2108995/qwen-x-pony?modelVersionId=2385870",
                targetFolder,
                apiKey,
                progress: null,
                CancellationToken.None);

            result.ResultType.Should().Be(ModelDownloadResultType.Success);
            downloader.ReceivedApiKey.Should().Be(apiKey);
            downloader.WrittenFile.Should().NotBeNull();
            File.Exists(downloader.WrittenFile!).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(targetFolder))
            {
                Directory.Delete(targetFolder, recursive: true);
            }
        }
    }

    private sealed class TestFileDownloader : CivitaiFileDownloader
    {
        public string? ReceivedApiKey { get; private set; }
        public string? WrittenFile { get; private set; }

        public override Task DownloadAsync(Uri downloadUri, string destinationFilePath, string? apiKey, IProgress<ModelDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            ReceivedApiKey = apiKey;
            WrittenFile = destinationFilePath;
            File.WriteAllText(destinationFilePath, "dummy");
            progress?.Report(new ModelDownloadProgress(5, 5, null));
            return Task.CompletedTask;
        }
    }
}
