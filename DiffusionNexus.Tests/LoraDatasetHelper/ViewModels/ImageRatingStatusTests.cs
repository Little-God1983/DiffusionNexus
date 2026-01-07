using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="ImageRatingStatus"/> enum.
/// Tests enum values and their relationships.
/// </summary>
public class ImageRatingStatusTests
{
    #region Enum Value Tests

    [Fact]
    public void Unrated_HasValueZero()
    {
        ((int)ImageRatingStatus.Unrated).Should().Be(0);
    }

    [Fact]
    public void Approved_HasValueOne()
    {
        ((int)ImageRatingStatus.Approved).Should().Be(1);
    }

    [Fact]
    public void Rejected_HasValueNegativeOne()
    {
        ((int)ImageRatingStatus.Rejected).Should().Be(-1);
    }

    #endregion

    #region Default Value Tests

    [Fact]
    public void DefaultValue_IsUnrated()
    {
        // Arrange
        ImageRatingStatus defaultStatus = default;

        // Assert
        defaultStatus.Should().Be(ImageRatingStatus.Unrated);
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void Unrated_ToStringReturnsUnrated()
    {
        ImageRatingStatus.Unrated.ToString().Should().Be("Unrated");
    }

    [Fact]
    public void Approved_ToStringReturnsApproved()
    {
        ImageRatingStatus.Approved.ToString().Should().Be("Approved");
    }

    [Fact]
    public void Rejected_ToStringReturnsRejected()
    {
        ImageRatingStatus.Rejected.ToString().Should().Be("Rejected");
    }

    #endregion

    #region Parse Tests

    [Theory]
    [InlineData("Unrated", ImageRatingStatus.Unrated)]
    [InlineData("Approved", ImageRatingStatus.Approved)]
    [InlineData("Rejected", ImageRatingStatus.Rejected)]
    public void TryParse_WhenValidString_ParsesCorrectly(string input, ImageRatingStatus expected)
    {
        // Act
        var success = Enum.TryParse<ImageRatingStatus>(input, out var result);

        // Assert
        success.Should().BeTrue();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("unrated", ImageRatingStatus.Unrated)]
    [InlineData("APPROVED", ImageRatingStatus.Approved)]
    [InlineData("rejected", ImageRatingStatus.Rejected)]
    public void TryParse_WhenCaseInsensitive_ParsesCorrectly(string input, ImageRatingStatus expected)
    {
        // Act
        var success = Enum.TryParse<ImageRatingStatus>(input, ignoreCase: true, out var result);

        // Assert
        success.Should().BeTrue();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("Invalid")]
    [InlineData("")]
    [InlineData("Ready")]
    public void TryParse_WhenInvalidString_ReturnsFalse(string input)
    {
        // Act
        var success = Enum.TryParse<ImageRatingStatus>(input, out _);

        // Assert
        success.Should().BeFalse();
    }

    [Theory]
    [InlineData("0", ImageRatingStatus.Unrated)]
    [InlineData("1", ImageRatingStatus.Approved)]
    [InlineData("-1", ImageRatingStatus.Rejected)]
    public void TryParse_WhenNumericString_ParsesCorrectly(string input, ImageRatingStatus expected)
    {
        // Act
        var success = Enum.TryParse<ImageRatingStatus>(input, out var result);

        // Assert
        success.Should().BeTrue();
        result.Should().Be(expected);
    }

    #endregion

    #region All Values Tests

    [Fact]
    public void GetValues_ReturnsAllThreeStatuses()
    {
        // Arrange
        var allValues = Enum.GetValues<ImageRatingStatus>();

        // Assert
        allValues.Should().HaveCount(3);
        allValues.Should().Contain(ImageRatingStatus.Unrated);
        allValues.Should().Contain(ImageRatingStatus.Approved);
        allValues.Should().Contain(ImageRatingStatus.Rejected);
    }

    #endregion

    #region Comparison Tests

    [Fact]
    public void Comparison_ApprovedGreaterThanUnrated()
    {
        (ImageRatingStatus.Approved > ImageRatingStatus.Unrated).Should().BeTrue();
    }

    [Fact]
    public void Comparison_UnratedGreaterThanRejected()
    {
        (ImageRatingStatus.Unrated > ImageRatingStatus.Rejected).Should().BeTrue();
    }

    [Fact]
    public void Comparison_ApprovedGreaterThanRejected()
    {
        (ImageRatingStatus.Approved > ImageRatingStatus.Rejected).Should().BeTrue();
    }

    #endregion

    #region Equality Tests

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var status1 = ImageRatingStatus.Approved;
        var status2 = ImageRatingStatus.Approved;

        (status1 == status2).Should().BeTrue();
        status1.Equals(status2).Should().BeTrue();
    }

    [Fact]
    public void Equality_DifferentValues_AreNotEqual()
    {
        var status1 = ImageRatingStatus.Approved;
        var status2 = ImageRatingStatus.Rejected;

        (status1 != status2).Should().BeTrue();
        status1.Equals(status2).Should().BeFalse();
    }

    #endregion
}
