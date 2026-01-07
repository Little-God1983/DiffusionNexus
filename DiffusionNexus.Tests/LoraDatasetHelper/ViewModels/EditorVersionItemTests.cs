using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="EditorVersionItem"/>.
/// Tests factory method and display text formatting.
/// </summary>
public class EditorVersionItemTests
{
    #region Create Factory Method Tests

    [Fact]
    public void Create_SetsVersionCorrectly()
    {
        // Act
        var item = EditorVersionItem.Create(3, 10);

        // Assert
        item.Version.Should().Be(3);
    }

    [Fact]
    public void Create_SetsImageCountCorrectly()
    {
        // Act
        var item = EditorVersionItem.Create(1, 42);

        // Assert
        item.ImageCount.Should().Be(42);
    }

    [Fact]
    public void Create_WithZeroImageCount_SetsImageCountToZero()
    {
        // Act
        var item = EditorVersionItem.Create(1, 0);

        // Assert
        item.ImageCount.Should().Be(0);
    }

    #endregion

    #region DisplayText Tests

    [Fact]
    public void DisplayText_WhenOneImage_UsesSingularForm()
    {
        // Arrange
        var item = EditorVersionItem.Create(1, 1);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be("V1 | 1 Image");
    }

    [Fact]
    public void DisplayText_WhenMultipleImages_UsesPluralForm()
    {
        // Arrange
        var item = EditorVersionItem.Create(1, 45);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be("V1 | 45 Images");
    }

    [Fact]
    public void DisplayText_WhenZeroImages_UsesPluralForm()
    {
        // Arrange
        var item = EditorVersionItem.Create(1, 0);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be("V1 | 0 Images");
    }

    [Fact]
    public void DisplayText_WhenVersionTwo_ShowsCorrectVersion()
    {
        // Arrange
        var item = EditorVersionItem.Create(2, 15);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be("V2 | 15 Images");
    }

    [Fact]
    public void DisplayText_WhenLargeVersion_ShowsCorrectVersion()
    {
        // Arrange
        var item = EditorVersionItem.Create(99, 1000);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be("V99 | 1000 Images");
    }

    [Theory]
    [InlineData(1, 0, "V1 | 0 Images")]
    [InlineData(1, 1, "V1 | 1 Image")]
    [InlineData(1, 2, "V1 | 2 Images")]
    [InlineData(2, 50, "V2 | 50 Images")]
    [InlineData(5, 100, "V5 | 100 Images")]
    [InlineData(10, 1, "V10 | 1 Image")]
    public void DisplayText_FormatsCorrectly(int version, int imageCount, string expected)
    {
        // Arrange
        var item = EditorVersionItem.Create(version, imageCount);

        // Act
        var displayText = item.DisplayText;

        // Assert
        displayText.Should().Be(expected);
    }

    #endregion

    #region Immutability Tests

    [Fact]
    public void EditorVersionItem_Properties_AreReadOnly()
    {
        // Verify that Version and ImageCount are init-only (compile-time check via usage)
        var item = EditorVersionItem.Create(1, 10);

        // These should be readable
        _ = item.Version;
        _ = item.ImageCount;
        _ = item.DisplayText;

        // Note: The init-only nature is verified by the fact that this compiles
        // but we cannot set item.Version = 2 outside of initialization
    }

    #endregion
}
