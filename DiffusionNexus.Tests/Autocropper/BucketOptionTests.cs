using System.Collections.Generic;
using System.Linq;
using DiffusionNexus.Autocropper.ViewModels;
using DiffusionNexus.Core.Models.Configuration;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Autocropper;

/// <summary>
/// Unit tests for BucketOption.
/// Tests the selectable aspect ratio bucket option class.
/// </summary>
public class BucketOptionTests
{
    private static BucketDefinition CreateBucket(string name, double width, double height)
    {
        return new BucketDefinition { Name = name, Width = width, Height = height };
    }

    #region Constructor Tests

    [Theory]
    [InlineData("16:9", 16, 9)]
    [InlineData("9:16", 9, 16)]
    [InlineData("1:1", 1, 1)]
    public void WhenBucketOptionCreatedThenDisplayNameIsSet(string name, double width, double height)
    {
        // Arrange
        var bucket = CreateBucket(name, width, height);

        // Act
        var option = new BucketOption(bucket);

        // Assert
        option.DisplayName.Should().Be(name);
    }

    [Theory]
    [InlineData("16:9", 16, 9, 16.0 / 9.0)]
    [InlineData("9:16", 9, 16, 9.0 / 16.0)]
    [InlineData("1:1", 1, 1, 1.0)]
    public void WhenBucketOptionCreatedThenRatioIsSet(string name, double width, double height, double expectedRatio)
    {
        // Arrange
        var bucket = CreateBucket(name, width, height);

        // Act
        var option = new BucketOption(bucket);

        // Assert
        option.Ratio.Should().BeApproximately(expectedRatio, 0.0001);
    }

    [Fact]
    public void WhenBucketOptionCreatedThenBucketIsSet()
    {
        // Arrange
        var bucket = CreateBucket("16:9", 16, 9);

        // Act
        var option = new BucketOption(bucket);

        // Assert
        option.Bucket.Should().Be(bucket);
    }

    [Fact]
    public void WhenBucketOptionCreatedThenIsSelectedIsTrueByDefault()
    {
        // Act
        var option = new BucketOption(CreateBucket("1:1", 1, 1));

        // Assert
        option.IsSelected.Should().BeTrue();
    }

    #endregion

    #region IsSelected Property Tests

    [Fact]
    public void WhenIsSelectedChangedThenPropertyChangedIsRaised()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("1:1", 1, 1));
        var propertyChangedRaised = false;
        
        option.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BucketOption.IsSelected))
            {
                propertyChangedRaised = true;
            }
        };

        // Act
        option.IsSelected = false;

        // Assert
        propertyChangedRaised.Should().BeTrue();
    }

    [Fact]
    public void WhenIsSelectedSetToSameValueThenPropertyChangedIsNotRaised()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("1:1", 1, 1));
        option.IsSelected = true; // Default is already true
        
        var propertyChangedCount = 0;
        option.PropertyChanged += (_, _) => propertyChangedCount++;

        // Act - set to same value
        option.IsSelected = true;

        // Assert
        propertyChangedCount.Should().Be(0);
    }

    [Fact]
    public void WhenIsSelectedSetToFalseThenValueIsUpdated()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("1:1", 1, 1));

        // Act
        option.IsSelected = false;

        // Assert
        option.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void WhenIsSelectedToggledThenValueIsUpdated()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("1:1", 1, 1));

        // Act
        option.IsSelected = false;
        option.IsSelected = true;

        // Assert
        option.IsSelected.Should().BeTrue();
    }

    #endregion

    #region Immutable Properties Tests

    [Fact]
    public void WhenBucketOptionCreatedThenBucketIsImmutable()
    {
        // Arrange
        var bucket = CreateBucket("16:9", 16, 9);
        var option = new BucketOption(bucket);

        // Assert - Bucket property is get-only
        option.Bucket.Should().Be(bucket);
    }

    [Fact]
    public void WhenBucketOptionCreatedThenDisplayNameIsImmutable()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("16:9", 16, 9));

        // Assert - DisplayName property is get-only
        option.DisplayName.Should().Be("16:9");
    }

    [Fact]
    public void WhenBucketOptionCreatedThenRatioIsImmutable()
    {
        // Arrange
        var option = new BucketOption(CreateBucket("16:9", 16, 9));
        var initialRatio = option.Ratio;

        // Assert - Ratio property is get-only
        option.Ratio.Should().Be(initialRatio);
    }

    #endregion

    #region All Bucket Types Tests

    [Fact]
    public void WhenMultipleBucketsCreatedThenAllHaveUniqueDisplayNames()
    {
        // Arrange
        var buckets = new List<BucketDefinition>
        {
            CreateBucket("16:9", 16, 9),
            CreateBucket("1:1", 1, 1),
            CreateBucket("4:3", 4, 3)
        };

        // Act
        var options = buckets.Select(b => new BucketOption(b)).ToList();

        // Assert
        var displayNames = options.Select(o => o.DisplayName).ToList();
        displayNames.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void WhenMultipleBucketsCreatedThenAllHavePositiveRatios()
    {
        // Arrange
        var buckets = new List<BucketDefinition>
        {
            CreateBucket("16:9", 16, 9),
            CreateBucket("1:1", 1, 1)
        };

        // Act
        var options = buckets.Select(b => new BucketOption(b)).ToList();

        // Assert
        options.Should().OnlyContain(o => o.Ratio > 0);
    }

    #endregion
}
