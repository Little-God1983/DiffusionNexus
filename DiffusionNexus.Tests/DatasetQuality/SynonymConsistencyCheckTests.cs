using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="SynonymConsistencyCheck"/>.
/// Tests use in-memory <see cref="CaptionFile"/> records — no disk I/O needed.
/// </summary>
public class SynonymConsistencyCheckTests
{
    private readonly SynonymConsistencyCheck _sut = new();

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
    public void WhenCheckMetadataInspectedThenOrderIsThree()
    {
        _sut.Order.Should().Be(3);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().NotBeNullOrWhiteSpace();
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region No Conflicts

    [Fact]
    public void WhenNoCaptionsThenReturnsEmpty()
    {
        var issues = _sut.Run([], MakeConfig());

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenCaptionsUseNoSynonymsThenReturnsEmpty()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A photo of a sunset over the mountains."),
            ("b.txt", "Bright colors fill the horizon."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenAllCaptionsUseSameSynonymThenNoConflict()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman standing in the park."),
            ("b.txt", "A woman sitting on a bench."),
            ("c.txt", "A woman reading a book."));

        var issues = _sut.Run(captions, MakeConfig());

        // "woman" is used everywhere — no synonym conflict in the people-female group
        issues.Should().NotContain(i =>
            i.Message.Contains("woman", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("vs"));
    }

    #endregion

    #region Synonym Conflicts Detected

    [Fact]
    public void WhenTwoSynonymsUsedThenReportsWarning()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman standing in the park."),
            ("b.txt", "A lady sitting on a bench."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("woman", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("lady", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenThreeSynonymsUsedThenAllAppearInMessage()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked on the street."),
            ("b.txt", "An automobile driving fast."),
            ("c.txt", "A vehicle on the highway."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("vs"));

        conflict.Message.Should().Contain("automobile");
        conflict.Message.Should().Contain("vehicle");
    }

    [Fact]
    public void WhenConflictDetectedThenAffectedFilesContainsAllInvolvedFiles()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked on the street."),
            ("b.txt", "An automobile driving fast."),
            ("c.txt", "A photo of the sky."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        conflict.AffectedFiles.Should().HaveCount(2);
        conflict.AffectedFiles.Should().Contain(f => f.Contains("a.txt"));
        conflict.AffectedFiles.Should().Contain(f => f.Contains("b.txt"));
    }

    [Fact]
    public void WhenConflictDetectedThenMostUsedTermIsListedFirst()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked on the street."),
            ("b.txt", "A car driving fast."),
            ("c.txt", "A car near the building."),
            ("d.txt", "An automobile on the road."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        // "car"(3x) should come before "automobile"(1x) in the message
        var carPos = conflict.Message.IndexOf("car", StringComparison.OrdinalIgnoreCase);
        var autoPos = conflict.Message.IndexOf("automobile", StringComparison.OrdinalIgnoreCase);
        carPos.Should().BeLessThan(autoPos);
    }

    [Fact]
    public void WhenBooruTagsHaveSynonymConflictThenDetected()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, car, blue sky", CaptionStyle.BooruTags),
            ("b.txt", "1girl, automobile, sunset", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenUsageCountsShownThenFormatIsCorrect()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "A car on the road."),
            ("c.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        conflict.Message.Should().Contain("\"car\"(2x)");
        conflict.Message.Should().Contain("\"automobile\"(1x)");
    }

    #endregion

    #region Fix Suggestions

    [Fact]
    public void WhenTwoSynonymsUsedThenTwoFixSuggestionsGenerated()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        conflict.FixSuggestions.Should().HaveCount(2);
    }

    [Fact]
    public void WhenFixTargetsCarThenAutomobileFileIsEdited()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        carFix.Edits.Should().ContainSingle();
        carFix.Edits[0].FilePath.Should().Contain("b.txt");
        carFix.Edits[0].NewText.Should().Contain("car");
        carFix.Edits[0].NewText.Should().NotContain("automobile");
    }

    [Fact]
    public void WhenFixTargetsAutomobileThenCarFileIsEdited()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        var autoFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"automobile\""));

        autoFix.Edits.Should().ContainSingle();
        autoFix.Edits[0].FilePath.Should().Contain("a.txt");
        autoFix.Edits[0].NewText.Should().Contain("automobile");
        autoFix.Edits[0].NewText.Should().NotContain("car");
    }

    [Fact]
    public void WhenThreeSynonymsUsedThenThreeFixSuggestions()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "An automobile nearby."),
            ("c.txt", "A vehicle on the road."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        conflict.FixSuggestions.Should().HaveCount(3);
    }

    [Fact]
    public void WhenFixAppliedToMajorityTermThenOnlyMinorityFilesHaveEdits()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "A car on the road."),
            ("c.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        // Only the automobile file needs to be edited
        carFix.Edits.Should().ContainSingle();
        carFix.Edits[0].FilePath.Should().Contain("c.txt");
    }

    [Fact]
    public void WhenBooruFixAppliedThenTagsAreReplacedCorrectly()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, car, blue sky", CaptionStyle.BooruTags),
            ("b.txt", "1girl, automobile, sunset", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        carFix.Edits.Should().ContainSingle();
        carFix.Edits[0].NewText.Should().Be("1girl, car, sunset");
    }

    [Fact]
    public void WhenFixEditGeneratedThenOriginalTextMatchesRawCaption()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked outside."),
            ("b.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase));

        foreach (var fix in conflict.FixSuggestions)
        {
            foreach (var edit in fix.Edits)
            {
                // OriginalText should match the raw text of the caption
                var matchingCaption = captions.Single(c => c.FilePath == edit.FilePath);
                edit.OriginalText.Should().Be(matchingCaption.RawText);
            }
        }
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
    public void WhenCaptionIsEmptyStringThenDoesNotThrow()
    {
        var captions = MakeCaptionsNl(("a.txt", ""), ("b.txt", ""));

        var act = () => _sut.Run(captions, MakeConfig());

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenSameTermAppearsInMultipleCaptionsThenCountedCorrectly()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car on the road."),
            ("b.txt", "A car in the lot."),
            ("c.txt", "A car near the house."),
            ("d.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        conflict.Message.Should().Contain("\"car\"(3x)");
        conflict.Message.Should().Contain("\"automobile\"(1x)");
    }

    [Fact]
    public void WhenMultipleGroupsConflictThenMultipleIssuesReported()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A woman sitting in a car."),
            ("b.txt", "A lady standing near an automobile."));

        var issues = _sut.Run(captions, MakeConfig());

        // Should have conflicts for both female-people group and vehicle group
        issues.Should().Contain(i =>
            i.Message.Contains("woman", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("lady", StringComparison.OrdinalIgnoreCase));
        issues.Should().Contain(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WhenOnlySingleTermFromGroupFoundThenNoConflict()
    {
        // All use "car" from the vehicle group, no synonym conflict
        var captions = MakeCaptionsNl(
            ("a.txt", "A car on the road."),
            ("b.txt", "A car in the driveway."));

        var issues = _sut.Run(captions, MakeConfig());

        issues.Should().NotContain(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("vs"));
    }

    #endregion

    #region ReplaceTerm Helper

    [Fact]
    public void WhenReplacingInNlTextThenUsesWordBoundary()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "A car on the road.", "car", "automobile", CaptionStyle.NaturalLanguage);

        result.Should().Be("An automobile on the road.");
    }

    [Fact]
    public void WhenReplacingInBooruTagsThenReplacesExactTag()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "1girl, car, blue sky", "car", "automobile", CaptionStyle.BooruTags);

        result.Should().Be("1girl, automobile, blue sky");
    }

    [Fact]
    public void WhenReplacingEmptyStringThenReturnsNewTerm()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "", "car", "automobile", CaptionStyle.NaturalLanguage);

        result.Should().Be("automobile");
    }

    [Fact]
    public void WhenReplacingInNlTextThenReplacesInPlace()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "A bustling city alley at night.", "city", "urban", CaptionStyle.NaturalLanguage);

        result.Should().Be("A bustling urban alley at night.");
    }

    [Fact]
    public void WhenReplacingWordPrecededByAnThenCorrectsToa()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "She wore an elegant dress.", "elegant", "beautiful", CaptionStyle.NaturalLanguage);

        result.Should().Be("She wore a beautiful dress.");
    }

    [Fact]
    public void WhenReplacingWordPrecededByAThenCorrectsToAn()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "He held a beautiful ornament.", "beautiful", "elegant", CaptionStyle.NaturalLanguage);

        result.Should().Be("He held an elegant ornament.");
    }

    [Fact]
    public void WhenReplacingWordPrecededByTheThenArticleIsUnchanged()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "The car was fast.", "car", "automobile", CaptionStyle.NaturalLanguage);

        result.Should().Contain("automobile");
        result.Should().NotContain("car");
        result.Should().StartWith("The automobile");
    }

    [Fact]
    public void WhenReplacingCapitalisedWordThenPreservesLeadingCase()
    {
        var result = SynonymConsistencyCheck.ReplaceTerm(
            "City lights at dusk.", "City", "urban", CaptionStyle.NaturalLanguage);

        result.Should().Be("Urban lights at dusk.");
    }

    #endregion

    #region Multi-Synonym Captions

    [Fact]
    public void WhenNlCaptionContainsTwoSynonymsThenEachSuggestionProducesOneEditPerFile()
    {
        // "car" and "automobile" both appear in the same caption
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked near an automobile."),
            ("b.txt", "A car on the road."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        // "Replace all with car" — a.txt has "automobile" to remove, b.txt already uses "car"
        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        carFix.Edits.Should().ContainSingle("a.txt already has 'car', just remove 'automobile'");
        carFix.Edits[0].FilePath.Should().Contain("a.txt");
        carFix.Edits[0].NewText.Should().NotContain("automobile");

        // "Replace all with automobile" — a.txt has "car" to remove, b.txt needs car→automobile
        var autoFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"automobile\""));

        autoFix.Edits.Should().HaveCount(2);
        autoFix.Edits.Select(e => e.FilePath).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void WhenNlCaptionContainsTargetAlreadyThenTargetIsNotDuplicated()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car parked near an automobile."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase));

        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        // Should remove "automobile" without appending a second "car"
        var newText = carFix.Edits.Single().NewText;
        var carCount = System.Text.RegularExpressions.Regex.Matches(
            newText, @"\bcar\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count;

        carCount.Should().Be(1, "the target term should appear only once");
    }

    [Fact]
    public void WhenBooruCaptionContainsTwoSynonymTagsThenDeduplicatedInEdit()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, car, automobile, blue sky", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("automobile", StringComparison.OrdinalIgnoreCase));

        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        carFix.Edits.Should().ContainSingle();
        carFix.Edits[0].NewText.Should().Be("1girl, car, blue sky");
    }

    [Fact]
    public void WhenThreeWayConflictAndCaptionHasTwoSynonymsThenOneEditPerFile()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "A car near a vehicle."),
            ("b.txt", "An automobile nearby."));

        var issues = _sut.Run(captions, MakeConfig());

        var conflict = issues.Single(i =>
            i.Message.Contains("car", StringComparison.OrdinalIgnoreCase)
            && i.Message.Contains("vehicle", StringComparison.OrdinalIgnoreCase));

        // "Replace all with car" — a.txt has "car" + "vehicle", b.txt has "automobile"
        var carFix = conflict.FixSuggestions
            .Single(f => f.Description.Contains("\"car\""));

        // a.txt: remove "vehicle" (already has "car"), b.txt: replace "automobile"→"car" = 2 edits
        carFix.Edits.Should().HaveCount(2);
        carFix.Edits.Select(e => e.FilePath).Should().OnlyHaveUniqueItems();

        // Verify a.txt edit removes "vehicle" but keeps one "car"
        var aEdit = carFix.Edits.Single(e => e.FilePath.Contains("a.txt"));
        aEdit.NewText.Should().NotContain("vehicle");
    }

    #endregion

    #region AnalyzeGroup Helper

    [Fact]
    public void WhenGroupHasNoMatchesThenUsedTermsIsEmpty()
    {
        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "xyznonexistent", "abcnonexistent" };
        var captions = MakeCaptionsNl(("a.txt", "A photo of the sky."));

        var conflict = SynonymConsistencyCheck.AnalyzeGroup(group, captions);

        conflict.UsedTerms.Should().BeEmpty();
    }

    [Fact]
    public void WhenGroupHasOneMatchThenUsedTermsHasOneEntry()
    {
        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "car", "automobile" };
        var captions = MakeCaptionsNl(("a.txt", "A car on the road."));

        var conflict = SynonymConsistencyCheck.AnalyzeGroup(group, captions);

        conflict.UsedTerms.Should().ContainSingle().Which.Should().Be("car");
    }

    [Fact]
    public void WhenGroupHasTwoMatchesThenUsedTermsSortedByCount()
    {
        var group = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "car", "automobile", "vehicle" };
        var captions = MakeCaptionsNl(
            ("a.txt", "A car on the road."),
            ("b.txt", "A car near the house."),
            ("c.txt", "An automobile nearby."));

        var conflict = SynonymConsistencyCheck.AnalyzeGroup(group, captions);

        conflict.UsedTerms.Should().HaveCount(2);
        conflict.UsedTerms[0].Should().Be("car");
        conflict.UsedTerms[1].Should().Be("automobile");
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
