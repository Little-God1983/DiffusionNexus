using DiffusionNexus.UI.ViewModels.Tabs;
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
        var none = ResolutionOption.None;
        none.MaxSize.Should().BeNull();
        none.DisplayName.Should().Contain("No scaling");
    }

    [Fact]
    public void WhenCustomAccessedThenReturnsCustomOption()
    {
        var custom = ResolutionOption.Custom;
        custom.MaxSize.Should().BeNull();
        custom.DisplayName.Should().Be("Custom");
    }

    [Fact]
    public void WhenNoneAccessedMultipleTimesThenReturnsSameInstance()
    {
        var none1 = ResolutionOption.None;
        var none2 = ResolutionOption.None;
        none1.Should().BeSameAs(none2);
    }

    [Fact]
    public void WhenCustomAccessedMultipleTimesThenReturnsSameInstance()
    {
        var custom1 = ResolutionOption.Custom;
        var custom2 = ResolutionOption.Custom;
        custom1.Should().BeSameAs(custom2);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void WhenResolutionOptionCreatedWithSizeThenMaxSizeIsSet()
    {
        var option = new ResolutionOption(1024, "1024px");
        option.MaxSize.Should().Be(1024);
    }

    [Fact]
    public void WhenResolutionOptionCreatedWithDisplayNameThenDisplayNameIsSet()
    {
        var option = new ResolutionOption(512, "512 pixels");
        option.DisplayName.Should().Be("512 pixels");
    }

    [Fact]
    public void WhenResolutionOptionCreatedWithNullSizeThenMaxSizeIsNull()
    {
        var option = new ResolutionOption(null, "No size");
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
        var option = new ResolutionOption(size, displayName);
        option.MaxSize.Should().Be(size);
        option.DisplayName.Should().Be(displayName);
    }

    #endregion

    #region Record Equality Tests

    [Fact]
    public void WhenTwoOptionsHaveSameValuesThenTheyAreEqual()
    {
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024px");
        option1.Should().Be(option2);
    }

    [Fact]
    public void WhenTwoOptionsHaveDifferentSizesThenTheyAreNotEqual()
    {
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(512, "1024px");
        option1.Should().NotBe(option2);
    }

    [Fact]
    public void WhenTwoOptionsHaveDifferentDisplayNamesThenTheyAreNotEqual()
    {
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024 pixels");
        option1.Should().NotBe(option2);
    }

    [Fact]
    public void WhenOptionComparedToNullThenNotEqual()
    {
        var option = new ResolutionOption(1024, "1024px");
        option.Should().NotBe(null);
    }

    #endregion

    #region Record With Tests

    [Fact]
    public void WhenWithMaxSizeCalledThenNewInstanceIsCreated()
    {
        var original = new ResolutionOption(1024, "1024px");
        var modified = original with { MaxSize = 2048 };

        modified.MaxSize.Should().Be(2048);
        modified.DisplayName.Should().Be("1024px");
        original.MaxSize.Should().Be(1024);
    }

    [Fact]
    public void WhenWithDisplayNameCalledThenNewInstanceIsCreated()
    {
        var original = new ResolutionOption(1024, "1024px");
        var modified = original with { DisplayName = "One K" };

        modified.MaxSize.Should().Be(1024);
        modified.DisplayName.Should().Be("One K");
        original.DisplayName.Should().Be("1024px");
    }

    #endregion

    #region Hash Code Tests

    [Fact]
    public void WhenTwoEqualOptionsGetHashCodeThenHashCodesMatch()
    {
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(1024, "1024px");
        option1.GetHashCode().Should().Be(option2.GetHashCode());
    }

    [Fact]
    public void WhenTwoDifferentOptionsGetHashCodeThenHashCodesAreLikelyDifferent()
    {
        var option1 = new ResolutionOption(1024, "1024px");
        var option2 = new ResolutionOption(512, "512px");
        option1.GetHashCode().Should().NotBe(option2.GetHashCode());
    }

    #endregion

    #region ToString Tests

    [Fact]
    public void WhenToStringCalledThenReturnsRecordRepresentation()
    {
        var option = new ResolutionOption(1024, "1024px");
        var str = option.ToString();

        str.Should().Contain("1024");
        str.Should().Contain("1024px");
    }

    #endregion

    #region Deconstruction Tests

    [Fact]
    public void WhenDeconstructedThenValuesAreCorrect()
    {
        var option = new ResolutionOption(1024, "1024px");
        var (maxSize, displayName) = option;

        maxSize.Should().Be(1024);
        displayName.Should().Be("1024px");
    }

    #endregion

    #region None and Custom Distinction Tests

    [Fact]
    public void WhenNoneAndCustomComparedThenTheyAreDifferent()
    {
        ResolutionOption.None.Should().NotBe(ResolutionOption.Custom);
    }

    [Fact]
    public void WhenNoneAndCustomBothHaveNullMaxSizeThenDistinguishedByDisplayName()
    {
        ResolutionOption.None.MaxSize.Should().BeNull();
        ResolutionOption.Custom.MaxSize.Should().BeNull();
        ResolutionOption.None.DisplayName.Should().NotBe(ResolutionOption.Custom.DisplayName);
    }

    #endregion
}
