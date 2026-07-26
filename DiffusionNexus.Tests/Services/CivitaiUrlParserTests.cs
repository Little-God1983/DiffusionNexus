using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services;

/// <summary>
/// Unit tests for <see cref="CivitaiUrlParser"/>.
/// Covers the four outcomes: blank input, unparseable input, model-only,
/// version-only and the combined model + version case.
/// </summary>
public class CivitaiUrlParserTests
{
    // ---------------------------------------------------------------
    //  Blank input
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void WhenUrlIsNullOrBlankThenParsingFailsWithAPromptToEnterAUrl(string? url)
    {
        var ok = CivitaiUrlParser.TryResolveIds(url, out var modelId, out var versionId, out var error);

        ok.Should().BeFalse();
        modelId.Should().BeNull();
        versionId.Should().BeNull();
        error.Should().Be("Enter a Civitai URL.");
    }

    // ---------------------------------------------------------------
    //  Unparseable input
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("https://civitai.com/")]
    [InlineData("https://civitai.com/models/")]
    [InlineData("https://civitai.com/models/abc")]
    [InlineData("https://civitai.com/models/not-a-number/slug")]
    [InlineData("https://civitai.com/images/12345")]
    [InlineData("just some text")]
    [InlineData("modelVersionId=555")]
    [InlineData("https://civitai.com/?modelVersionId=abc")]
    [InlineData("https://example.com/download?id=999")]
    public void WhenUrlContainsNoRecognizableIdsThenParsingFailsWithAParseError(string url)
    {
        var ok = CivitaiUrlParser.TryResolveIds(url, out var modelId, out var versionId, out var error);

        ok.Should().BeFalse();
        modelId.Should().BeNull();
        versionId.Should().BeNull();
        error.Should().Be("Could not parse a Model ID or Model Version ID from the URL.");
    }

    // ---------------------------------------------------------------
    //  Model id only
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlIsAPlainModelPageThenOnlyTheModelIdIsResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/12345", out var modelId, out var versionId, out var error);

        ok.Should().BeTrue();
        modelId.Should().Be(12345);
        versionId.Should().BeNull();
        error.Should().BeEmpty();
    }

    [Fact]
    public void WhenModelPageHasASlugThenTheSlugIsIgnored()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/4384/dreamshaper", out var modelId, out var versionId, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(4384);
        versionId.Should().BeNull();
    }

    [Fact]
    public void WhenUrlIsPaddedWithWhitespaceThenItIsTrimmedBeforeParsing()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "   https://civitai.com/models/777   ", out var modelId, out _, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(777);
    }

    [Fact]
    public void WhenModelSegmentIsUpperCaseThenItIsStillMatched()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/MODELS/321", out var modelId, out _, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(321);
    }

    [Fact]
    public void WhenUrlHasUnrelatedQueryParametersThenOnlyTheModelIdIsResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/900?type=Checkpoint&sort=Newest",
            out var modelId, out var versionId, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(900);
        versionId.Should().BeNull();
    }

    [Fact]
    public void WhenSeveralModelSegmentsAppearThenTheFirstMatchWins()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/11/a?ref=/models/22", out var modelId, out _, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(11);
    }

    // ---------------------------------------------------------------
    //  Model id + version id
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlCarriesModelVersionQueryThenBothIdsAreResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/12345/my-lora?modelVersionId=67890",
            out var modelId, out var versionId, out var error);

        ok.Should().BeTrue();
        modelId.Should().Be(12345);
        versionId.Should().Be(67890);
        error.Should().BeEmpty();
    }

    [Fact]
    public void WhenModelVersionIsNotTheFirstQueryParameterThenItIsStillResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/1/x?type=LORA&modelVersionId=42",
            out var modelId, out var versionId, out _);

        ok.Should().BeTrue();
        modelId.Should().Be(1);
        versionId.Should().Be(42);
    }

    [Fact]
    public void WhenModelVersionParameterUsesDifferentCasingThenItIsStillResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/1?modelversionid=42", out _, out var versionId, out _);

        ok.Should().BeTrue();
        versionId.Should().Be(42);
    }

    // ---------------------------------------------------------------
    //  Version id only
    // ---------------------------------------------------------------

    [Fact]
    public void WhenUrlCarriesOnlyAVersionQueryThenOnlyTheVersionIdIsResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/?modelVersionId=999", out var modelId, out var versionId, out var error);

        ok.Should().BeTrue();
        modelId.Should().BeNull();
        versionId.Should().Be(999);
        error.Should().BeEmpty();
    }

    [Fact]
    public void WhenVersionQueryFollowsAnAmpersandOnANonModelPageThenItIsResolved()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/user/bob?tab=models&modelVersionId=13",
            out var modelId, out var versionId, out _);

        ok.Should().BeTrue();
        modelId.Should().BeNull();
        versionId.Should().Be(13);
    }

    // ---------------------------------------------------------------
    //  Numeric overflow — digits match the pattern but not an int
    // ---------------------------------------------------------------

    [Fact]
    public void WhenModelIdOverflowsIntThenItIsDiscardedAndParsingFails()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/99999999999", out var modelId, out var versionId, out var error);

        ok.Should().BeFalse();
        modelId.Should().BeNull();
        versionId.Should().BeNull();
        error.Should().Be("Could not parse a Model ID or Model Version ID from the URL.");
    }

    [Fact]
    public void WhenOnlyTheVersionIdOverflowsThenTheModelIdStillSucceeds()
    {
        var ok = CivitaiUrlParser.TryResolveIds(
            "https://civitai.com/models/50?modelVersionId=99999999999",
            out var modelId, out var versionId, out var error);

        ok.Should().BeTrue();
        modelId.Should().Be(50);
        versionId.Should().BeNull();
        error.Should().BeEmpty();
    }

    // ---------------------------------------------------------------
    //  Output contract
    // ---------------------------------------------------------------

    [Fact]
    public void WhenParsingSucceedsThenTheErrorOutputIsAlwaysEmptyRatherThanNull()
    {
        CivitaiUrlParser.TryResolveIds("https://civitai.com/models/1", out _, out _, out var error);

        error.Should().NotBeNull().And.BeEmpty();
    }
}
