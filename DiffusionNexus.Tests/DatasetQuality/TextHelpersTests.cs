using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="TextHelpers"/>.
/// </summary>
public class TextHelpersTests
{
    #region DetectCaptionStyle

    [Fact]
    public void WhenTextIsNaturalLanguageSentenceThenReturnsNaturalLanguage()
    {
        var text = "A woman with brown hair standing in a sunlit park, wearing a blue dress.";

        var result = TextHelpers.DetectCaptionStyle(text);

        result.Should().Be(CaptionStyle.NaturalLanguage);
    }

    [Fact]
    public void WhenTextIsBooruCommaSeparatedTagsThenReturnsBooruTags()
    {
        var text = "1girl, brown hair, blue dress, park, sunlight, standing, solo";

        var result = TextHelpers.DetectCaptionStyle(text);

        result.Should().Be(CaptionStyle.BooruTags);
    }

    [Fact]
    public void WhenTextContainsUnderscoreTokensThenReturnsBooruTags()
    {
        var text = "1girl, brown_hair, blue_eyes, school_uniform, standing";

        var result = TextHelpers.DetectCaptionStyle(text);

        result.Should().Be(CaptionStyle.BooruTags);
    }

    [Fact]
    public void WhenTextIsEmptyThenReturnsUnknown()
    {
        TextHelpers.DetectCaptionStyle("").Should().Be(CaptionStyle.Unknown);
    }

    [Fact]
    public void WhenTextIsWhitespaceThenReturnsUnknown()
    {
        TextHelpers.DetectCaptionStyle("   ").Should().Be(CaptionStyle.Unknown);
    }

    [Fact]
    public void WhenTextIsNullThenReturnsUnknown()
    {
        TextHelpers.DetectCaptionStyle(null!).Should().Be(CaptionStyle.Unknown);
    }

    [Fact]
    public void WhenTextHasSentencesAndHighCommaDensityThenReturnsMixed()
    {
        var text = "A detailed portrait, brown hair, blue eyes, the woman stands in a park. She is wearing a white dress.";

        var result = TextHelpers.DetectCaptionStyle(text);

        // This has both sentence structure AND high comma density
        result.Should().BeOneOf(CaptionStyle.Mixed, CaptionStyle.NaturalLanguage);
    }

    #endregion

    #region ContainsPhrase

    [Fact]
    public void WhenPhraseExistsAsWholeWordThenReturnsTrue()
    {
        TextHelpers.ContainsPhrase("a car is parked", "car").Should().BeTrue();
    }

    [Fact]
    public void WhenPhraseIsSubstringOfAnotherWordThenReturnsFalse()
    {
        TextHelpers.ContainsPhrase("a scar on her face", "car").Should().BeFalse();
    }

    [Fact]
    public void WhenPhraseIsAtStartOfTextThenReturnsTrue()
    {
        TextHelpers.ContainsPhrase("car parked on the street", "car").Should().BeTrue();
    }

    [Fact]
    public void WhenPhraseIsAtEndOfTextThenReturnsTrue()
    {
        TextHelpers.ContainsPhrase("she drove a car", "car").Should().BeTrue();
    }

    [Fact]
    public void WhenPhraseIsCaseDifferentThenReturnsTrue()
    {
        TextHelpers.ContainsPhrase("The CAR is red", "car").Should().BeTrue();
    }

    [Fact]
    public void WhenPhraseIsMultiWordThenMatchesExactly()
    {
        TextHelpers.ContainsPhrase("she has brown hair today", "brown hair").Should().BeTrue();
    }

    [Fact]
    public void WhenTextIsEmptyThenReturnsFalse()
    {
        TextHelpers.ContainsPhrase("", "car").Should().BeFalse();
    }

    [Fact]
    public void WhenPhraseIsEmptyThenReturnsFalse()
    {
        TextHelpers.ContainsPhrase("some text", "").Should().BeFalse();
    }

    #endregion

    #region ContainsFeature

    [Fact]
    public void WhenStyleIsBooruAndExactTagMatchesThenReturnsTrue()
    {
        TextHelpers.ContainsFeature("1girl, brown hair, blue eyes", "brown hair", CaptionStyle.BooruTags)
            .Should().BeTrue();
    }

    [Fact]
    public void WhenStyleIsBooruAndTagIsSubstringOfAnotherThenReturnsFalse()
    {
        TextHelpers.ContainsFeature("1girl, long brown hair, blue eyes", "brown hair", CaptionStyle.BooruTags)
            .Should().BeFalse();
    }

    [Fact]
    public void WhenStyleIsNaturalLanguageThenUsesWordBoundary()
    {
        TextHelpers.ContainsFeature("The woman has brown hair.", "brown hair", CaptionStyle.NaturalLanguage)
            .Should().BeTrue();
    }

    [Fact]
    public void WhenStyleIsUnknownThenFallsBackToWordBoundary()
    {
        TextHelpers.ContainsFeature("She has brown hair and blue eyes.", "brown hair", CaptionStyle.Unknown)
            .Should().BeTrue();
    }

    #endregion

    #region ExtractTokens

    [Fact]
    public void WhenCaptionHasContentWordsThenReturnsLowercaseTokens()
    {
        var tokens = TextHelpers.ExtractTokens("A Woman Standing in the Park");

        tokens.Should().Contain("woman");
        tokens.Should().Contain("standing");
        tokens.Should().Contain("park");
    }

    [Fact]
    public void WhenCaptionHasStopWordsThenTheyAreRemoved()
    {
        var tokens = TextHelpers.ExtractTokens("A woman is standing in the park");

        tokens.Should().NotContain("a");
        tokens.Should().NotContain("is");
        tokens.Should().NotContain("in");
        tokens.Should().NotContain("the");
    }

    [Fact]
    public void WhenCaptionHasUnderscoreTokensThenTheyAreSplit()
    {
        var tokens = TextHelpers.ExtractTokens("brown_hair, blue_eyes");

        tokens.Should().Contain("brown");
        tokens.Should().Contain("hair");
        tokens.Should().Contain("blue");
        tokens.Should().Contain("eyes");
    }

    [Fact]
    public void WhenCaptionIsEmptyThenReturnsEmptyList()
    {
        TextHelpers.ExtractTokens("").Should().BeEmpty();
    }

    [Fact]
    public void WhenCaptionHasPunctuationThenItIsStripped()
    {
        var tokens = TextHelpers.ExtractTokens("Hello, world! How are you?");

        tokens.Should().Contain("hello");
        tokens.Should().Contain("world");
        tokens.Should().NotContain(",");
        tokens.Should().NotContain("!");
    }

    [Fact]
    public void WhenTokensAreSingleCharacterThenTheyAreFiltered()
    {
        var tokens = TextHelpers.ExtractTokens("I am a b c test");

        tokens.Should().NotContain("b");
        tokens.Should().NotContain("c");
        tokens.Should().Contain("test");
    }

    #endregion

    #region ExtractBigrams

    [Fact]
    public void WhenTokensHaveThreeItemsThenReturnsTwoBigrams()
    {
        var tokens = new List<string> { "brown", "hair", "woman" };

        var bigrams = TextHelpers.ExtractBigrams(tokens);

        bigrams.Should().Equal("brown hair", "hair woman");
    }

    [Fact]
    public void WhenTokensHaveOneItemThenReturnsEmpty()
    {
        TextHelpers.ExtractBigrams(["single"]).Should().BeEmpty();
    }

    [Fact]
    public void WhenTokensAreEmptyThenReturnsEmpty()
    {
        TextHelpers.ExtractBigrams([]).Should().BeEmpty();
    }

    #endregion

    #region ExtractTrigrams

    [Fact]
    public void WhenTokensHaveFourItemsThenReturnsTwoTrigrams()
    {
        var tokens = new List<string> { "long", "brown", "hair", "woman" };

        var trigrams = TextHelpers.ExtractTrigrams(tokens);

        trigrams.Should().Equal("long brown hair", "brown hair woman");
    }

    [Fact]
    public void WhenTokensHaveTwoItemsThenReturnsEmpty()
    {
        TextHelpers.ExtractTrigrams(["a", "b"]).Should().BeEmpty();
    }

    #endregion

    #region RemovePhrase

    [Fact]
    public void WhenStyleIsBooruThenRemovesExactTag()
    {
        var result = TextHelpers.RemovePhrase(
            "1girl, bad tag, blue eyes",
            "bad tag",
            CaptionStyle.BooruTags);

        result.Should().Be("1girl, blue eyes");
    }

    [Fact]
    public void WhenStyleIsBooruAndTagNotFoundThenReturnsOriginal()
    {
        var result = TextHelpers.RemovePhrase(
            "1girl, brown hair, blue eyes",
            "red hair",
            CaptionStyle.BooruTags);

        result.Should().Be("1girl, brown hair, blue eyes");
    }

    [Fact]
    public void WhenStyleIsNaturalLanguageThenRemovesWithWordBoundary()
    {
        var result = TextHelpers.RemovePhrase(
            "A woman with bad hair standing in a park.",
            "bad",
            CaptionStyle.NaturalLanguage);

        result.Should().NotContain(" bad ");
        result.Should().Contain("woman");
        result.Should().Contain("hair");
    }

    [Fact]
    public void WhenCaptionIsEmptyThenReturnsEmpty()
    {
        TextHelpers.RemovePhrase("", "tag", CaptionStyle.BooruTags)
            .Should().BeEmpty();
    }

    [Fact]
    public void WhenPhraseIsEmptyThenReturnsOriginal()
    {
        TextHelpers.RemovePhrase("1girl, test", "", CaptionStyle.BooruTags)
            .Should().Be("1girl, test");
    }

    [Fact]
    public void WhenStyleIsBooruAndRemovingLastTagThenNoTrailingComma()
    {
        var result = TextHelpers.RemovePhrase(
            "1girl, blue eyes",
            "blue eyes",
            CaptionStyle.BooruTags);

        result.Should().Be("1girl");
        result.Should().NotEndWith(",");
    }

    #endregion

    #region AppendPhrase

    [Fact]
    public void WhenStyleIsBooruThenAppendsAsTag()
    {
        var result = TextHelpers.AppendPhrase(
            "1girl, brown hair",
            "blue eyes",
            CaptionStyle.BooruTags);

        result.Should().Be("1girl, brown hair, blue eyes");
    }

    [Fact]
    public void WhenStyleIsBooruAndCaptionHasTrailingCommaThenHandlesGracefully()
    {
        var result = TextHelpers.AppendPhrase(
            "1girl, brown hair,",
            "blue eyes",
            CaptionStyle.BooruTags);

        result.Should().Be("1girl, brown hair, blue eyes");
    }

    [Fact]
    public void WhenStyleIsNaturalLanguageThenAppendsWithPeriodAndSpace()
    {
        var result = TextHelpers.AppendPhrase(
            "A woman stands in a park",
            "She has brown hair.",
            CaptionStyle.NaturalLanguage);

        result.Should().Be("A woman stands in a park. She has brown hair.");
    }

    [Fact]
    public void WhenStyleIsNaturalLanguageAndCaptionEndsWithPeriodThenNoDoublePeriod()
    {
        var result = TextHelpers.AppendPhrase(
            "A woman stands in a park.",
            "She has brown hair.",
            CaptionStyle.NaturalLanguage);

        result.Should().Be("A woman stands in a park. She has brown hair.");
        result.Should().NotContain("..");
    }

    [Fact]
    public void WhenCaptionIsEmptyThenReturnsPhraseOnly()
    {
        TextHelpers.AppendPhrase("", "blue eyes", CaptionStyle.BooruTags)
            .Should().Be("blue eyes");
    }

    [Fact]
    public void WhenAppendedPhraseIsEmptyThenReturnsOriginal()
    {
        TextHelpers.AppendPhrase("1girl, test", "", CaptionStyle.BooruTags)
            .Should().Be("1girl, test");
    }

    #endregion

    #region SplitTags

    [Fact]
    public void WhenTagsAreCommaSeparatedThenSplitsAndTrims()
    {
        var tags = TextHelpers.SplitTags("1girl, brown hair , blue eyes,  standing");

        tags.Should().Equal("1girl", "brown hair", "blue eyes", "standing");
    }

    [Fact]
    public void WhenTagsHaveEmptySegmentsThenTheyAreFiltered()
    {
        var tags = TextHelpers.SplitTags("1girl,, brown hair,  ,blue eyes");

        tags.Should().Equal("1girl", "brown hair", "blue eyes");
    }

    #endregion
}
