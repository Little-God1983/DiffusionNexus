using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="SpellCheckQualityCheck"/>.
/// Uses a mocked <see cref="ISpellChecker"/> — no real dictionary needed.
/// </summary>
public class SpellCheckQualityCheckTests
{
    private readonly Mock<ISpellChecker> _spellCheckerMock = new();
    private readonly SpellCheckQualityCheck _sut;

    public SpellCheckQualityCheckTests()
    {
        _spellCheckerMock.Setup(s => s.IsReady).Returns(true);
        _spellCheckerMock
            .Setup(s => s.FindMisspelledWords(It.IsAny<string>()))
            .Returns<string>(_ => []);
        _sut = new SpellCheckQualityCheck(_spellCheckerMock.Object);
    }

    private static DatasetConfig MakeConfig(LoraType type = LoraType.Character) => new()
    {
        FolderPath = @"C:\fake\dataset",
        TriggerWord = "ohwx",
        LoraType = type
    };

    #region Applicability & Metadata

    [Theory]
    [InlineData(LoraType.Character)]
    [InlineData(LoraType.Concept)]
    [InlineData(LoraType.Style)]
    public void WhenAnyLoraTypeThenIsApplicable(LoraType type)
    {
        _sut.IsApplicable(type).Should().BeTrue();
    }

    [Fact]
    public void WhenCheckMetadataInspectedThenValuesAreCorrect()
    {
        _sut.Order.Should().Be(6);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().Be("Spell Check");
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Spell Checker Not Ready

    [Fact]
    public void WhenSpellCheckerNotReadyThenReturnsNoIssues()
    {
        _spellCheckerMock.Setup(s => s.IsReady).Returns(false);
        var sut = new SpellCheckQualityCheck(_spellCheckerMock.Object);
        var captions = MakeCaptions(("img1.txt", "A womn standing in a park."));

        var issues = sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    #endregion

    #region No Misspellings

    [Fact]
    public void WhenAllWordsAreCorrectThenReturnsNoIssues()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a sunlit park wearing a blue dress."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenNoCaptionsThenReturnsEmpty()
    {
        var issues = _sut.Run([], MakeConfig());

        issues.Should().BeEmpty();
    }

    #endregion

    #region Misspelled Words Detected

    [Fact]
    public void WhenCaptionHasMisspelledWordsThenReportsWarning()
    {
        _spellCheckerMock
            .Setup(s => s.FindMisspelledWords("A womn standing in a prk."))
            .Returns(["womn", "prk"]);

        var captions = MakeCaptions(("img1.txt", "A womn standing in a prk."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle();
        var issue = issues[0];
        issue.Severity.Should().Be(IssueSeverity.Warning);
        issue.CheckName.Should().Be("Spell Check");
        issue.Message.Should().Contain("2");
        issue.Details.Should().Contain("womn");
        issue.Details.Should().Contain("prk");
        issue.AffectedFiles.Should().ContainSingle();
    }

    [Fact]
    public void WhenMultipleCaptionsHaveSameMisspelledWordThenReportsFileCount()
    {
        _spellCheckerMock
            .Setup(s => s.FindMisspelledWords(It.Is<string>(t => t.Contains("womn"))))
            .Returns(["womn"]);

        var captions = MakeCaptions(
            ("a.txt", "A womn standing in a park."),
            ("b.txt", "A womn sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle();
        var issue = issues[0];
        issue.Details.Should().Contain("womn");
        issue.Details.Should().Contain("2 files");
        issue.AffectedFiles.Should().HaveCount(2);
    }

    #endregion

    #region Booru Tags Skipped

    [Fact]
    public void WhenCaptionIsBooruTagsThenSkipsSpellCheck()
    {
        _spellCheckerMock
            .Setup(s => s.FindMisspelledWords(It.IsAny<string>()))
            .Returns(["xyzfake"]);

        var captions = new List<CaptionFile>
        {
            new()
            {
                FilePath = @"C:\fake\dataset\img1.txt",
                RawText = "1girl, blck_hair, blue_eyes",
                DetectedStyle = CaptionStyle.BooruTags
            }
        };

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
        _spellCheckerMock.Verify(
            s => s.FindMisspelledWords(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void WhenMixOfBooruAndNaturalLanguageThenOnlyChecksNaturalLanguage()
    {
        _spellCheckerMock
            .Setup(s => s.FindMisspelledWords("A womn standing in a park."))
            .Returns(["womn"]);

        var captions = new List<CaptionFile>
        {
            new()
            {
                FilePath = @"C:\fake\dataset\a.txt",
                RawText = "1girl, blck_hair, blue_eyes",
                DetectedStyle = CaptionStyle.BooruTags
            },
            new()
            {
                FilePath = @"C:\fake\dataset\b.txt",
                RawText = "A womn standing in a park.",
                DetectedStyle = CaptionStyle.NaturalLanguage
            }
        };

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle();
        issues[0].AffectedFiles.Should().ContainSingle()
            .Which.Should().Contain("b.txt");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WhenCaptionsIsNullThenThrowsArgumentNullException()
    {
        var act = () => _sut.Run(null!, MakeConfig());

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenConfigIsNullThenThrowsArgumentNullException()
    {
        var act = () => _sut.Run([], null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenSpellCheckerIsNullThenConstructorThrows()
    {
        var act = () => new SpellCheckQualityCheck(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenCaptionIsEmptyTextThenSkipsWithoutError()
    {
        var captions = MakeCaptions(("img1.txt", ""));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenCaptionIsWhitespaceOnlyThenSkipsWithoutError()
    {
        var captions = MakeCaptions(("img1.txt", "   \t  "));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    #endregion

    #region Helpers

    private static List<CaptionFile> MakeCaptions(
        params (string FileName, string Text)[] entries)
    {
        return entries.Select(e => new CaptionFile
        {
            FilePath = Path.Combine(@"C:\fake\dataset", e.FileName),
            RawText = e.Text,
            DetectedStyle = TextHelpers.DetectCaptionStyle(e.Text)
        }).ToList();
    }

    #endregion
}
