using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;
using Moq;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class MetadataProvidersTests
{
    [Fact]
    public async Task LocalFileProvider_ReadsCivitaiInfo()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var safetensors = Path.Combine(tempDir, "model.safetensors");
            var info = Path.Combine(tempDir, "model.civitai.info");
            File.WriteAllText(safetensors, "dummy");
            await File.WriteAllTextAsync(info, "{\"baseModel\":\"SD 1.5\",\"model\":{\"name\":\"Test\",\"type\":\"LORA\",\"tags\":[\"tag1\"]}}" );
            var provider = new LocalFileMetadataProvider();
            var meta = await provider.GetModelMetadataAsync(safetensors);
            meta.BaseModel.Should().Be("SD 1.5");
            meta.ModelVersionName.Should().Be("Test");
            meta.ModelType.Should().Be(DiffusionTypes.LORA);
            meta.Tags.Should().Contain("tag1");
            meta.SHA256Hash.Should().NotBeNull();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task CivitaiApiProvider_UsesApiClient()
    {
        var api = new Mock<ICivitaiApiClient>();
        api.Setup(a => a.GetModelVersionByHashAsync("hash", It.IsAny<string>())).ReturnsAsync("{\"modelId\":\"1\",\"baseModel\":\"SDXL\",\"name\":\"v1\"}");
        api.Setup(a => a.GetModelAsync("1", It.IsAny<string>())).ReturnsAsync("{\"type\":\"LORA\",\"tags\":[\"t1\"]}");
        var provider = new CivitaiApiMetadataProvider(api.Object, "");
        var meta = await provider.GetModelMetadataAsync("hash");
        meta.ModelId.Should().Be("1");
        meta.BaseModel.Should().Be("SDXL");
        meta.ModelVersionName.Should().Be("v1");
        meta.ModelType.Should().Be(DiffusionTypes.LORA);
        meta.Tags.Should().Contain("t1");
    }
}
