using DiffusionNexus.Domain.Entities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Entities;

/// <summary>
/// Unit tests for <see cref="EpochFileItem"/>.
/// Tests file detection, creation from file path, and formatting.
/// </summary>
public class EpochFileItemTests : IDisposable
{
    private readonly string _testTempPath;

    public EpochFileItemTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"EpochFileItemTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    #region SupportedExtensions Tests

    [Fact]
    public void SupportedExtensions_ContainsExpectedFormats()
    {
        EpochFileItem.SupportedExtensions.Should().Contain(".safetensors");
        EpochFileItem.SupportedExtensions.Should().Contain(".pt");
        EpochFileItem.SupportedExtensions.Should().Contain(".pth");
        EpochFileItem.SupportedExtensions.Should().Contain(".gguf");
    }

    [Fact]
    public void SupportedExtensions_HasFourItems()
    {
        EpochFileItem.SupportedExtensions.Should().HaveCount(4);
    }

    #endregion

    #region IsSupportedExtension Tests

    [Theory]
    [InlineData(".safetensors", true)]
    [InlineData(".pt", true)]
    [InlineData(".pth", true)]
    [InlineData(".gguf", true)]
    [InlineData(".SAFETENSORS", true)]
    [InlineData(".PT", true)]
    [InlineData("safetensors", true)]
    [InlineData("pt", true)]
    [InlineData(".txt", false)]
    [InlineData(".json", false)]
    [InlineData(".bin", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupportedExtension_ReturnsExpectedResult(string? extension, bool expected)
    {
        EpochFileItem.IsSupportedExtension(extension!).Should().Be(expected);
    }

    #endregion

    #region IsEpochFile Tests

    [Theory]
    [InlineData("model.safetensors", true)]
    [InlineData("model.pt", true)]
    [InlineData("model.pth", true)]
    [InlineData("model.gguf", true)]
    [InlineData("MODEL.SAFETENSORS", true)]
    [InlineData("/path/to/model.safetensors", true)]
    [InlineData("C:\\path\\model.pt", true)]
    [InlineData("model.txt", false)]
    [InlineData("model.json", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEpochFile_ReturnsExpectedResult(string? filePath, bool expected)
    {
        EpochFileItem.IsEpochFile(filePath!).Should().Be(expected);
    }

    #endregion

    #region FromFile Tests

    [Fact]
    public void FromFile_WithNullPath_ThrowsArgumentNullException()
    {
        var act = () => EpochFileItem.FromFile(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void FromFile_CreatesItemWithCorrectProperties()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "test_model.safetensors");
        File.WriteAllText(filePath, "test content for size calculation");

        // Act
        var item = EpochFileItem.FromFile(filePath);

        // Assert
        item.FileName.Should().Be("test_model.safetensors");
        item.DisplayName.Should().Be("test_model");
        item.FilePath.Should().Be(filePath);
        item.Extension.Should().Be(".safetensors");
        item.FileSizeBytes.Should().BeGreaterThan(0);
        item.FileSizeDisplay.Should().NotBeNullOrEmpty();
        item.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void FromFile_WhenFileDoesNotExist_SetsZeroSize()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "nonexistent.safetensors");

        // Act
        var item = EpochFileItem.FromFile(filePath);

        // Assert
        item.FileSizeBytes.Should().Be(0);
        item.FileSizeDisplay.Should().Be("0 B");
    }

    [Fact]
    public void FromFile_SetsCreatedAtAndModifiedAt()
    {
        // Arrange
        var filePath = Path.Combine(_testTempPath, "dated_model.pt");
        File.WriteAllText(filePath, "content");

        // Act
        var item = EpochFileItem.FromFile(filePath);

        // Assert
        item.CreatedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(10));
        item.ModifiedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromSeconds(10));
    }

    #endregion

    #region FormatFileSize Tests

    [Fact]
    public void FormatFileSize_FormatsBytes()
    {
        EpochFileItem.FormatFileSize(0).Should().Be("0 B");
        EpochFileItem.FormatFileSize(100).Should().Be("100 B");
        EpochFileItem.FormatFileSize(1023).Should().Be("1023 B");
    }

    [Fact]
    public void FormatFileSize_FormatsKilobytes()
    {
        var result = EpochFileItem.FormatFileSize(1024);
        result.Should().EndWith("KB");
        result.Should().StartWith("1");
    }

    [Fact]
    public void FormatFileSize_FormatsMegabytes()
    {
        var result = EpochFileItem.FormatFileSize(1048576);
        result.Should().EndWith("MB");
        result.Should().StartWith("1");
    }

    [Fact]
    public void FormatFileSize_FormatsGigabytes()
    {
        var result = EpochFileItem.FormatFileSize(1073741824);
        result.Should().EndWith("GB");
        result.Should().StartWith("1");
    }

    [Fact]
    public void FormatFileSize_HandlesLargeFiles()
    {
        // 1.5 GB
        var result = EpochFileItem.FormatFileSize(1610612736);
        result.Should().EndWith("GB");
        result.Should().StartWith("1");
    }

    #endregion

    #region Default Values Tests

    [Fact]
    public void DefaultConstructor_HasEmptyStrings()
    {
        var item = new EpochFileItem();

        item.FileName.Should().BeEmpty();
        item.DisplayName.Should().BeEmpty();
        item.FilePath.Should().BeEmpty();
        item.FileSizeDisplay.Should().BeEmpty();
        item.Extension.Should().BeEmpty();
        item.FileSizeBytes.Should().Be(0);
        item.IsSelected.Should().BeFalse();
    }

    #endregion
}
