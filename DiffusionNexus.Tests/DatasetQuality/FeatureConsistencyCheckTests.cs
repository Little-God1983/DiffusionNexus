using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="FeatureConsistencyCheck"/>.
/// Tests use in-memory <see cref="CaptionFile"/> records — no disk I/O needed.
/// </summary>
public class FeatureConsistencyCheckTests
{
    private readonly FeatureConsistencyCheck _sut = new();

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
    public void WhenCheckMetadataInspectedThenOrderIsFour()
    {
        _sut.Order.Should().Be(4);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().NotBeNullOrWhiteSpace();
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Known Features — Near-Constant (80-99%)

    [Fact]
    public void WhenKnownFeatureIn80PercentThenReportsCritical()
    {
        // "blue eyes" in 4/5 captions = 80%
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes")
            && i.Message.Contains("4/5"));
    }

    [Fact]
    public void WhenKnownFeatureIn90PercentThenReportsCritical()
    {
        // "brown hair" in 9/10 captions = 90%
        var captions = MakeCaptionsNl(
            ("01.txt", "ohwx a woman with brown hair standing."),
            ("02.txt", "ohwx a woman with brown hair sitting."),
            ("03.txt", "ohwx a woman with brown hair walking."),
            ("04.txt", "ohwx a woman with brown hair running."),
            ("05.txt", "ohwx a woman with brown hair reading."),
            ("06.txt", "ohwx a woman with brown hair smiling."),
            ("07.txt", "ohwx a woman with brown hair laughing."),
            ("08.txt", "ohwx a woman with brown hair posing."),
            ("09.txt", "ohwx a woman with brown hair waving."),
            ("10.txt", "ohwx a woman in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("brown hair")
            && i.Message.Contains("9/10"));
    }

    [Fact]
    public void WhenNearConstantThenFixSuggestionToAddToMissing()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes"));

        var addFix = issue.FixSuggestions.SingleOrDefault(f =>
            f.Description.Contains("Add"));

        addFix.Should().NotBeNull();
        addFix!.Edits.Should().ContainSingle();
        addFix.Edits[0].FilePath.Should().Contain("e.txt");
        addFix.Edits[0].NewText.Should().Contain("blue eyes");
    }

    [Fact]
    public void WhenNearConstantThenFixSuggestionToRemoveFromAll()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes"));

        var removeFix = issue.FixSuggestions.SingleOrDefault(f =>
            f.Description.Contains("Remove"));

        removeFix.Should().NotBeNull();
        removeFix!.Edits.Should().HaveCount(4);
        removeFix.Edits.Should().OnlyContain(e => !e.NewText.Contains("blue eyes"));
    }

    [Fact]
    public void WhenNearConstantThenAffectedFilesAreMissing()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes"));

        // Affected files = the ones MISSING the feature
        issue.AffectedFiles.Should().ContainSingle()
            .Which.Should().Contain("e.txt");
    }

    [Fact]
    public void WhenNearConstantThenMessageShowsCategory()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes"));

        issue.Message.Should().Contain("eye_color");
    }

    [Fact]
    public void WhenBooruNearConstantThenDetectedCorrectly()
    {
        var captions = MakeCaptions(
            ("a.txt", "ohwx, 1girl, blue eyes, park", CaptionStyle.BooruTags),
            ("b.txt", "ohwx, 1girl, blue eyes, beach", CaptionStyle.BooruTags),
            ("c.txt", "ohwx, 1girl, blue eyes, garden", CaptionStyle.BooruTags),
            ("d.txt", "ohwx, 1girl, blue eyes, lake", CaptionStyle.BooruTags),
            ("e.txt", "ohwx, 1girl, sitting", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("blue eyes"));
    }

    #endregion

    #region Known Features — Fully Covered (100%)

    [Fact]
    public void WhenKnownFeatureIn100PercentThenReportsInfo()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Info
            && i.Message.Contains("blue eyes")
            && i.Message.Contains("3/3"));
    }

    [Fact]
    public void WhenFullyCoveredThenFixSuggestionToRemove()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Info
            && i.Message.Contains("blue eyes"));

        issue.FixSuggestions.Should().ContainSingle();
        issue.FixSuggestions[0].Description.Should().Contain("Remove");
        issue.FixSuggestions[0].Edits.Should().HaveCount(3);
    }

    [Fact]
    public void WhenFullyCoveredThenMessageSuggestsBaking()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        var issue = issues.Single(i =>
            i.Severity == IssueSeverity.Info
            && i.Message.Contains("blue eyes"));

        issue.Message.Should().Contain("bake into trigger");
    }

    #endregion

    #region Known Features — Not Flagged

    [Fact]
    public void WhenFeatureInLessThan80PercentThenNoIssue()
    {
        // "blue eyes" in 2/5 = 40%
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman in a garden."),
            ("d.txt", "ohwx a woman near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i =>
            i.Message.Contains("blue eyes")
            && i.Severity == IssueSeverity.Critical);
    }

    [Fact]
    public void WhenFeatureInZeroPercentThenNoIssue()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman in a park."),
            ("b.txt", "ohwx a woman at the beach."),
            ("c.txt", "ohwx a woman in a garden."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i =>
            i.Message.Contains("blue eyes"));
    }

    #endregion

    #region Discovered N-grams (Sub-analysis B)

    [Fact]
    public void WhenDiscoveredNgramIn80PercentThenReportsWarning()
    {
        // The bigram "iron gate" appears in 4/5 = 80% and is not a known feature
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx near an iron gate on a cobblestone path."),
            ("b.txt", "ohwx beside an iron gate under the sky."),
            ("c.txt", "ohwx through an iron gate in an alley."),
            ("d.txt", "ohwx leaning on an iron gate at dusk."),
            ("e.txt", "ohwx standing near a tree."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("iron gate")
            && i.Message.Contains("4/5"));
    }

    [Fact]
    public void WhenNgramIsAlsoKnownFeatureThenNotDuplicateWarning()
    {
        // "blue eyes" is a known feature and also a bigram — should only flag via known features
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx a woman with blue eyes in a park."),
            ("b.txt", "ohwx a woman with blue eyes at the beach."),
            ("c.txt", "ohwx a woman with blue eyes in a garden."),
            ("d.txt", "ohwx a woman with blue eyes near a lake."),
            ("e.txt", "ohwx a woman sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        // Should have exactly one Critical for "blue eyes" (known), not an additional Warning
        var blueEyesIssues = issues.Where(i => i.Message.Contains("blue eyes")).ToList();
        blueEyesIssues.Should().ContainSingle();
        blueEyesIssues[0].Severity.Should().Be(IssueSeverity.Critical);
    }

    [Fact]
    public void WhenNgramInLessThan80PercentThenNoWarning()
    {
        // "iron gate" in 2/5 = 40%
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx near an iron gate on a path."),
            ("b.txt", "ohwx beside an iron gate under the sky."),
            ("c.txt", "ohwx in an alley."),
            ("d.txt", "ohwx at the harbor."),
            ("e.txt", "ohwx standing near a tree."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i =>
            i.Message.Contains("iron gate")
            && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void WhenNgramIn100PercentThenNotFlaggedAsWarning()
    {
        // If an n-gram is in all captions, it's not in the 80-99% danger zone
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx near an iron gate on a path."),
            ("b.txt", "ohwx beside an iron gate under the sky."),
            ("c.txt", "ohwx through an iron gate in an alley."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i =>
            i.Message.Contains("iron gate")
            && i.Severity == IssueSeverity.Warning);
    }

    [Fact]
    public void WhenDiscoveredNgramThenAffectedFilesAreMissingOnes()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx near an iron gate on a cobblestone path."),
            ("b.txt", "ohwx beside an iron gate under the sky."),
            ("c.txt", "ohwx through an iron gate in an alley."),
            ("d.txt", "ohwx leaning on an iron gate at dusk."),
            ("e.txt", "ohwx standing near a tree."));

        var issues = _sut.Run(captions, MakeConfig());

        var ngramIssue = issues.SingleOrDefault(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("iron gate"));

        ngramIssue.Should().NotBeNull();
        ngramIssue!.AffectedFiles.Should().ContainSingle()
            .Which.Should().Contain("e.txt");
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
    public void WhenFewerThanMinCaptionsThenReturnsEmpty()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx with blue eyes."),
            ("b.txt", "ohwx with blue eyes."));

        var issues = _sut.Run(captions, MakeConfig());

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
    public void WhenCaptionsAreEmptyStringsThenDoesNotThrow()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", ""),
            ("b.txt", ""),
            ("c.txt", ""));

        var act = () => _sut.Run(captions, MakeConfig());

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenMultipleKnownFeaturesNearConstantThenMultipleIssues()
    {
        // "blue eyes" in 4/5, "brown hair" in 4/5
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx with blue eyes and brown hair."),
            ("b.txt", "ohwx with blue eyes and brown hair."),
            ("c.txt", "ohwx with blue eyes and brown hair."),
            ("d.txt", "ohwx with blue eyes and brown hair."),
            ("e.txt", "ohwx sitting quietly."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("blue eyes"));
        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("brown hair"));
    }

    #endregion

    #region FindCaptionsContaining / FindCaptionsMissing Helpers

    [Fact]
    public void WhenFindingContainingThenReturnsCorrectIndices()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman with blue eyes."),
            ("b.txt", "A woman in a park."),
            ("c.txt", "A woman with blue eyes reading."));

        var indices = FeatureConsistencyCheck.FindCaptionsContaining(captions, "blue eyes");

        indices.Should().Equal(0, 2);
    }

    [Fact]
    public void WhenFindingMissingThenReturnsCorrectIndices()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman with blue eyes."),
            ("b.txt", "A woman in a park."),
            ("c.txt", "A woman with blue eyes reading."));

        var indices = FeatureConsistencyCheck.FindCaptionsMissing(captions, "blue eyes");

        indices.Should().Equal(1);
    }

    [Fact]
    public void WhenEmptyCaptionThenCountedAsMissing()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman with blue eyes."),
            ("b.txt", ""),
            ("c.txt", "A woman with blue eyes reading."));

        var missing = FeatureConsistencyCheck.FindCaptionsMissing(captions, "blue eyes");

        missing.Should().Contain(1);
    }

    [Fact]
    public void WhenBooruTagSearchThenMatchesExactTag()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, blue eyes, park", CaptionStyle.BooruTags),
            ("b.txt", "1girl, sitting", CaptionStyle.BooruTags));

        var indices = FeatureConsistencyCheck.FindCaptionsContaining(captions, "blue eyes");

        indices.Should().Equal(0);
    }

    #endregion

    #region Helpers

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
