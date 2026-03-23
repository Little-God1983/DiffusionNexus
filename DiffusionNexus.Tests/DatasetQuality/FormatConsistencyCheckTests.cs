using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="FormatConsistencyCheck"/>.
/// Tests use in-memory <see cref="CaptionFile"/> records — no disk I/O needed.
/// </summary>
public class FormatConsistencyCheckTests
{
    private readonly FormatConsistencyCheck _sut = new();

    private static DatasetConfig MakeConfig(LoraType type = LoraType.Character) => new()
    {
        FolderPath = @"C:\fake\dataset",
        TriggerWord = "ohwx",
        LoraType = type
    };

    #region Applicability

    [Theory]
    [InlineData(LoraType.Character)]
    [InlineData(LoraType.Concept)]
    [InlineData(LoraType.Style)]
    public void WhenAnyLoraTypeThenIsApplicable(LoraType type)
    {
        _sut.IsApplicable(type).Should().BeTrue();
    }

    [Fact]
    public void WhenCheckMetadataInspectedThenOrderIsOne()
    {
        _sut.Order.Should().Be(1);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().NotBeNullOrWhiteSpace();
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Empty / Near-Empty Captions

    [Fact]
    public void WhenCaptionIsEmptyThenReportsCritical()
    {
        var captions = MakeCaptions(("img1.txt", ""));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("empty"));
    }

    [Fact]
    public void WhenCaptionIsWhitespaceOnlyThenReportsCritical()
    {
        var captions = MakeCaptions(("img1.txt", "   \t  "));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("empty"));
    }

    [Fact]
    public void WhenCaptionHasOneWordThenReportsCriticalNearEmpty()
    {
        var captions = MakeCaptions(("img1.txt", "woman"));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains($"≤{FormatConsistencyCheck.NearEmptyWordThreshold}"));
    }

    [Fact]
    public void WhenCaptionHasTwoWordsThenReportsCriticalNearEmpty()
    {
        var captions = MakeCaptions(("img1.txt", "brown hair"));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains($"≤{FormatConsistencyCheck.NearEmptyWordThreshold}"));
    }

    [Fact]
    public void WhenCaptionHasThreeWordsThenNoEmptyIssue()
    {
        // 3 words is above the threshold — should not trigger empty/near-empty
        var captions = MakeCaptions(("img1.txt", "a brown haired"));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Severity == IssueSeverity.Critical);
    }

    [Fact]
    public void WhenMultipleEmptyCaptionsExistThenSingleIssueWithAllFiles()
    {
        var captions = MakeCaptions(
            ("a.txt", ""),
            ("b.txt", ""),
            ("c.txt", "a woman with brown hair standing in a park, smiling."));

        var issues = _sut.Run(captions, MakeConfig());

        var emptyIssue = issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("empty")).Subject;

        emptyIssue.AffectedFiles.Should().HaveCount(2);
    }

    #endregion

    #region Mixed Styles

    [Fact]
    public void WhenAllCaptionsAreNaturalLanguageThenNoMixedStyleIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park.", CaptionStyle.NaturalLanguage),
            ("b.txt", "A man sitting on a bench.", CaptionStyle.NaturalLanguage));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("Mixed caption styles"));
    }

    [Fact]
    public void WhenAllCaptionsAreBooruTagsThenNoMixedStyleIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, brown hair, blue eyes", CaptionStyle.BooruTags),
            ("b.txt", "1boy, black hair, red eyes", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("Mixed caption styles"));
    }

    [Fact]
    public void WhenCaptionsAreMixedStyleThenReportsWarning()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park.", CaptionStyle.NaturalLanguage),
            ("b.txt", "1girl, brown hair, standing", CaptionStyle.BooruTags),
            ("c.txt", "A man sitting on a bench reading a book.", CaptionStyle.NaturalLanguage));

        var issues = _sut.Run(captions, MakeConfig());

        var mixedIssue = issues.Should().ContainSingle(i =>
            i.Message.Contains("Mixed caption styles")).Subject;

        mixedIssue.Severity.Should().Be(IssueSeverity.Warning);
        mixedIssue.Message.Should().Contain("2 natural language");
        mixedIssue.Message.Should().Contain("1 booru tags");
    }

    [Fact]
    public void WhenMixedStyleDetectedThenAffectedFilesAreMinority()
    {
        // 3 NL + 1 booru → minority is booru
        var captions = MakeCaptions(
            ("a.txt", "Sentence one.", CaptionStyle.NaturalLanguage),
            ("b.txt", "Sentence two.", CaptionStyle.NaturalLanguage),
            ("c.txt", "Sentence three.", CaptionStyle.NaturalLanguage),
            ("d.txt", "1girl, solo", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        var mixedIssue = issues.First(i => i.Message.Contains("Mixed caption styles"));
        mixedIssue.AffectedFiles.Should().HaveCount(1);
        mixedIssue.AffectedFiles[0].Should().Contain("d.txt");
    }

    [Fact]
    public void WhenUnknownAndMixedStylesExistThenTheyAreIgnoredForMixedCheck()
    {
        // Only one definitive style (NL), plus Unknown/Mixed → no mixed-style issue
        var captions = MakeCaptions(
            ("a.txt", "A sentence.", CaptionStyle.NaturalLanguage),
            ("b.txt", "short", CaptionStyle.Unknown),
            ("c.txt", "mixed stuff, sentence.", CaptionStyle.Mixed));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("Mixed caption styles"));
    }

    #endregion

    #region Length Outliers

    [Fact]
    public void WhenAllCaptionsAreSimilarLengthThenNoOutlierIssue()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a sunlit park wearing a blue dress."),
            ("b.txt", "A man sitting on a wooden bench reading a small book."),
            ("c.txt", "A child playing with a golden retriever on green grass."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("unusual length"));
    }

    [Fact]
    public void WhenOneCaptionIsMuchLongerThenReportsWarning()
    {
        var normalCaption = "A woman standing in a park.";
        var longCaption = string.Join(" ", Enumerable.Repeat("word", 100));

        // Use enough normal captions so the single outlier doesn't skew
        // the mean/stddev enough to mask itself at the 2σ boundary.
        var captions = MakeCaptions(
            ("a.txt", normalCaption),
            ("b.txt", normalCaption),
            ("c.txt", normalCaption),
            ("d.txt", normalCaption),
            ("e.txt", normalCaption),
            ("f.txt", normalCaption),
            ("g.txt", normalCaption),
            ("h.txt", normalCaption),
            ("i.txt", longCaption));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().ContainSingle(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("unusual length"));
    }

    [Fact]
    public void WhenFewerThanThreeCaptionsThenNoOutlierCheck()
    {
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park."),
            ("b.txt", string.Join(" ", Enumerable.Repeat("word", 200))));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i => i.Message.Contains("unusual length"));
    }

    [Fact]
    public void WhenOutlierIsAlsoNearEmptyThenItIsNotDoubleFlagged()
    {
        // One near-empty caption among normal ones — should be flagged as
        // near-empty (Critical) but NOT as a length outlier (Warning)
        var captions = MakeCaptions(
            ("a.txt", "A woman standing in a park wearing a blue dress and smiling at the camera."),
            ("b.txt", "A man sitting on a bench reading a book in the afternoon sunshine."),
            ("c.txt", "A child running through a meadow with a golden retriever by their side."),
            ("d.txt", "hi"));

        var issues = _sut.Run(captions, MakeConfig());

        var criticalIssues = issues.Where(i => i.Severity == IssueSeverity.Critical).ToList();
        criticalIssues.Should().NotBeEmpty();

        // "hi" should NOT appear as an outlier warning
        var outlierIssues = issues.Where(i => i.Message.Contains("unusual length")).ToList();
        foreach (var outlier in outlierIssues)
        {
            outlier.AffectedFiles.Should().NotContain(f => f.Contains("d.txt"));
        }
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

    #endregion

    #region CountWords

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("word", 1)]
    [InlineData("two words", 2)]
    [InlineData("  extra   spaces   here  ", 3)]
    [InlineData("1girl, brown hair, blue eyes", 5)]
    public void WhenCountingWordsThenReturnsCorrectCount(string input, int expected)
    {
        FormatConsistencyCheck.CountWords(input).Should().Be(expected);
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

    #endregion
}
