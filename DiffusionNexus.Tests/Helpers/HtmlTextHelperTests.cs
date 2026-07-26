using DiffusionNexus.UI.Helpers;
using FluentAssertions;

namespace DiffusionNexus.Tests.Helpers;

/// <summary>
/// Unit tests for <see cref="HtmlTextHelper"/>.
/// Covers tag stripping, block/line-break handling, list bullets, named and numeric
/// entity decoding (including parse failures) and whitespace normalization.
/// </summary>
public class HtmlTextHelperTests
{
    // ---------------------------------------------------------------
    //  Guard clauses
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\t")]
    public void WhenHtmlIsNullOrBlankThenResultIsEmptyString(string? html)
    {
        HtmlTextHelper.HtmlToPlainText(html).Should().BeEmpty();
    }

    [Fact]
    public void WhenHtmlHasNoMarkupThenTextIsReturnedTrimmed()
    {
        HtmlTextHelper.HtmlToPlainText("  plain description  ").Should().Be("plain description");
    }

    // ---------------------------------------------------------------
    //  <br> handling
    // ---------------------------------------------------------------

    [Fact]
    public void WhenBreakTagsAppearInAnyFormThenEachBecomesASingleNewline()
    {
        HtmlTextHelper.HtmlToPlainText("a<br>b<br/>c<br />d<BR>e")
            .Should().Be("a\nb\nc\nd\ne");
    }

    [Fact]
    public void WhenBreakTagCarriesAttributesThenItIsStrippedWithoutInsertingANewline()
    {
        // The <br> regex allows no attributes, so such a tag falls through to the
        // generic tag stripper and produces no line break at all.
        HtmlTextHelper.HtmlToPlainText("a<br class=\"x\">b").Should().Be("ab");
    }

    // ---------------------------------------------------------------
    //  Block elements
    // ---------------------------------------------------------------

    [Fact]
    public void WhenParagraphIsClosedThenItBecomesABlankLineSeparator()
    {
        HtmlTextHelper.HtmlToPlainText("<p>First</p><p>Second</p>")
            .Should().Be("First\n\nSecond");
    }

    [Fact]
    public void WhenHeadingIsUsedThenItsTextIsKeptAndSeparatedByABlankLine()
    {
        HtmlTextHelper.HtmlToPlainText("<h2>Title</h2>Body")
            .Should().Be("Title\n\nBody");
    }

    [Fact]
    public void WhenBlockTagCarriesAttributesThenTheOpeningTagIsStillRemoved()
    {
        HtmlTextHelper.HtmlToPlainText("<div class=\"note\" id=\"a\">Hello</div>")
            .Should().Be("Hello");
    }

    [Fact]
    public void WhenBlockTagIsNeverClosedThenTheOpeningTagIsStillRemoved()
    {
        HtmlTextHelper.HtmlToPlainText("<p>unclosed").Should().Be("unclosed");
    }

    [Fact]
    public void WhenTagsAreNestedThenOnlyTheirTextSurvives()
    {
        HtmlTextHelper.HtmlToPlainText("<p><strong>Bold</strong> <em>and <u>deep</u></em> text</p>")
            .Should().Be("Bold and deep text");
    }

    [Fact]
    public void WhenUnknownInlineTagsAreUsedThenTheyAreStrippedWithoutSeparators()
    {
        HtmlTextHelper.HtmlToPlainText("<span><a href=\"http://x\">link</a></span>")
            .Should().Be("link");
    }

    // ---------------------------------------------------------------
    //  Lists
    // ---------------------------------------------------------------

    [Fact]
    public void WhenListItemIsUsedThenItIsPrefixedWithABullet()
    {
        HtmlTextHelper.HtmlToPlainText("<li>Item</li>").Should().Be("• Item");
    }

    [Fact]
    public void WhenListItemCarriesAttributesThenItIsStillBulleted()
    {
        HtmlTextHelper.HtmlToPlainText("<li class=\"x\">Item</li>").Should().Be("• Item");
    }

    [Fact]
    public void WhenUnorderedListIsRenderedThenEachItemGetsItsOwnBulletedLine()
    {
        // </li> becomes a blank line before the next bullet is emitted.
        HtmlTextHelper.HtmlToPlainText("<ul><li>Alpha</li><li>Beta</li></ul>")
            .Should().Be("• Alpha\n\n • Beta");
    }

    // ---------------------------------------------------------------
    //  Named entities
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("&amp;", "&")]
    [InlineData("&lt;", "<")]
    [InlineData("&gt;", ">")]
    [InlineData("&quot;", "\"")]
    [InlineData("&apos;", "'")]
    [InlineData("&#39;", "'")]
    public void WhenNamedEntityIsPresentThenItIsDecoded(string html, string expected)
    {
        HtmlTextHelper.HtmlToPlainText("x" + html + "y").Should().Be("x" + expected + "y");
    }

    [Fact]
    public void WhenNonBreakingSpaceEntityIsPresentThenItBecomesAnOrdinarySpace()
    {
        HtmlTextHelper.HtmlToPlainText("a&nbsp;b").Should().Be("a b");
    }

    [Fact]
    public void WhenSeveralNonBreakingSpacesAreAdjacentThenTheyCollapseIntoOneSpace()
    {
        HtmlTextHelper.HtmlToPlainText("a&nbsp;&nbsp;&nbsp;b").Should().Be("a b");
    }

    [Fact]
    public void WhenEscapedMarkupIsDecodedThenItIsNotReinterpretedAsTags()
    {
        // Tag stripping runs before entity decoding, so escaped markup survives as text.
        HtmlTextHelper.HtmlToPlainText("&lt;p&gt;hello&lt;/p&gt;").Should().Be("<p>hello</p>");
    }

    [Fact]
    public void WhenAmpersandEntityWrapsAnotherEntityThenDecodingIsAppliedTwice()
    {
        // Sequential string replacements decode "&amp;lt;" all the way down to "<".
        HtmlTextHelper.HtmlToPlainText("&amp;lt;").Should().Be("<");
    }

    [Fact]
    public void WhenDoubleEscapedAmpersandIsGivenThenOnlyTheOuterEntityIsDecoded()
    {
        HtmlTextHelper.HtmlToPlainText("&amp;amp;").Should().Be("&amp;");
    }

    [Theory]
    [InlineData("&amp")]
    [InlineData("&lt")]
    [InlineData("&nbsp")]
    [InlineData("& amp;")]
    [InlineData("&unknown;")]
    public void WhenEntityIsMalformedOrUnknownThenItIsLeftUntouched(string html)
    {
        HtmlTextHelper.HtmlToPlainText(html).Should().Be(html);
    }

    // ---------------------------------------------------------------
    //  Numeric entities
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("&#65;", "A")]
    [InlineData("&#8212;", "—")]
    [InlineData("&#32;", " ")]
    public void WhenDecimalEntityIsPresentThenItIsDecodedToItsCharacter(string html, string expected)
    {
        HtmlTextHelper.HtmlToPlainText("x" + html + "y").Should().Be("x" + expected + "y");
    }

    [Theory]
    [InlineData("&#x41;", "A")]
    [InlineData("&#x2764;", "❤")]
    [InlineData("&#X41;", "&#X41;")] // hex regex is case-sensitive on the 'x'
    public void WhenHexEntityIsPresentThenItIsDecodedOnlyForLowercaseX(string html, string expected)
    {
        HtmlTextHelper.HtmlToPlainText("x" + html + "y").Should().Be("x" + expected + "y");
    }

    [Fact]
    public void WhenHexEntityUsesUppercaseDigitsThenItIsStillDecoded()
    {
        // 0xABC -> U+0ABC; the hex regex accepts upper-case digits.
        HtmlTextHelper.HtmlToPlainText("&#xABC;").Should().Be("઼");
    }

    [Fact]
    public void WhenDecimalEntityOverflowsIntThenItIsLeftAsLiteralText()
    {
        const string html = "&#99999999999;";

        HtmlTextHelper.HtmlToPlainText(html).Should().Be(html);
    }

    [Fact]
    public void WhenHexEntityOverflowsIntThenItIsLeftAsLiteralText()
    {
        const string html = "&#xFFFFFFFFFF;";

        HtmlTextHelper.HtmlToPlainText(html).Should().Be(html);
    }

    [Theory]
    [InlineData("&#65")]   // missing terminator
    [InlineData("&#;")]    // no digits
    [InlineData("&#x;")]   // no hex digits
    [InlineData("&#xZZ;")] // not hex digits
    public void WhenNumericEntityIsMalformedThenItIsLeftUntouched(string html)
    {
        HtmlTextHelper.HtmlToPlainText(html).Should().Be(html);
    }

    [Fact]
    public void WhenEntityIsAboveTheBasicMultilingualPlaneThenTheCodePointIsTruncated()
    {
        // (char)0x1F600 truncates to 0xF600 — astral code points are mangled, not surrogate-paired.
        HtmlTextHelper.HtmlToPlainText("&#128512;").Should().Be("");
    }

    [Fact]
    public void WhenBothDecimalAndHexEntitiesAppearThenBothAreDecoded()
    {
        HtmlTextHelper.HtmlToPlainText("&#72;&#x69;").Should().Be("Hi");
    }

    // ---------------------------------------------------------------
    //  Whitespace normalization
    // ---------------------------------------------------------------

    [Fact]
    public void WhenRunsOfSpacesAndTabsAppearThenTheyCollapseIntoASingleSpace()
    {
        HtmlTextHelper.HtmlToPlainText("a   \t  b").Should().Be("a b");
    }

    [Fact]
    public void WhenTextHasBlankLinesThenTheyArePreservedAsASingleBlankLine()
    {
        HtmlTextHelper.HtmlToPlainText("a\n\nb").Should().Be("a\n\nb");
    }

    [Fact]
    public void WhenThreeOrMoreNewlinesAppearThenTheyCollapseIntoTwo()
    {
        HtmlTextHelper.HtmlToPlainText("a\n\n\n\n\nb").Should().Be("a\n\nb");
    }

    [Fact]
    public void WhenCarriageReturnsArePresentThenTheyAreTreatedAsHorizontalWhitespace()
    {
        // \r matches the "space but not newline" class and collapses to a space.
        HtmlTextHelper.HtmlToPlainText("a\r\nb").Should().Be("a \nb");
    }

    [Fact]
    public void WhenSingleNewlinesSeparateTextThenTheyArePreserved()
    {
        HtmlTextHelper.HtmlToPlainText("line1\nline2").Should().Be("line1\nline2");
    }

    // ---------------------------------------------------------------
    //  Lossy / surprising input
    // ---------------------------------------------------------------

    [Fact]
    public void WhenAngleBracketHasNoClosingBracketThenItSurvivesAsText()
    {
        HtmlTextHelper.HtmlToPlainText("a < b").Should().Be("a < b");
    }

    [Fact]
    public void WhenBareComparisonLooksLikeATagThenTheSpanBetweenBracketsIsRemoved()
    {
        // "< 10 >" matches the generic tag pattern and is stripped — undecoded math is lossy.
        HtmlTextHelper.HtmlToPlainText("5 < 10 > 3").Should().Be("5 3");
    }

    // ---------------------------------------------------------------
    //  Realistic composition
    // ---------------------------------------------------------------

    [Fact]
    public void WhenTypicalCivitaiDescriptionIsGivenThenItRendersAsReadablePlainText()
    {
        const string html =
            "<p>Use <strong>weight&nbsp;0.8</strong>.</p>" +
            "<p>Trigger: &quot;anime&quot; &amp; &lt;style&gt;<br>Notes below.</p>";

        HtmlTextHelper.HtmlToPlainText(html)
            .Should().Be("Use weight 0.8.\n\nTrigger: \"anime\" & <style>\nNotes below.");
    }
}
