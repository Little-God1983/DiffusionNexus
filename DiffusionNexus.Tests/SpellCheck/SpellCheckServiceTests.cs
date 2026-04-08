using DiffusionNexus.UI.Services.SpellCheck;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.SpellCheck;

/// <summary>
/// Unit tests for <see cref="SpellCheckService"/>, focusing on the CheckText
/// tokenizer edge cases that previously caused infinite loops.
/// Uses a minimal Hunspell dictionary written to a random temp directory.
/// </summary>
public class SpellCheckServiceTests
{
    private readonly SpellCheckService _sut;
    private readonly string _tempDir;

    public SpellCheckServiceTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            $"spellcheck_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Minimal Hunspell files so the dictionary loads and CheckText runs the tokenizer
        File.WriteAllText(
            Path.Combine(_tempDir, "en_US.aff"),
            "SET UTF-8\n");

        File.WriteAllText(
            Path.Combine(_tempDir, "en_US.dic"),
            "3\nhello\nworld\ntest\n");

        var userDict = new Mock<IUserDictionaryService>();
        userDict.Setup(d => d.Contains(It.IsAny<string>())).Returns(false);

        _sut = new SpellCheckService(userDict.Object, _tempDir);
    }

    [Fact]
    public void WhenDictionaryLoadedThenServiceIsReady()
    {
        _sut.IsReady.Should().BeTrue();
    }

    [Theory]
    [InlineData("--")]
    [InlineData("---")]
    [InlineData("'")]
    [InlineData("''")]
    [InlineData("'-")]
    [InlineData("-'")]
    [InlineData("---'---")]
    public void WhenTextIsOnlyHyphensOrApostrophesThenCheckTextCompletes(string text)
    {
        var result = _sut.CheckText(text);

        result.Should().NotBeNull();
        result.Should().BeEmpty("pure punctuation tokens are not real words");
    }

    [Theory]
    [InlineData("hello -- world")]
    [InlineData("test ' test")]
    [InlineData("hello---world")]
    [InlineData("hello-'-world")]
    public void WhenTextContainsStandaloneHyphensOrApostrophesBetweenWordsThenCheckTextCompletes(string text)
    {
        var result = _sut.CheckText(text);

        result.Should().NotBeNull();
    }

    [Theory]
    [InlineData("hello-", 0)]
    [InlineData("world'", 0)]
    [InlineData("hello- world-", 0)]
    public void WhenWordsHaveTrailingHyphensOrApostrophesThenTokenIsTrimmedCleanly(string text, int expectedErrors)
    {
        var result = _sut.CheckText(text);

        result.Should().HaveCount(expectedErrors);
    }

    [Fact]
    public void WhenKnownWordUsedThenNoErrorsReported()
    {
        var result = _sut.CheckText("hello world test");

        result.Should().BeEmpty();
    }

    [Fact]
    public void WhenMisspelledWordUsedThenErrorReported()
    {
        var result = _sut.CheckText("hello xyzzy world");

        result.Should().ContainSingle()
            .Which.Word.Should().Be("xyzzy");
    }
}
