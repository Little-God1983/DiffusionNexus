using DiffusionNexus.Domain.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Domain.Services;

/// <summary>
/// Unit tests for <see cref="CaptionPostProcessor.Process"/>.
/// Covers trigger-word prepending, blacklist removal, whitespace normalization
/// and edge cases.
/// </summary>
public class CaptionPostProcessorTests
{
    [Fact]
    public void Process_TrimsLeadingAndTrailingWhitespace()
    {
        var result = CaptionPostProcessor.Process("   a beautiful landscape   ");

        result.Should().Be("a beautiful landscape");
    }

    [Fact]
    public void Process_PrependsTriggerWord_WithCommaSeparator()
    {
        var result = CaptionPostProcessor.Process("a cat sitting on a mat", triggerWord: "myToken");

        result.Should().Be("myToken, a cat sitting on a mat");
    }

    [Fact]
    public void Process_TrimsTriggerWord_BeforePrepending()
    {
        var result = CaptionPostProcessor.Process("a cat", triggerWord: "  myToken  ");

        result.Should().Be("myToken, a cat");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Process_DoesNotPrepend_WhenTriggerWordIsNullOrWhitespace(string? trigger)
    {
        var result = CaptionPostProcessor.Process("a cat", triggerWord: trigger);

        result.Should().Be("a cat");
    }

    [Fact]
    public void Process_RemovesBlacklistedWords_AsWholeWordsCaseInsensitive()
    {
        var result = CaptionPostProcessor.Process(
            "a beautiful Cat sitting on a mat",
            blacklistedWords: new[] { "cat" });

        result.Should().Be("a beautiful sitting on a mat");
    }

    [Fact]
    public void Process_DoesNotRemovePartialWordMatches()
    {
        // "cat" should not strip "category" thanks to \b word-boundary regex.
        var result = CaptionPostProcessor.Process(
            "a category of cat",
            blacklistedWords: new[] { "cat" });

        result.Should().Be("a category of");
    }

    [Fact]
    public void Process_CollapsesWhitespace_AfterRemoval()
    {
        var result = CaptionPostProcessor.Process(
            "alpha beta gamma delta",
            blacklistedWords: new[] { "beta", "gamma" });

        result.Should().Be("alpha delta");
    }

    [Fact]
    public void Process_AppliesBlacklistThenTriggerWord()
    {
        var result = CaptionPostProcessor.Process(
            "an unwanted cat",
            triggerWord: "tok",
            blacklistedWords: new[] { "unwanted" });

        result.Should().Be("tok, an cat");
    }

    [Fact]
    public void Process_HandlesEmptyBlacklist_AsNoOp()
    {
        var result = CaptionPostProcessor.Process(
            "a cat",
            blacklistedWords: Array.Empty<string>());

        result.Should().Be("a cat");
    }

    [Fact]
    public void Process_EscapesRegexMetaCharacters_WithoutThrowing()
    {
        // The blacklist contains regex metacharacters. They must be escaped so
        // the call does not throw a RegexParseException. Note: \b word-boundary
        // semantics mean tokens ending in non-word characters (like "C++") will
        // not actually be removed — but the call must succeed regardless.
        var act = () => CaptionPostProcessor.Process(
            "C++ is a language with (parens) and a.dot",
            blacklistedWords: new[] { "C++", "(parens)", "a.dot" });

        act.Should().NotThrow();
        var result = act();
        result.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Process_ReturnsEmptyString_WhenInputIsOnlyWhitespace()
    {
        var result = CaptionPostProcessor.Process("    ");

        result.Should().BeEmpty();
    }
}
