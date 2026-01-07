using DiffusionNexus.Autocropper.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Autocropper;

/// <summary>
/// Unit tests for ResolutionOption.
/// Tests the resolution option record used for downscaling configuration.
/// </summary>
public class ResolutionOptionTests
{
    #region Static Property Tests

    [Fact]
    public void WhenNoneAccessedThenReturnsNoScalingOption()
    {
        // Act
        var none = ResolutionOption.None;

        // Assert
        none.MaxSize.Should().BeNull();
        none.DisplayName.Should().Contain("No scaling");
    }

    [Fact]
    public void WhenCustomAccessedThenReturnsCustomOption()
    {
        // Act
        var custom = ResolutionOption.Custom;

        // Assert
        custom.MaxSize.Should().BeNull();
        custom.DisplayName.Should().Be("Custom");
    }

    [Fact]
    public void WhenNoneAccessedMultipleTimesThenReturnsSameInstance()
    {
        // Act
        var none1 = ResolutionOption.None;
        var none2 = ResolutionOption.None;

        // Assert
        none1.Should().BeSameAs(none2);
    }

    [Fact]
    public void WhenCustomAccessedMultipleTimesThenReturnsSameInstance()
    {
        // Act
        var custom1 = ResolutionOption.Custom;
        var custom2 = ResolutionOption.Custom;

        // Assert
        custom1.Should().BeSameAs(custom2);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void WhenResolutionOptionCreatedWithSizeThenMaxSizeIsSet()
    {
        // Act
        var option = new ResolutionOption(1024, "1024px");

        // Assert
        option.MaxSize.Should().Be(1024);
    }

    [Fact]
    public void WhenResolutionOptionCreatedWithDisplayNameThenDisplayNameIsSet()
    {
        // Act
        var option = new ResolutionOption(512, "512 pixels");

        // Assert
        option.DisplayName.Should().Be("512 pixels");
    }

    [Fact]
    public void WhenResolutionOptionCreatedWithNullSizeThenMaxSizeIsNull()
    {
        // Act
        var option = new ResolutionOption(null, "No size");

        // Assert
        option.MaxSize.Should().BeNull();
    }

    #endregion

    #region Common Resolution Values Tests

    [Theory]
    [InlineData(256, "256px")]
    [InlineData(512, "512px")]
    [InlineData(768, "768px")]
    [InlineData(1024, "1024px")]
    [InlineData(1280, "1280px")]
    [InlineData(1536, "1536px")]
    [InlineData(2048, "2048px")]
    public void WhenCommonResolutionCreatedThenValuesAreCorrect(int size, string displayName)
    {
        // Act
        var option = new ResolutionOption(size, displayName);

        // Assert
        option.MaxSize.Should().Be(size);
        option.DisplayName.Should().Be(displayName);
    }

    #endregion

    #region Record Equality Tests

    [Fact]
    public void WhenTwoOptionsHaveSameValuesThenTheyAreEqual()
    {
        // Arrange
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024px");

        // Assert
        option1.Should().Be(option2);
    }

    [Fact]
    public void WhenTwoOptionsHaveDifferentSizesThenTheyAreNotEqual()
    {
        // Arrange
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(512, "1024px");

        // Assert
        option1.Should().NotBe(option2);
    }

    [Fact]
    public void WhenTwoOptionsHaveDifferentDisplayNamesThenTheyAreNotEqual()
    {
        // Arrange
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024 pixels");

        // Assert
        option1.Should().NotBe(option2);
    }

    [Fact]
    public void WhenOptionComparedToNullThenNotEqual()
    {
        // Arrange
        var option = new ResolutionOption(1024, "1024px");

        // Assert
        option.Should().NotBe(null);
    }

    #endregion

    #region Record With Tests

    [Fact]
    public void WhenWithMaxSizeCalledThenNewInstanceIsCreated()
    {
        // Arrange
        var original = new ResolutionOption(1024, "1024px");

        // Act
        var modified = original with { MaxSize = 2048 };

        // Assert
        modified.MaxSize.Should().Be(2048);
        modified.DisplayName.Should().Be("1024px");
        original.MaxSize.Should().Be(1024); // Original unchanged
    }

    [Fact]
    public void WhenWithDisplayNameCalledThenNewInstanceIsCreated()
    {
        // Arrange
        var original = new ResolutionOption(1024, "1024px");

        // Act
        var modified = original with { DisplayName = "One K" };

        // Assert
        modified.MaxSize.Should().Be(1024);
        modified.DisplayName.Should().Be("One K");
        original.DisplayName.Should().Be("1024px"); // Original unchanged
    }

    #endregion

    #region Hash Code Tests

    [Fact]
    public void WhenTwoEqualOptionsGetHashCodeThenHashCodesMatch()
    {
        // Arrange
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024px");

        // Assert
        option1.GetHashCode().Should().Be(option2.GetHashCode());
    }

    [Fact]
    public void WhenTwoDifferentOptionsGetHashCodeThenHashCodesAreLikelyDifferent()
    {
        // Arrange
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(512, "512px");

        // Assert - hash codes should be different (though not guaranteed)
        option1.GetHashCode().Should().NotBe(option2.GetHashCode());
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void WhenToStringCalledThenReturnsRecordRepresentation()
    {
        // Arrange
        var option = new ResolutionOption(1024, "1024px");

        // Act
        var str = option.ToString();

        // Assert
        str.Should().Contain("1024");
        str.Should().Contain("1024px");
    }

    #endregion

    #region Deconstruction Tests

    [Fact]
    public void WhenDeconstructedThenValuesAreCorrect()
    {
        // Arrange
        var option = new ResolutionOption(1024, "1024px");

        // Act
        var (maxSize, displayName) = option;

        // Assert
        maxSize.Should().Be(1024);
        displayName.Should().Be("1024px");
    }

    #endregion

    #region None and Custom Distinction Tests

    [Fact]
    public void WhenNoneAndCustomComparedThenTheyAreDifferent()
    {
        // Assert
        ResolutionOption.None.Should().NotBe(ResolutionOption.Custom);
    }

    [Fact]
    public void WhenNoneAndCustomBothHaveNullMaxSizeThenDistinguishedByDisplayName()
    {
        // Assert
        ResolutionOption.None.MaxSize.Should().BeNull();
        ResolutionOption.Custom.MaxSize.Should().BeNull();
        ResolutionOption.None.DisplayName.Should().NotBe(ResolutionOption.Custom.DisplayName);
    }

    #endregion
}
