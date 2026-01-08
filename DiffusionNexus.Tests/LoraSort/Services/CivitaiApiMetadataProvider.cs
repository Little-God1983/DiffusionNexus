using Moq;
using FluentAssertions;
using DiffusionNexus.Service.Enums;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Classes;
using System.Text.Json;

namespace DiffusionNexus.Tests.LoraSort.Services;

public class CivitaiApiMetadataProviderTests : IDisposable
{
    private readonly Mock<ICivitaiApiClient> _mockApiClient;
    private readonly CivitaiApiMetadataProvider _provider;
    private readonly List<string> _tempFiles;
    private const string TestApiKey = "test-api-key";
    private const string ValidSha256Hash = "a1b2c3d4e5f67890123456789012345678901234567890123456789012345678";

    public CivitaiApiMetadataProviderTests()
    {
        _mockApiClient = new Mock<ICivitaiApiClient>();
        _provider = new CivitaiApiMetadataProvider(_mockApiClient.Object, TestApiKey);
        _tempFiles = new List<string>();
    }

    private string CreateTempFileWithContent(string content = "test content")
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, content);
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
                File.Delete(file);
        }
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
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        var versionJson = "{\"modelId\": \"12345\", \"baseModel\": \"SD 1.5\", \"name\": \"Test Version\"}";
        var modelJson = "{\"type\": \"LORA\", \"tags\": [\"anime\", \"character\"]}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), TestApiKey))
                     .ReturnsAsync(versionJson);
        _mockApiClient.Setup(x => x.GetModelAsync("12345", TestApiKey))
                     .ReturnsAsync(modelJson);

        // Act
        var result = await _provider.GetModelMetadataAsync(testFilePath);

        // Assert
        result.Should().NotBeNull();
        result.SHA256Hash.Should().NotBeNullOrEmpty();
        result.ModelId.Should().Be("12345");
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        result.ModelVersionName.Should().Be("Test Version");
        result.ModelType.Should().Be(DiffusionTypes.LORA);
        result.Tags.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithoutModelId_ShouldNotCallModelApi()
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        var versionJson = "{\"baseModel\": \"SD 1.5\", \"name\": \"Test Version\"}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), TestApiKey))
                     .ReturnsAsync(versionJson);

        // Act
        var result = await _provider.GetModelMetadataAsync(testFilePath);

        // Assert
        result.Should().NotBeNull();
        result.ModelId.Should().BeNull();
        result.DiffusionBaseModel.Should().Be("SD 1.5");
        _mockApiClient.Verify(x => x.GetModelAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithEmptyJson_ShouldReturnDefaults()
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), TestApiKey))
                     .ReturnsAsync("{}");

        // Act
        var result = await _provider.GetModelMetadataAsync(testFilePath);

        // Assert
        result.Should().NotBeNull();
        result.SHA256Hash.Should().NotBeNullOrEmpty();
        result.ModelId.Should().BeNull();
        result.ModelType.Should().Be(DiffusionTypes.UNASSIGNED);
    }

    [Theory]
    [InlineData("LORA", DiffusionTypes.LORA)]
    [InlineData("CHECKPOINT", DiffusionTypes.CHECKPOINT)]
    [InlineData("UNKNOWN_TYPE", DiffusionTypes.UNASSIGNED)]
    public async Task GetModelMetadataAsync_WithDifferentTypes_ShouldParseCorrectly(string modelType, DiffusionTypes expected)
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        var versionJson = "{\"modelId\": \"12345\"}";
        var modelJson = $"{{\"type\": \"{modelType}\"}}";

        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), TestApiKey))
                     .ReturnsAsync(versionJson);
        _mockApiClient.Setup(x => x.GetModelAsync("12345", TestApiKey))
                     .ReturnsAsync(modelJson);

        // Act
        var result = await _provider.GetModelMetadataAsync(testFilePath);

        // Assert
        result.ModelType.Should().Be(expected);
    }

    [Fact]
    public async Task GetModelMetadataAsync_WhenApiThrows_ShouldPropagateException()
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(
            It.IsAny<string>(), 
            It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Network error", null, System.Net.HttpStatusCode.InternalServerError));

        // Act & Assert
        await Assert.ThrowsAsync<HttpRequestException>(() => 
            _provider.GetModelMetadataAsync(testFilePath));
    }

    [Fact]
    public async Task GetModelMetadataAsync_WhenApiReturns404_ShouldSetNoMetadata()
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(
            It.IsAny<string>(), 
            It.IsAny<string>()))
            .ThrowsAsync(new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound));

        // Act
        var result = await _provider.GetModelMetadataAsync(testFilePath);

        // Assert
        result.Should().NotBeNull();
        result.NoMetaData.Should().BeTrue();
    }

    [Fact]
    public async Task GetModelMetadataAsync_WithInvalidJson_ShouldThrowJsonException()
    {
        // Arrange
        var testFilePath = CreateTempFileWithContent();
        _mockApiClient.Setup(x => x.GetModelVersionByHashAsync(It.IsAny<string>(), TestApiKey))
                     .ReturnsAsync("{ invalid json");

        // Act & Assert
        await Assert.ThrowsAnyAsync<JsonException>(() =>
            _provider.GetModelMetadataAsync(testFilePath));
    }
}
