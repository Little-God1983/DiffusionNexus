using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="TriggerWordCheck"/>.
/// Tests use in-memory <see cref="CaptionFile"/> records — no disk I/O needed.
/// </summary>
public class TriggerWordCheckTests
{
    private readonly TriggerWordCheck _sut = new();

    private static DatasetConfig MakeConfig(
        LoraType type = LoraType.Character,
        string? triggerWord = "ohwx") => new()
    {
        FolderPath = @"C:\fake\dataset",
        TriggerWord = triggerWord,
        LoraType = type
    };

    #region Applicability

    [Theory]
    [InlineData(LoraType.Character)]
    [InlineData(LoraType.Concept)]
    public void WhenCharacterOrConceptThenIsApplicable(LoraType type)
    {
        _sut.IsApplicable(type).Should().BeTrue();
    }

    [Fact]
    public void WhenStyleThenIsNotApplicable()
    {
        _sut.IsApplicable(LoraType.Style).Should().BeFalse();
    }

    [Fact]
    public void WhenCheckMetadataInspectedThenOrderIsTwo()
    {
        _sut.Order.Should().Be(2);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().NotBeNullOrWhiteSpace();
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Missing Trigger Word

    [Fact]
    public void WhenTriggerMissingFromCaptionThenReportsCritical()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("missing"));
    }

    [Fact]
    public void WhenTriggerMissingThenFixSuggestionPrependsTrigger()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        var missingIssue = issues.Single(i => i.Message.Contains("missing"));
        missingIssue.FixSuggestions.Should().ContainSingle();

        var fix = missingIssue.FixSuggestions[0];
        fix.Edits.Should().ContainSingle();
        fix.Edits[0].NewText.Should().StartWith("ohwx");
    }

    [Fact]
    public void WhenTriggerPresentThenNoMissingIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman standing in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("missing"));
    }

    [Fact]
    public void WhenMultipleCaptionsMissingTriggerThenSingleIssueWithAllFiles()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman in a park."),
            ("b.txt", "A man on a bench."),
            ("c.txt", "ohwx standing near a tree."));

        var issues = _sut.Run(captions, MakeConfig());

        var missingIssue = issues.Single(i => i.Message.Contains("missing"));
        missingIssue.AffectedFiles.Should().HaveCount(2);
        missingIssue.FixSuggestions[0].Edits.Should().HaveCount(2);
    }

    [Fact]
    public void WhenTriggerMissingFromBooruCaptionThenFixPrependsBooruStyle()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, brown hair, blue eyes", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        var fix = issues.Single(i => i.Message.Contains("missing")).FixSuggestions[0];
        fix.Edits[0].NewText.Should().Be("ohwx, 1girl, brown hair, blue eyes");
    }

    [Fact]
    public void WhenTriggerMissingFromNlCaptionThenFixPrependsWithSpace()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman standing in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        var fix = issues.Single(i => i.Message.Contains("missing")).FixSuggestions[0];
        fix.Edits[0].NewText.Should().Be("ohwx A woman standing in a park.");
    }

    #endregion

    #region Case Mismatch

    [Fact]
    public void WhenTriggerHasWrongCaseThenReportsCritical()
    {
        var captions = MakeCaptions(
            ("a.txt", "OHWX a woman in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("casing"));
    }

    [Fact]
    public void WhenCaseMismatchThenFixCorrectsCasing()
    {
        var captions = MakeCaptions(
            ("a.txt", "OHWX a woman in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        var caseIssue = issues.Single(i => i.Message.Contains("casing"));
        caseIssue.FixSuggestions.Should().ContainSingle();

        var fix = caseIssue.FixSuggestions[0];
        fix.Edits[0].NewText.Should().Contain("ohwx");
        fix.Edits[0].NewText.Should().NotContain("OHWX");
    }

    [Fact]
    public void WhenCaseMismatchInBooruTagsThenFixCorrectsCasing()
    {
        var captions = MakeCaptions(
            ("a.txt", "OHWX, 1girl, brown hair", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        var fix = issues.Single(i => i.Message.Contains("casing")).FixSuggestions[0];
        fix.Edits[0].NewText.Should().Be("ohwx, 1girl, brown hair");
    }

    [Fact]
    public void WhenExactCaseMatchExistsThenNoCaseMismatchIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("casing"));
    }

    [Fact]
    public void WhenBothExactAndWrongCaseExistThenNoMismatchForExactFile()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."),
            ("b.txt", "OHWX a man on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        var caseIssue = issues.SingleOrDefault(i => i.Message.Contains("casing"));
        caseIssue.Should().NotBeNull();
        caseIssue!.AffectedFiles.Should().ContainSingle()
            .Which.Should().Contain("b.txt");
    }

    #endregion

    #region Position Inconsistency

    [Fact]
    public void WhenTriggerAtSamePositionInAllCaptionsThenNoPositionIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."),
            ("b.txt", "ohwx a man on a bench."),
            ("c.txt", "ohwx a child at play."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Severity == IssueSeverity.Info);
    }

    [Fact]
    public void WhenMostTriggerAtPosition0ButSomeElsewhereThenReportsInfo()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."),
            ("b.txt", "ohwx a man on a bench."),
            ("c.txt", "ohwx a child at play."),
            ("d.txt", "A photo of ohwx in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Info
            && i.Message.Contains("position"));
    }

    [Fact]
    public void WhenPositionInconsistentThenAffectedFilesAreMinority()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."),
            ("b.txt", "ohwx a man on a bench."),
            ("c.txt", "ohwx a child at play."),
            ("d.txt", "A photo of ohwx in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        var positionIssue = issues.Single(i => i.Message.Contains("position"));
        positionIssue.AffectedFiles.Should().ContainSingle()
            .Which.Should().Contain("d.txt");
    }

    [Fact]
    public void WhenBooruTriggerPositionVariesThenReportsInfo()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx, 1girl, brown hair", CaptionStyle.BooruTags),
            ("b.txt", "ohwx, solo, blue eyes", CaptionStyle.BooruTags),
            ("c.txt", "1girl, ohwx, standing", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Info
            && i.Message.Contains("position"));
    }

    #endregion

    #region Duplicate Trigger

    [Fact]
    public void WhenTriggerAppearsOnceThenNoDuplicateIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("more than once"));
    }

    [Fact]
    public void WhenTriggerAppearsTwiceThenReportsWarning()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman ohwx in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("more than once"));
    }

    [Fact]
    public void WhenDuplicateTriggerInBooruTagsThenReportsWarning()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx, 1girl, ohwx, brown hair", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("more than once"));
    }

    [Fact]
    public void WhenDuplicateWithMixedCaseThenAlsoCountsAsMultiple()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman OHWX in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("more than once"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WhenNoCaptionsThenReturnsEmpty()
    {
        var issues = _sut.Run([], MakeConfig());

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenTriggerWordIsNullThenReturnsEmpty()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman in a park."));

        var issues = _sut.Run(captions, MakeConfig(triggerWord: null));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenTriggerWordIsEmptyThenReturnsEmpty()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman in a park."));

        var issues = _sut.Run(captions, MakeConfig(triggerWord: ""));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenTriggerWordIsWhitespaceThenReturnsEmpty()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman in a park."));

        var issues = _sut.Run(captions, MakeConfig(triggerWord: "   "));

        issues.Should().BeEmpty();
    }

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
    public void WhenCaptionIsEmptyStringThenTriggerIsMissing()
    {
        var captions = MakeCaptions(
            ("a.txt", ""));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("missing"));
    }

    [Fact]
    public void WhenTriggerIsSubstringOfWordThenNotCountedAsPresent()
    {
        // "ohwx" should not match inside "xohwxyz"
        var captions = MakeCaptionsNl(
            ("a.txt", "xohwxyz a woman in a park."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("missing"));
    }

    [Fact]
    public void WhenAllCaptionsHaveTriggerCorrectlyThenNoIssues()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx a woman standing in a park."),
            ("b.txt", "ohwx a man on a bench reading."),
            ("c.txt", "ohwx a child playing in the sand."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    #endregion

    #region Tokenize Helper

    [Theory]
    [InlineData("hello world", CaptionStyle.NaturalLanguage, 2)]
    [InlineData("1girl, brown hair, blue eyes", CaptionStyle.BooruTags, 3)]
    [InlineData("", CaptionStyle.NaturalLanguage, 0)]
    [InlineData("   ", CaptionStyle.BooruTags, 0)]
    public void WhenTokenizingThenReturnsExpectedCount(
        string input, CaptionStyle style, int expectedCount)
    {
        TriggerWordCheck.Tokenize(input, style).Should().HaveCount(expectedCount);
    }

    [Fact]
    public void WhenTokenizingBooruTagsThenSplitsByComma()
    {
        var tokens = TriggerWordCheck.Tokenize("ohwx, 1girl, brown hair", CaptionStyle.BooruTags);

        tokens.Should().Equal("ohwx", "1girl", "brown hair");
    }

    [Fact]
    public void WhenTokenizingNlTextThenSplitsByWhitespace()
    {
        var tokens = TriggerWordCheck.Tokenize("ohwx a woman", CaptionStyle.NaturalLanguage);

        tokens.Should().Equal("ohwx", "a", "woman");
    }

    #endregion

    #region PrependTrigger Helper

    [Fact]
    public void WhenPrependToBooruThenCommaFormatting()
    {
        var result = TriggerWordCheck.PrependTrigger(
            "1girl, brown hair", "ohwx", CaptionStyle.BooruTags);

        result.Should().Be("ohwx, 1girl, brown hair");
    }

    [Fact]
    public void WhenPrependToNlThenSpaceFormatting()
    {
        var result = TriggerWordCheck.PrependTrigger(
            "A woman in a park.", "ohwx", CaptionStyle.NaturalLanguage);

        result.Should().Be("ohwx A woman in a park.");
    }

    [Fact]
    public void WhenPrependToEmptyThenReturnsTriggerOnly()
    {
        var result = TriggerWordCheck.PrependTrigger(
            "", "ohwx", CaptionStyle.NaturalLanguage);

        result.Should().Be("ohwx");
    }

    #endregion

    #region ReplaceCaseMismatch Helper

    [Fact]
    public void WhenReplacingCaseMismatchInNlThenCorrectsCasing()
    {
        var result = TriggerWordCheck.ReplaceCaseMismatch(
            "OHWX a woman in a park.", "ohwx", CaptionStyle.NaturalLanguage);

        result.Should().Be("ohwx a woman in a park.");
    }

    [Fact]
    public void WhenReplacingCaseMismatchInBooruThenCorrectsCasing()
    {
        var result = TriggerWordCheck.ReplaceCaseMismatch(
            "OHWX, 1girl, brown hair", "ohwx", CaptionStyle.BooruTags);

        result.Should().Be("ohwx, 1girl, brown hair");
    }

    [Fact]
    public void WhenReplacingMultipleMismatchesThenAllCorrected()
    {
        var result = TriggerWordCheck.ReplaceCaseMismatch(
            "Ohwx a woman OHWX in a park.", "ohwx", CaptionStyle.NaturalLanguage);

        result.Should().Be("ohwx a woman ohwx in a park.");
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

    private static List<CaptionFile> MakeCaptions(
        params (string FileName, string Text, CaptionStyle Style)[] entries)
    {
        return entries.Select(e => new CaptionFile
        {
            FilePath = Path.Combine(@"C:\fake\dataset", e.FileName),
            RawText = e.Text,
            DetectedStyle = e.Style
        }).ToList();
    }

    private static List<CaptionFile> MakeCaptionsNl(
        params (string FileName, string Text)[] entries)
    {
        return entries.Select(e => new CaptionFile
        {
            FilePath = Path.Combine(@"C:\fake\dataset", e.FileName),
            RawText = e.Text,
            DetectedStyle = CaptionStyle.NaturalLanguage
        }).ToList();
    }

    #endregion
}
