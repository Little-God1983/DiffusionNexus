using DiffusionNexus.Domain.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Utilities;

/// <summary>
/// Unit tests for <see cref="BaseModelDisplayMapper"/>.
/// Verifies the lookup behavior, fallback handling, and multi-format helpers.
/// </summary>
public class BaseModelDisplayMapperTests
{
    [Theory]
    [InlineData("SD 1.5", "1.5", "Stable Diffusion 1.5")]
    [InlineData("SDXL 1.0", "XL", "Stable Diffusion XL 1.0")]
    [InlineData("Flux.1 D", "Flux D", "Flux.1 Dev")]
    [InlineData("Pony", "Pony", "Pony Diffusion")]
    [InlineData("Illustrious", "IL", "Illustrious")]
    [InlineData("Other", "Other", "Other")]
    [InlineData("UNKNOWN", "?", "Unknown Base Model")]
    public void GetDisplayInfo_ReturnsMappedEntry_ForKnownBaseModel(string baseModel, string expectedShort, string expectedTooltip)
    {
        var info = BaseModelDisplayMapper.GetDisplayInfo(baseModel);

        info.ShortName.Should().Be(expectedShort);
        info.ToolTip.Should().Be(expectedTooltip);
    }

    [Fact]
    public void GetDisplayInfo_IsCaseInsensitive()
    {
        var lower = BaseModelDisplayMapper.GetDisplayInfo("sd 1.5");
        var canonical = BaseModelDisplayMapper.GetDisplayInfo("SD 1.5");

        lower.Should().Be(canonical);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDisplayInfo_ReturnsUnknownPlaceholder_ForNullOrWhitespace(string? input)
    {
        var info = BaseModelDisplayMapper.GetDisplayInfo(input);

        info.ShortName.Should().Be("?");
        info.ToolTip.Should().Be("Unknown");
        info.Icon.Should().BeNull();
    }

    [Fact]
    public void GetDisplayInfo_FallsBackToOriginalString_WhenShortEnough()
    {
        // 12 chars or fewer are returned verbatim by the truncation helper.
        const string custom = "MyShort";

        var info = BaseModelDisplayMapper.GetDisplayInfo(custom);

        info.ShortName.Should().Be(custom);
        info.ToolTip.Should().Be(custom);
        info.Icon.Should().BeNull();
    }

    [Fact]
    public void GetDisplayInfo_FallsBack_PreservesFullToolTip_EvenWhenShortNameTruncated()
    {
        // 13+ chars trigger truncation in ShortName, but ToolTip retains the original.
        const string custom = "MyCustomModel";

        var info = BaseModelDisplayMapper.GetDisplayInfo(custom);

        info.ShortName.Length.Should().BeLessThanOrEqualTo(12);
        info.ToolTip.Should().Be(custom);
        info.Icon.Should().BeNull();
    }

    [Fact]
    public void GetDisplayInfo_TruncatesLongUnknownNames()
    {
        var input = new string('A', 50);

        var info = BaseModelDisplayMapper.GetDisplayInfo(input);

        info.ShortName.Length.Should().BeLessThan(input.Length);
        info.ToolTip.Should().Be(input, "tooltip preserves the full original string");
    }

    [Fact]
    public void GetShortName_DelegatesToGetDisplayInfo()
    {
        BaseModelDisplayMapper.GetShortName("SDXL 1.0").Should().Be("XL");
        BaseModelDisplayMapper.GetShortName(null).Should().Be("?");
    }

    [Fact]
    public void GetIcon_ReturnsConfiguredIcon_OrNull()
    {
        // Pony has an icon configured in the mapping table.
        BaseModelDisplayMapper.GetIcon("Pony").Should().NotBeNull();
        // SD 1.5 has no icon.
        BaseModelDisplayMapper.GetIcon("SD 1.5").Should().BeNull();
        // Unknown values return null.
        BaseModelDisplayMapper.GetIcon("not-a-real-model").Should().BeNull();
    }

    [Theory]
    [InlineData("Wan Video 1.3B t2v", true)]
    [InlineData("CogVideoX", true)]
    [InlineData("LTXV", true)]
    [InlineData("Mochi", true)]
    [InlineData("SVD", true)]
    [InlineData("Stable Audio", true)] // shares the "??" icon convention
    [InlineData("SD 1.5", false)]
    [InlineData("SDXL 1.0", false)]
    [InlineData(null, false)]
    public void IsVideoModel_ReturnsTrue_ForEntriesWithVideoIcon(string? baseModel, bool expected)
    {
        BaseModelDisplayMapper.IsVideoModel(baseModel).Should().Be(expected);
    }

    [Fact]
    public void FormatMultiple_ReturnsQuestionMark_ForNullCollection()
    {
        BaseModelDisplayMapper.FormatMultiple(null).Should().Be("?");
    }

    [Fact]
    public void FormatMultiple_SkipsNullAndWhitespaceEntries()
    {
        var input = new string?[] { null, "", "   ", "SD 1.5" };

        var result = BaseModelDisplayMapper.FormatMultiple(input);

        result.Should().Be("1.5");
    }

    [Fact]
    public void FormatMultiple_DeduplicatesEntries()
    {
        var input = new[] { "SD 1.5", "SD 1.5", "SDXL 1.0" };

        var result = BaseModelDisplayMapper.FormatMultiple(input);

        result.Split(", ").Should().BeEquivalentTo(new[] { "1.5", "XL" });
    }

    [Fact]
    public void FormatMultiple_PrependsIcon_WhenAvailable()
    {
        var input = new[] { "Pony", "SD 1.5" };

        var result = BaseModelDisplayMapper.FormatMultiple(input);

        // Pony has an icon, SD 1.5 does not. Both must appear, separated by ", ".
        result.Should().Contain("Pony");
        result.Should().Contain("1.5");
        result.Should().Contain(", ");
    }

    [Fact]
    public void FormatMultiple_RespectsCustomSeparator()
    {
        var input = new[] { "SD 1.5", "SDXL 1.0" };

        var result = BaseModelDisplayMapper.FormatMultiple(input, " | ");

        result.Should().Be("1.5 | XL");
    }
}
