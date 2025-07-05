using Moq;
using FluentAssertions;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;
using System.Text.Json;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiApiMetadataProviderTests
{
    private readonly Mock<ICivitaiApiClient> _mockApiClient;
    private readonly CivitaiApiMetadataProvider _provider;
    private const string TestApiKey = "test-api-key";
    private const string ValidSha256Hash = "a1b2c3d4e5f67890123456789012345678901234567890123456789012345678";

    public CivitaiApiMetadataProviderTests()
    {
        _mockApiClient = new Mock<ICivitaiApiClient>();
        _provider = new CivitaiApiMetadataProvider(_mockApiClient.Object, TestApiKey);
    }

    [Fact]
    public void Constructor_WithValidParameters_ShouldCreateInstance()
    {
        var provider = new CivitaiApiMetadataProvider(_mockApiClient.Object, TestApiKey);
        provider.Should().NotBeNull();
    }

    [Theory]
    [InlineData("a1b2c3d4e5f67890123456789012345678901234567890123456789012345678", true)]
    [InlineData("invalid-hash", false)]
    [InlineData("12345", false)]
    [InlineData("", false)]
    public async Task CanHandleAsync_WithVariousIdentifiers_ShouldReturnCorrectResult(string identifier, bool expected)
    {
        var result = await _provider.CanHandleAsync(identifier);
        result.Should().Be(expected);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithValidHash_ShouldReturnModelClass()
    {
        var versionJson = "{\"modelId\": \"12345\", \"baseModel\": \"SD 1.5\", \"name\": \"Test Version\"}";
        var modelJson = "{\"type\": \"LORA\", \"tags\": [\"anime\", \"character\"]}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(ValidSha256Hash, TestApiKey))
                     .ReturnsAsync(versionJson);
        _mockApiClient.Setup(x => x.GetModelAsync("12345", TestApiKey))
                     .ReturnsAsync(modelJson);

        var result = await _provider.GetModelMetadataAsync(ValidSha256Hash);

        result.Should().NotBeNull();
        result.SHA256Hash.Should().Be(ValidSha256Hash);
        result.ModelId.Should().Be("12345");
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.ModelVersionName.Should().Be("Test Version");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
        result.Tags.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithoutModelId_ShouldNotCallModelApi()
    {
        var versionJson = "{\"baseModel\": \"SD 1.5\", \"name\": \"Test Version\"}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(ValidSha256Hash, TestApiKey))
                     .ReturnsAsync(versionJson);

        var result = await _provider.GetModelMetadataAsync(ValidSha256Hash);

        result.Should().NotBeNull();
        result.ModelId.Should().BeNull();
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        _mockApiClient.Verify(x => x.GetModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithEmptyJson_ShouldReturnDefaults()
    {
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(ValidSha256Hash, TestApiKey))
                     .ReturnsAsync("{}");

        var result = await _provider.GetModelMetadataAsync(ValidSha256Hash);

        result.Should().NotBeNull();
        result.SHA256Hash.Should().Be(ValidSha256Hash);
        result.ModelId.Should().BeNull();
        result.ModelType.Should().Be(DiffusionTypes.OTHER);
    }

    [Theory]
    [InlineData("LORA", DiffusionTypes.LORA)]
    [InlineData("CHECKPOINT", DiffusionTypes.CHECKPOINT)]
    [InlineData("UNKNOWN_TYPE", DiffusionTypes.OTHER)]
    public async Task GetModelMetadataAsync_WithDifferentTypes_ShouldParseCorrectly(string modelType, DiffusionTypes expected)
    {
        var versionJson = "{\"modelId\": \"12345\"}";
        var modelJson = $"{{\"type\": \"{modelType}\"}}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(ValidSha256Hash, TestApiKey))
                     .ReturnsAsync(versionJson);
        _mockApiClient.Setup(x => x.GetModelAsync("12345", TestApiKey))
                     .ReturnsAsync(modelJson);

        var result = await _provider.GetModelMetadataAsync(ValidSha256Hash);

        result.ModelType.Should().Be(expected);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WhenApiThrows_ShouldPropagateException()
    {
        // Arrange
        var testFilePath = Path.GetTempFileName();
        try
        {
            // Write some content to the test file
            await File.WriteAllTextAsync(testFilePath, "test content");
            
            // Setup mock to throw for any hash
            _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(
                It.IsAny<string>(), 
                It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("Network error"));

            // Act & Assert
            await Assert.ThrowsAsync<HttpRequestException>(() => 
                _provider.GetModelMetadataAsync(testFilePath));
        }
        finally
        {
            // Cleanup
            if (File.Exists(testFilePath))
                File.Delete(testFilePath);
        }
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithInvalidJson_ShouldThrowJsonException()
    {
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(ValidSha256Hash, TestApiKey))
                     .ReturnsAsync("{ invalid json");

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            _provider.GetModelMetadataAsync(ValidSha256Hash));
    }
}
