using System.Reflection;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>
/// Unit tests for <see cref="ImageQualityTabViewModel"/>.
/// <para>
/// The ViewModel is always constructed with an empty check collection so no
/// analysis pipeline (and no image IO) ever runs. <c>BuildVerdict</c> is a
/// private static pure function reached through reflection.
/// </para>
/// <para>
/// Expectations contain the same literal em dash (U+2014) and middle dot
/// (U+00B7) that the production code emits via <c>—</c> / <c>·</c>
/// escapes.
/// </para>
/// </summary>
public class ImageQualityTabViewModelTests
{
    /// <summary>Separator used by <c>BuildVerdict</c> when joining parts.</summary>
    private const string Sep = " · ";

    private static readonly MethodInfo BuildVerdictMethod =
        typeof(ImageQualityTabViewModel).GetMethod(
            "BuildVerdict",
            BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("BuildVerdict not found on ImageQualityTabViewModel.");

    private static string BuildVerdict(double? blur, double? exposure, double? noise, double? color)
        => (string)BuildVerdictMethod.Invoke(null, [blur, exposure, noise, color])!;

    private static ImageQualityTabViewModel CreateVm()
        => new(Array.Empty<IImageQualityCheck>());

    private static Issue MakeIssue(string checkName, params string[] affectedFiles) => new()
    {
        Severity = IssueSeverity.Warning,
        Message = $"{checkName} issue",
        Domain = CheckDomain.Image,
        CheckName = checkName,
        AffectedFiles = affectedFiles
    };

    private static ImageCheckResult MakeResult(
        string checkName,
        double score,
        IReadOnlyList<PerImageScore>? perImage = null,
        IReadOnlyList<Issue>? issues = null) => new()
        {
            CheckName = checkName,
            Score = score,
            Issues = issues ?? [],
            PerImageScores = perImage ?? []
        };

    #region Construction

    [Fact]
    public void WhenConstructedWithNullChecksThenThrowsArgumentNullException()
    {
        var act = () => new ImageQualityTabViewModel(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenConstructedThenCommandsExistAndStateIsEmpty()
    {
        var vm = CreateVm();

        vm.AnalyzeCommand.Should().NotBeNull();
        vm.OpenFixerCommand.Should().NotBeNull();
        vm.Issues.Should().BeEmpty();
        vm.AllImages.Should().BeEmpty();
        vm.AffectedImages.Should().BeEmpty();
        vm.LastResults.Should().BeEmpty();
        vm.HasResults.Should().BeFalse();
        vm.IsAnalyzing.Should().BeFalse();
        vm.SummaryText.Should().Be("Not analyzed yet");
    }

    #endregion

    #region BuildVerdict - blur arm (4 thresholds: 20 / 40 / 65 / 80)

    [Theory]
    [InlineData(-1.0, "Extremely blurry — replace with a sharper image")]
    [InlineData(0.0, "Extremely blurry — replace with a sharper image")]
    [InlineData(19.999, "Extremely blurry — replace with a sharper image")]
    [InlineData(20.0, "Very blurry — consider replacing")]
    [InlineData(39.999, "Very blurry — consider replacing")]
    [InlineData(40.0, "Slightly soft — usable but may reduce output detail")]
    [InlineData(64.999, "Slightly soft — usable but may reduce output detail")]
    [InlineData(65.0, "Acceptable sharpness")]
    [InlineData(79.999, "Acceptable sharpness")]
    [InlineData(80.0, "Sharp")]
    [InlineData(100.0, "Sharp")]
    public void WhenOnlyBlurIsScoredThenVerdictMatchesTheBlurThreshold(double blur, string expected)
    {
        BuildVerdict(blur, null, null, null).Should().Be(expected);
    }

    #endregion

    #region BuildVerdict - exposure arm (4 thresholds: 20 / 40 / 65 / 80)

    [Theory]
    [InlineData(0.0, "Severely mis-exposed — replace this image")]
    [InlineData(19.999, "Severely mis-exposed — replace this image")]
    [InlineData(20.0, "Poor exposure — consider replacing")]
    [InlineData(39.999, "Poor exposure — consider replacing")]
    [InlineData(40.0, "Exposure could be better — review")]
    [InlineData(64.999, "Exposure could be better — review")]
    [InlineData(65.0, "Acceptable exposure")]
    [InlineData(79.999, "Acceptable exposure")]
    [InlineData(80.0, "Well exposed")]
    [InlineData(100.0, "Well exposed")]
    public void WhenOnlyExposureIsScoredThenVerdictMatchesTheExposureThreshold(double exposure, string expected)
    {
        BuildVerdict(null, exposure, null, null).Should().Be(expected);
    }

    #endregion

    #region BuildVerdict - noise arm (only 3 thresholds: 20 / 40 / 70)

    [Theory]
    [InlineData(0.0, "Very noisy — denoise or replace")]
    [InlineData(19.999, "Very noisy — denoise or replace")]
    [InlineData(20.0, "Noisy — consider denoising")]
    [InlineData(39.999, "Noisy — consider denoising")]
    [InlineData(40.0, "Slight noise — acceptable")]
    [InlineData(69.999, "Slight noise — acceptable")]
    [InlineData(70.0, "Clean")]
    [InlineData(100.0, "Clean")]
    public void WhenOnlyNoiseIsScoredThenVerdictMatchesTheNoiseThreshold(double noise, string expected)
    {
        BuildVerdict(null, null, noise, null).Should().Be(expected);
    }

    #endregion

    #region BuildVerdict - color arm (only 2 thresholds: 50 / 75)

    [Theory]
    [InlineData(0.0, "Color issues detected — review")]
    [InlineData(49.999, "Color issues detected — review")]
    [InlineData(50.0, "Minor color concerns")]
    [InlineData(74.999, "Minor color concerns")]
    [InlineData(75.0, "Good color distribution")]
    [InlineData(100.0, "Good color distribution")]
    public void WhenOnlyColorIsScoredThenVerdictMatchesTheColorThreshold(double color, string expected)
    {
        BuildVerdict(null, null, null, color).Should().Be(expected);
    }

    #endregion

    #region BuildVerdict - composition and numeric edge cases

    [Fact]
    public void WhenNoScoresAreSuppliedThenVerdictIsNoChecksRun()
    {
        BuildVerdict(null, null, null, null).Should().Be("No checks run");
    }

    [Fact]
    public void WhenAllFourScoresAreSuppliedThenPartsAreJoinedInFixedOrder()
    {
        BuildVerdict(90, 90, 90, 90).Should().Be(
            "Sharp" + Sep +
            "Well exposed" + Sep +
            "Clean" + Sep +
            "Good color distribution");
    }

    [Fact]
    public void WhenOnlySomeScoresAreSuppliedThenOnlyThosePartsAppear()
    {
        var verdict = BuildVerdict(10, null, 10, null);

        verdict.Should().Be(
            "Extremely blurry — replace with a sharper image" + Sep +
            "Very noisy — denoise or replace");
        verdict.Should().NotContain("exposed");
        verdict.Should().NotContain("color");
    }

    [Fact]
    public void WhenScoresAreMixedThenEachArmIsEvaluatedIndependently()
    {
        BuildVerdict(19, 65, 40, 74).Should().Be(
            "Extremely blurry — replace with a sharper image" + Sep +
            "Acceptable exposure" + Sep +
            "Slight noise — acceptable" + Sep +
            "Minor color concerns");
    }

    [Fact]
    public void WhenScoresAreExactlyAtEveryUpperBoundaryThenTheBestArmIsUsed()
    {
        BuildVerdict(80, 80, 70, 75).Should().Be(
            "Sharp" + Sep + "Well exposed" + Sep + "Clean" + Sep + "Good color distribution");
    }

    [Fact]
    public void WhenScoresAreJustBelowEveryUpperBoundaryThenTheNextArmDownIsUsed()
    {
        BuildVerdict(79.999, 79.999, 69.999, 74.999).Should().Be(
            "Acceptable sharpness" + Sep +
            "Acceptable exposure" + Sep +
            "Slight noise — acceptable" + Sep +
            "Minor color concerns");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void WhenBlurIsNotComparableThenItFallsIntoTheDefaultArm(double blur)
    {
        // NaN fails every relational pattern, so the switch drops through to "_".
        BuildVerdict(blur, null, null, null).Should().Be("Sharp");
    }

    [Fact]
    public void WhenScoreIsNegativeInfinityThenTheLowestArmIsSelected()
    {
        BuildVerdict(double.NegativeInfinity, null, null, null)
            .Should().Be("Extremely blurry — replace with a sharper image");
    }

    [Fact]
    public void WhenScoreIsAboveOneHundredThenTheBestArmIsStillSelected()
    {
        BuildVerdict(500, 500, 500, 500).Should().Be(
            "Sharp" + Sep + "Well exposed" + Sep + "Clean" + Sep + "Good color distribution");
    }

    [Fact]
    public void WhenAScoreIsZeroThenItIsTreatedAsAScoreRatherThanAMissingCheck()
    {
        // 0 is a real score; only null means "check did not run".
        BuildVerdict(0, null, null, null)
            .Should().Be("Extremely blurry — replace with a sharper image");
    }

    #endregion

    #region CanAnalyze gate

    [Fact]
    public void WhenNoFolderIsSetThenCanAnalyzeIsFalse()
    {
        var vm = CreateVm();

        vm.CanAnalyze.Should().BeFalse();
        vm.AnalyzeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenFolderIsSetThenCanAnalyzeIsTrue()
    {
        var vm = CreateVm();

        vm.RefreshContext(@"C:\datasets\demo");

        vm.CanAnalyze.Should().BeTrue();
        vm.AnalyzeCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenFolderIsEmptyStringThenCanAnalyzeIsFalse()
    {
        var vm = CreateVm();
        vm.RefreshContext(@"C:\datasets\demo");

        vm.RefreshContext(string.Empty);

        vm.CanAnalyze.Should().BeFalse();
        vm.AnalyzeCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenFolderIsNullThenItIsNormalizedToEmptyAndCanAnalyzeIsFalse()
    {
        var vm = CreateVm();
        vm.RefreshContext(@"C:\datasets\demo");

        vm.RefreshContext(null!);

        vm.CanAnalyze.Should().BeFalse();
    }

    [Fact]
    public void WhenRefreshContextRunsThenPreviousResultsAreCleared()
    {
        var vm = CreateVm();
        vm.ApplyResults([
            MakeResult("Blur Detection", 70,
                perImage: [new PerImageScore(@"C:\d\a.png", 70, "v")],
                issues: [MakeIssue("Blur Detection", @"C:\d\a.png")])
        ]);
        vm.HasResults.Should().BeTrue();

        vm.RefreshContext(@"C:\datasets\other");

        vm.HasResults.Should().BeFalse();
        vm.Issues.Should().BeEmpty();
        vm.AllImages.Should().BeEmpty();
        vm.AffectedImages.Should().BeEmpty();
        vm.SelectedIssue.Should().BeNull();
        vm.SummaryText.Should().Be("Not analyzed yet");
    }

    #endregion

    #region CanOpenFixer gate

    [Fact]
    public void WhenNoResultsThenCanOpenFixerIsFalse()
    {
        var vm = CreateVm();

        vm.CanOpenFixer.Should().BeFalse();
        vm.OpenFixerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void WhenResultsAreAppliedThenCanOpenFixerIsTrue()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 70, perImage: [new PerImageScore(@"C:\d\a.png", 70, "v")])
        ]);

        vm.HasResults.Should().BeTrue();
        vm.CanOpenFixer.Should().BeTrue();
        vm.OpenFixerCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void WhenAnEmptyResultListIsAppliedThenCanOpenFixerStaysFalse()
    {
        var vm = CreateVm();

        vm.ApplyResults([]);

        vm.HasResults.Should().BeFalse();
        vm.LastResults.Should().BeEmpty();
        vm.CanOpenFixer.Should().BeFalse();
        vm.SummaryText.Should().Be("No image quality checks were run.");
    }

    [Fact]
    public void WhenContextIsRefreshedAfterResultsThenCanOpenFixerGoesFalseEvenThoughLastResultsRemain()
    {
        var vm = CreateVm();
        vm.ApplyResults([
            MakeResult("Blur Detection", 70, perImage: [new PerImageScore(@"C:\d\a.png", 70, "v")])
        ]);

        vm.RefreshContext(@"C:\datasets\other");

        // RefreshContext resets HasResults but intentionally leaves LastResults alone;
        // the HasResults term is what closes the gate.
        vm.LastResults.Should().NotBeEmpty();
        vm.HasResults.Should().BeFalse();
        vm.CanOpenFixer.Should().BeFalse();
    }

    [Fact]
    public async Task WhenDialogServiceIsMissingThenOpenFixerIsANoOp()
    {
        var vm = CreateVm();
        vm.ApplyResults([
            MakeResult("Blur Detection", 70, perImage: [new PerImageScore(@"C:\d\a.png", 70, "v")])
        ]);
        vm.DialogService.Should().BeNull();

        var act = async () => await vm.OpenFixerCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
    }

    #endregion

    #region ApplyResults

    [Fact]
    public void WhenMultipleChecksScoreTheSameFileThenTheOverallScoreIsTheirMean()
    {
        var vm = CreateVm();
        const string path = @"C:\d\a.png";

        vm.ApplyResults([
            MakeResult("Blur Detection", 50, perImage: [new PerImageScore(path, 40, "lap 12")]),
            MakeResult("Exposure Analysis", 50, perImage: [new PerImageScore(path, 90, "ok")])
        ]);

        vm.AllImages.Should().ContainSingle();
        var item = vm.AllImages[0];
        item.FilePath.Should().Be(path);
        item.FileName.Should().Be("a.png");
        item.OverallScore.Should().Be(65.0);
        item.ScoreBreakdown.Should().HaveCount(2);
        item.Verdict.Should().Be(
            "Slightly soft — usable but may reduce output detail" + Sep + "Well exposed");
    }

    [Fact]
    public void WhenSeveralImagesAreScoredThenAllImagesIsSortedWorstFirst()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 60, perImage:
            [
                new PerImageScore(@"C:\d\good.png", 95, "sharp"),
                new PerImageScore(@"C:\d\bad.png", 10, "mush"),
                new PerImageScore(@"C:\d\mid.png", 55, "soft")
            ])
        ]);

        vm.AllImages.Select(i => i.FileName)
            .Should().ContainInOrder("bad.png", "mid.png", "good.png");
    }

    [Fact]
    public void WhenCheckNameIsUnrecognizedThenTheImageIsListedWithNoScores()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Totally Unknown Check", 42, perImage: [new PerImageScore(@"C:\d\a.png", 99, "x")])
        ]);

        vm.AllImages.Should().ContainSingle();
        vm.AllImages[0].OverallScore.Should().Be(0);
        vm.AllImages[0].Verdict.Should().Be("No checks run");
        vm.AllImages[0].ScoreBreakdown.Should().BeEmpty();
    }

    [Theory]
    [InlineData(85.0, "Excellent")]
    [InlineData(90.0, "Excellent")]
    [InlineData(84.9, "Good")]
    [InlineData(65.0, "Good")]
    [InlineData(64.9, "Fair")]
    [InlineData(40.0, "Fair")]
    [InlineData(39.9, "Poor")]
    [InlineData(0.0, "Poor")]
    public void WhenResultsAreAppliedThenOverallLabelMatchesTheAverageScoreBand(double score, string label)
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", score, perImage: [new PerImageScore(@"C:\d\a.png", score, "x")])
        ]);

        vm.OverallScoreLabel.Should().Be(label);
        vm.OverallScore.Should().Be(Math.Round(score, 1));
    }

    [Fact]
    public void WhenResultsHaveIssuesThenTheFirstIssueIsSelectedAndItsFilesArePopulated()
    {
        var vm = CreateVm();
        const string a = @"C:\d\a.png";
        const string b = @"C:\d\b.png";

        vm.ApplyResults([
            MakeResult("Blur Detection", 30,
                perImage: [new PerImageScore(a, 20, "x"), new PerImageScore(b, 40, "y")],
                issues: [MakeIssue("Blur Detection", a)])
        ]);

        vm.Issues.Should().ContainSingle();
        vm.SelectedIssue.Should().BeSameAs(vm.Issues[0]);
        vm.HasSelectedIssue.Should().BeTrue();
        vm.ShowAllImages.Should().BeFalse();
        vm.AffectedImages.Should().ContainSingle();
        vm.AffectedImages[0].FilePath.Should().Be(a);
    }

    [Fact]
    public void WhenAnIssueReferencesAnUnknownFileThenItIsSkippedInsteadOfThrowing()
    {
        var vm = CreateVm();

        var act = () => vm.ApplyResults([
            MakeResult("Blur Detection", 30,
                perImage: [new PerImageScore(@"C:\d\a.png", 20, "x")],
                issues: [MakeIssue("Blur Detection", @"C:\d\ghost.png")])
        ]);

        act.Should().NotThrow();
        vm.AffectedImages.Should().BeEmpty();
    }

    [Fact]
    public void WhenSelectedIssueIsClearedThenAffectedImagesAreEmptiedAndAllImagesModeReturns()
    {
        var vm = CreateVm();
        vm.ApplyResults([
            MakeResult("Blur Detection", 30,
                perImage: [new PerImageScore(@"C:\d\a.png", 20, "x")],
                issues: [MakeIssue("Blur Detection", @"C:\d\a.png")])
        ]);
        vm.AffectedImages.Should().NotBeEmpty();

        vm.SelectedIssue = null;

        vm.AffectedImages.Should().BeEmpty();
        vm.HasSelectedIssue.Should().BeFalse();
        vm.ShowAllImages.Should().BeTrue();
    }

    [Fact]
    public void WhenNoIssuesAreReportedThenAllImagesModeIsActive()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 90, perImage: [new PerImageScore(@"C:\d\a.png", 90, "x")])
        ]);

        vm.SelectedIssue.Should().BeNull();
        vm.HasSelectedIssue.Should().BeFalse();
        vm.ShowAllImages.Should().BeTrue();
        vm.IssueCount.Should().Be(0);
        vm.SummaryText.Should().Be("Score: 90 (Excellent) · No issues");
    }

    [Fact]
    public void WhenExactlyOneIssueIsReportedThenTheSummaryIsSingular()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 50,
                perImage: [new PerImageScore(@"C:\d\a.png", 50, "x")],
                issues: [MakeIssue("Blur Detection", @"C:\d\a.png")])
        ]);

        vm.IssueCount.Should().Be(1);
        vm.SummaryText.Should().Be("Score: 50 (Fair) · 1 issue");
    }

    [Fact]
    public void WhenSeveralIssuesAreReportedThenTheSummaryIsPlural()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 50,
                perImage: [new PerImageScore(@"C:\d\a.png", 50, "x")],
                issues:
                [
                    MakeIssue("Blur Detection", @"C:\d\a.png"),
                    MakeIssue("Blur Detection", @"C:\d\a.png")
                ])
        ]);

        vm.IssueCount.Should().Be(2);
        vm.SummaryText.Should().Be("Score: 50 (Fair) · 2 issues");
    }

    [Fact]
    public void WhenResultsAreAppliedThenAnalysisCompletedIsRaisedWithScoreIssueCountAndLabel()
    {
        var vm = CreateVm();
        (double Score, int Issues, string Label)? captured = null;
        vm.AnalysisCompleted += (s, i, l) => captured = (s, i, l);

        vm.ApplyResults([
            MakeResult("Blur Detection", 70,
                perImage: [new PerImageScore(@"C:\d\a.png", 70, "x")],
                issues: [MakeIssue("Blur Detection", @"C:\d\a.png")])
        ]);

        captured.Should().NotBeNull();
        captured!.Value.Score.Should().Be(70);
        captured.Value.Issues.Should().Be(1);
        captured.Value.Label.Should().Be("Good");
    }

    [Fact]
    public void WhenAnEmptyResultListIsAppliedThenAnalysisCompletedIsNotRaised()
    {
        var vm = CreateVm();
        var raised = false;
        vm.AnalysisCompleted += (_, _, _) => raised = true;

        vm.ApplyResults([]);

        raised.Should().BeFalse();
    }

    [Fact]
    public void WhenResultsAreAppliedTwiceThenTheSecondRunReplacesTheFirst()
    {
        var vm = CreateVm();
        vm.ApplyResults([
            MakeResult("Blur Detection", 10, perImage:
            [
                new PerImageScore(@"C:\d\a.png", 10, "x"),
                new PerImageScore(@"C:\d\b.png", 10, "x")
            ])
        ]);
        vm.AllImages.Should().HaveCount(2);

        vm.ApplyResults([
            MakeResult("Blur Detection", 90, perImage: [new PerImageScore(@"C:\d\c.png", 90, "x")])
        ]);

        vm.AllImages.Should().ContainSingle();
        vm.AllImages[0].FileName.Should().Be("c.png");
        vm.LastResults.Should().ContainSingle();
    }

    [Fact]
    public void WhenFilePathsDifferOnlyByCaseThenTheyAreTreatedAsOneImage()
    {
        var vm = CreateVm();

        vm.ApplyResults([
            MakeResult("Blur Detection", 50, perImage: [new PerImageScore(@"C:\d\A.png", 20, "x")]),
            MakeResult("Exposure Analysis", 50, perImage: [new PerImageScore(@"C:\d\a.png", 80, "y")])
        ]);

        vm.AllImages.Should().ContainSingle();
        vm.AllImages[0].OverallScore.Should().Be(50);
    }

    #endregion

    #region ImageQualityItemViewModel

    [Theory]
    [InlineData(80.0, "#4CAF50")]
    [InlineData(79.9, "#8BC34A")]
    [InlineData(65.0, "#8BC34A")]
    [InlineData(64.9, "#FFA726")]
    [InlineData(40.0, "#FFA726")]
    [InlineData(39.9, "#FF6B6B")]
    [InlineData(0.0, "#FF6B6B")]
    public void WhenScoreVariesThenScoreColorMatchesTheBand(double score, string expected)
    {
        MakeItem(score).ScoreColor.Should().Be(expected);
    }

    [Theory]
    [InlineData(80.0, "✔")]
    [InlineData(79.9, "⚠")]
    [InlineData(40.0, "⚠")]
    [InlineData(39.9, "✖")]
    public void WhenScoreVariesThenSeverityIconMatchesTheBand(double score, string expected)
    {
        MakeItem(score).SeverityIcon.Should().Be(expected);
    }

    [Fact]
    public void WhenToggleExpandIsExecutedThenIsExpandedFlips()
    {
        var item = MakeItem(50);
        item.IsExpanded.Should().BeFalse();

        item.ToggleExpandCommand.Execute(null);
        item.IsExpanded.Should().BeTrue();

        item.ToggleExpandCommand.Execute(null);
        item.IsExpanded.Should().BeFalse();
    }

    [Fact]
    public void WhenItemIsCreatedThenImagePathMirrorsFilePath()
    {
        var item = MakeItem(50);

        item.ImagePath.Should().Be(item.FilePath);
    }

    private static ImageQualityItemViewModel MakeItem(double score) => new()
    {
        FileName = "a.png",
        FilePath = @"C:\d\a.png",
        OverallScore = score,
        Verdict = "v",
        ScoreBreakdown = []
    };

    #endregion
}
