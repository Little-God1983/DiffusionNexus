using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.Checks;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="TypeSpecificCheck"/>.
/// Tests use in-memory <see cref="CaptionFile"/> records — no disk I/O needed.
/// </summary>
public class TypeSpecificCheckTests
{
    private readonly TypeSpecificCheck _sut = new();

    private static DatasetConfig MakeConfig(LoraType type) => new()
    {
        FolderPath = @"C:\fake\dataset",
        TriggerWord = "ohwx",
        LoraType = type
    };

    #region Metadata

    [Theory]
    [InlineData(LoraType.Character)]
    [InlineData(LoraType.Concept)]
    [InlineData(LoraType.Style)]
    public void WhenAnyLoraTypeThenIsApplicable(LoraType type)
    {
        _sut.IsApplicable(type).Should().BeTrue();
    }

    [Fact]
    public void WhenCheckMetadataInspectedThenOrderIsFive()
    {
        _sut.Order.Should().Be(5);
        _sut.Domain.Should().Be(CheckDomain.Caption);
        _sut.Name.Should().NotBeNullOrWhiteSpace();
        _sut.Description.Should().NotBeNullOrWhiteSpace();
    }

    #endregion

    #region Guard Clauses

    [Fact]
    public void WhenCaptionsIsNullThenThrowsArgumentNullException()
    {
        var act = () => _sut.Run(null!, MakeConfig(LoraType.Character));

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenConfigIsNullThenThrowsArgumentNullException()
    {
        var act = () => _sut.Run([], null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenFewerThanMinCaptionsThenReturnsEmpty()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx standing in a shirt."),
            ("b.txt", "ohwx standing in a shirt."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().BeEmpty();
    }

    #endregion

    #region Character — Clothing Diversity

    [Fact]
    public void WhenCharacterHasSameClothingThenWarns()
    {
        // Every caption mentions "shirt" and nothing else for clothing
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx wearing a shirt in a park."),
            ("b.txt", "ohwx in a shirt at the beach."),
            ("c.txt", "ohwx with a shirt indoors."),
            ("d.txt", "ohwx wearing a shirt at a cafe."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("clothing")
            && i.Message.Contains("shirt"));
    }

    [Fact]
    public void WhenCharacterHasVariedClothingThenNoWarning()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx wearing a shirt in a park."),
            ("b.txt", "ohwx in a dress at the beach."),
            ("c.txt", "ohwx with a hoodie indoors."),
            ("d.txt", "ohwx wearing a jacket at a cafe."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("clothing"));
    }

    [Fact]
    public void WhenCharacterClothingWarningThenAffectedFilesIncludeAll()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx wearing a shirt in a park."),
            ("b.txt", "ohwx in a shirt at the beach."),
            ("c.txt", "ohwx with a shirt indoors."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        var clothingIssue = issues.FirstOrDefault(i => i.Message.Contains("clothing"));
        clothingIssue.Should().NotBeNull();
        clothingIssue!.AffectedFiles.Should().HaveCount(3);
    }

    [Fact]
    public void WhenCharacterHasTwoClothingItemsThenNoWarning()
    {
        // Two different clothing terms → diversity exists
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx wearing a shirt in a park."),
            ("b.txt", "ohwx wearing a shirt at the beach."),
            ("c.txt", "ohwx in a dress indoors."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("clothing"));
    }

    [Fact]
    public void WhenCharacterBooruSameClothingThenWarns()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, shirt, park", CaptionStyle.BooruTags),
            ("b.txt", "1girl, shirt, beach", CaptionStyle.BooruTags),
            ("c.txt", "1girl, shirt, indoors", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("clothing"));
    }

    #endregion

    #region Character — Pose Diversity

    [Fact]
    public void WhenCharacterHasSamePoseThenWarns()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx standing in a park."),
            ("b.txt", "ohwx standing at the beach."),
            ("c.txt", "ohwx standing indoors."),
            ("d.txt", "ohwx standing at a cafe."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("pose")
            && i.Message.Contains("standing"));
    }

    [Fact]
    public void WhenCharacterHasVariedPosesThenNoWarning()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx standing in a park."),
            ("b.txt", "ohwx sitting at the beach."),
            ("c.txt", "ohwx kneeling indoors."),
            ("d.txt", "ohwx walking at a cafe."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("pose"));
    }

    [Fact]
    public void WhenCharacterHasBothClothingAndPoseIssuesThenBothWarned()
    {
        // Same shirt, same standing pose
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx standing in a shirt at the park."),
            ("b.txt", "ohwx standing in a shirt at the beach."),
            ("c.txt", "ohwx standing in a shirt indoors."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().Contain(i => i.Message.Contains("clothing"));
        issues.Should().Contain(i => i.Message.Contains("pose"));
    }

    [Fact]
    public void WhenCharacterHasNoPoseTermsThenNoWarning()
    {
        // No pose terms found at all — nothing to flag
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx with blue eyes in a park."),
            ("b.txt", "ohwx with blue eyes at the beach."),
            ("c.txt", "ohwx with blue eyes indoors."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("pose"));
    }

    #endregion

    #region Concept — Viewpoint Diversity

    [Fact]
    public void WhenConceptHasSameAngleThenWarns()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a magic circle with a close-up shot."),
            ("b.txt", "a magic circle in close-up framing."),
            ("c.txt", "a close-up of the magic circle."),
            ("d.txt", "a magic circle close-up on stone."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Concept));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("viewpoint/angle"));
    }

    [Fact]
    public void WhenConceptHasVariedAnglesThenNoWarning()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a magic circle in close-up framing."),
            ("b.txt", "a magic circle in a wide shot."),
            ("c.txt", "a magic circle from low angle."),
            ("d.txt", "a magic circle with medium shot."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Concept));

        issues.Should().NotContain(i =>
            i.Message.Contains("viewpoint/angle"));
    }

    [Fact]
    public void WhenConceptHasNoAngleTermsThenNoWarning()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a magic circle in a forest."),
            ("b.txt", "a magic circle on the ground."),
            ("c.txt", "a magic circle at the temple."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Concept));

        issues.Should().NotContain(i =>
            i.Message.Contains("viewpoint/angle"));
    }

    [Fact]
    public void WhenConceptDoesNotCheckClothingThenNoClothingIssue()
    {
        // Same clothing but Concept type — should not flag clothing
        var captions = MakeCaptionsNl(
            ("a.txt", "a magic circle with a close-up shot and a shirt."),
            ("b.txt", "a magic circle in close-up framing and a shirt."),
            ("c.txt", "a close-up of the magic circle and a shirt."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Concept));

        issues.Should().NotContain(i =>
            i.Message.Contains("clothing"));
    }

    #endregion

    #region Style — Style-Leak Words

    [Fact]
    public void WhenStyleHasLeakWordThenCritical()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat sitting on a masterpiece canvas."),
            ("b.txt", "a dog running through a field."),
            ("c.txt", "a bird perched on a masterpiece painting."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("masterpiece")
            && i.Message.Contains("Style-leak"));
    }

    [Fact]
    public void WhenStyleHasLeakWordThenFixSuggestionRemoves()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat with masterpiece quality."),
            ("b.txt", "a dog running through a field."),
            ("c.txt", "a bird with masterpiece detail."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        var leakIssue = issues.FirstOrDefault(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("masterpiece"));

        leakIssue.Should().NotBeNull();
        leakIssue!.FixSuggestions.Should().ContainSingle();
        leakIssue.FixSuggestions[0].Edits.Should().HaveCount(2);
        leakIssue.FixSuggestions[0].Description.Should().Contain("Remove");
    }

    [Fact]
    public void WhenStyleHasMultipleLeakWordsThenMultipleCriticals()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a masterpiece illustration of a cat."),
            ("b.txt", "a masterpiece illustration of a dog."),
            ("c.txt", "a masterpiece illustration of a bird."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("masterpiece"));
        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical && i.Message.Contains("illustration"));
    }

    [Fact]
    public void WhenStyleHasNoLeakWordsThenNoCritical()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat sitting on a windowsill."),
            ("b.txt", "a dog running through a field."),
            ("c.txt", "a bird perched on a branch."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().NotContain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("Style-leak"));
    }

    [Fact]
    public void WhenStyleBooruHasLeakTagThenCritical()
    {
        var captions = MakeCaptions(
            ("a.txt", "1girl, cat ears, masterpiece, best quality", CaptionStyle.BooruTags),
            ("b.txt", "1boy, dog ears, masterpiece, high quality", CaptionStyle.BooruTags),
            ("c.txt", "landscape, mountains, masterpiece", CaptionStyle.BooruTags));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Critical
            && i.Message.Contains("masterpiece"));
    }

    [Fact]
    public void WhenStyleLeakWordThenAffectedFilesCorrect()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a watercolor of a cat."),
            ("b.txt", "a dog running through a field."),
            ("c.txt", "a watercolor of a bird."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        var wcIssue = issues.FirstOrDefault(i =>
            i.Message.Contains("watercolor"));

        wcIssue.Should().NotBeNull();
        wcIssue!.AffectedFiles.Should().HaveCount(2);
        wcIssue.AffectedFiles.Should().Contain(f => f.Contains("a.txt"));
        wcIssue.AffectedFiles.Should().Contain(f => f.Contains("c.txt"));
    }

    #endregion

    #region Style — Content Diversity

    [Fact]
    public void WhenStyleHasLowContentDiversityThenWarns()
    {
        // All captions describe essentially the same content
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat sitting on a table."),
            ("b.txt", "a cat sitting on a table."),
            ("c.txt", "a cat sitting on a table."),
            ("d.txt", "a cat sitting on a table."),
            ("e.txt", "a cat sitting on a table."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().Contain(i =>
            i.Severity == IssueSeverity.Warning
            && i.Message.Contains("content diversity"));
    }

    [Fact]
    public void WhenStyleHasHighContentDiversityThenNoWarning()
    {
        // All captions describe different things
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat sitting on a windowsill at sunrise."),
            ("b.txt", "a dog running through a green meadow."),
            ("c.txt", "a bird perched on a tall oak branch."),
            ("d.txt", "a mountain landscape with snow and clouds."),
            ("e.txt", "a city street with neon lights at night."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().NotContain(i =>
            i.Message.Contains("content diversity"));
    }

    [Fact]
    public void WhenStyleContentDiversityWarningThenDetailsExplainVariety()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat sitting on a table."),
            ("b.txt", "a cat sitting on a table."),
            ("c.txt", "a cat sitting on a table."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        var diversityIssue = issues.FirstOrDefault(i =>
            i.Message.Contains("content diversity"));

        diversityIssue.Should().NotBeNull();
        diversityIssue!.Details.Should().Contain("varied subjects");
    }

    #endregion

    #region Cross-Type Isolation

    [Fact]
    public void WhenCharacterTypeThenNoStyleLeakCheck()
    {
        // Style-leak words present but type is Character — should not flag
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx in a masterpiece photo standing."),
            ("b.txt", "ohwx in a masterpiece photo sitting."),
            ("c.txt", "ohwx in a masterpiece photo kneeling."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("Style-leak"));
    }

    [Fact]
    public void WhenConceptTypeThenNoStyleLeakCheck()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a masterpiece magic circle."),
            ("b.txt", "a masterpiece magic circle."),
            ("c.txt", "a masterpiece magic circle."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Concept));

        issues.Should().NotContain(i =>
            i.Message.Contains("Style-leak"));
    }

    [Fact]
    public void WhenStyleTypeThenNoPoseDiversityCheck()
    {
        // Same pose but Style type — should not check pose
        var captions = MakeCaptionsNl(
            ("a.txt", "a cat standing on a table."),
            ("b.txt", "a dog standing at the beach."),
            ("c.txt", "a bird standing on a branch."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().NotContain(i =>
            i.Message.Contains("pose") && i.Severity == IssueSeverity.Warning
            && i.Message.Contains("diversity"));
    }

    [Fact]
    public void WhenStyleTypeThenNoClothingDiversityCheck()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "a person in a shirt at the park."),
            ("b.txt", "a person in a shirt at the beach."),
            ("c.txt", "a person in a shirt at the market."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Style));

        issues.Should().NotContain(i =>
            i.Message.Contains("clothing"));
    }

    [Fact]
    public void WhenCharacterTypeThenNoViewpointCheck()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx in close-up with a shirt."),
            ("b.txt", "ohwx in close-up with a jacket."),
            ("c.txt", "ohwx in close-up with a dress."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("viewpoint/angle"));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WhenAllCaptionsEmptyThenReturnsNoIssues()
    {
        var captions = MakeCaptionsNl(
            ("a.txt", ""),
            ("b.txt", ""),
            ("c.txt", ""));

        var act = () => _sut.Run(captions, MakeConfig(LoraType.Character));

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenNoCaptionsThenReturnsEmpty()
    {
        var issues = _sut.Run([], MakeConfig(LoraType.Style));

        issues.Should().BeEmpty();
    }

    [Fact]
    public void WhenCharacterNoClothingTermsAtAllThenNoWarning()
    {
        // No clothing terms mentioned — nothing to flag for clothing
        var captions = MakeCaptionsNl(
            ("a.txt", "ohwx with blue eyes in a park."),
            ("b.txt", "ohwx with blue eyes at the beach."),
            ("c.txt", "ohwx with blue eyes indoors."));

        var issues = _sut.Run(captions, MakeConfig(LoraType.Character));

        issues.Should().NotContain(i =>
            i.Message.Contains("clothing"));
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
