using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="AnalysisPipeline"/>.
/// Uses a real temp folder with stub caption files so <see cref="CaptionLoader"/>
/// works without mocking, and mocked <see cref="IDatasetCheck"/> instances
/// to verify pipeline orchestration.
/// </summary>
public class AnalysisPipelineTests : IDisposable
{
    private readonly string _testFolder;

    public AnalysisPipelineTests()
    {
        _testFolder = Path.Combine(
            Path.GetTempPath(),
            "DiffusionNexus_Tests",
            $"Pipeline_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testFolder, recursive: true); } catch { /* intentional */ }
    }

    [Fact]
    public void WhenNoChecksRegisteredThenReturnsEmptyReport()
    {
        // Arrange
        CreateFile("img.png", "data");
        CreateFile("img.txt", "a caption");

        var config = MakeConfig(LoraType.Character);
        var sut = CreatePipeline([]);

        // Act
        var report = sut.Analyze(config);

        // Assert
        report.Issues.Should().BeEmpty();
        report.Summary.ChecksRun.Should().Be(0);
        report.Summary.TotalCaptionFiles.Should().Be(1);
        report.Summary.TotalImageFiles.Should().Be(1);
    }

    [Fact]
    public void WhenCheckIsNotApplicableThenItIsSkipped()
    {
        // Arrange
        CreateFile("img.txt", "caption");

        var check = CreateMockCheck("StyleOnly", CheckDomain.Caption, order: 1, applicableTo: LoraType.Style);
        var config = MakeConfig(LoraType.Character);
        var sut = CreatePipeline([check.Object]);

        // Act
        var report = sut.Analyze(config);

        // Assert
        report.Summary.ChecksRun.Should().Be(0);
        check.Verify(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()), Times.Never);
    }

    [Fact]
    public void WhenCheckIsApplicableThenItIsExecuted()
    {
        // Arrange
        CreateFile("img.txt", "a caption");

        var issue = MakeIssue(IssueSeverity.Warning, "TestCheck");
        var check = CreateMockCheck("TestCheck", CheckDomain.Caption, order: 1, applicableTo: LoraType.Character);
        check.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
             .Returns([issue]);

        var config = MakeConfig(LoraType.Character);
        var sut = CreatePipeline([check.Object]);

        // Act
        var report = sut.Analyze(config);

        // Assert
        report.Issues.Should().HaveCount(1);
        report.Issues[0].Message.Should().Be("TestCheck");
        report.Summary.ChecksRun.Should().Be(1);
    }

    [Fact]
    public void WhenMultipleChecksExistThenTheyRunInOrder()
    {
        // Arrange
        CreateFile("img.txt", "caption");

        var callOrder = new List<string>();

        var check1 = CreateMockCheck("First", CheckDomain.Caption, order: 10, applicableTo: LoraType.Concept);
        check1.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
              .Callback(() => callOrder.Add("First"))
              .Returns([]);

        var check2 = CreateMockCheck("Second", CheckDomain.Caption, order: 5, applicableTo: LoraType.Concept);
        check2.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
              .Callback(() => callOrder.Add("Second"))
              .Returns([]);

        var config = MakeConfig(LoraType.Concept);
        // Intentionally register out of order
        var sut = CreatePipeline([check1.Object, check2.Object]);

        // Act
        sut.Analyze(config);

        // Assert — check2 (order 5) should run before check1 (order 10)
        callOrder.Should().Equal("Second", "First");
    }

    [Fact]
    public void WhenIssuesHaveDifferentSeveritiesThenTheyAreSortedCriticalFirst()
    {
        // Arrange
        CreateFile("img.txt", "caption");

        var check = CreateMockCheck("Mixed", CheckDomain.Caption, order: 1, applicableTo: LoraType.Character);
        check.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
             .Returns([
                 MakeIssue(IssueSeverity.Info, "info"),
                 MakeIssue(IssueSeverity.Critical, "critical"),
                 MakeIssue(IssueSeverity.Warning, "warning")
             ]);

        var config = MakeConfig(LoraType.Character);
        var sut = CreatePipeline([check.Object]);

        // Act
        var report = sut.Analyze(config);

        // Assert
        report.Issues.Should().HaveCount(3);
        report.Issues[0].Severity.Should().Be(IssueSeverity.Critical);
        report.Issues[1].Severity.Should().Be(IssueSeverity.Warning);
        report.Issues[2].Severity.Should().Be(IssueSeverity.Info);
    }

    [Fact]
    public void WhenConfigIsNullThenThrowsArgumentNullException()
    {
        // Arrange
        var sut = CreatePipeline([]);

        // Act
        var act = () => sut.Analyze(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenSummaryIsBuiltThenFixableCountIsCorrect()
    {
        // Arrange
        CreateFile("img.txt", "caption");

        var fixableIssue = new Issue
        {
            Severity = IssueSeverity.Warning,
            Message = "fixable",
            Domain = CheckDomain.Caption,
            CheckName = "Test",
            FixSuggestions = [new FixSuggestion
            {
                Description = "Fix it",
                Edits = [new FileEdit { FilePath = "f.txt", OriginalText = "a", NewText = "b" }]
            }]
        };

        var nonFixableIssue = MakeIssue(IssueSeverity.Info, "no fix");

        var check = CreateMockCheck("Test", CheckDomain.Caption, order: 1, applicableTo: LoraType.Character);
        check.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
             .Returns([fixableIssue, nonFixableIssue]);

        var config = MakeConfig(LoraType.Character);
        var sut = CreatePipeline([check.Object]);

        // Act
        var report = sut.Analyze(config);

        // Assert
        report.Summary.FixableIssueCount.Should().Be(1);
    }

    #region Helpers

    private DatasetConfig MakeConfig(LoraType type) => new()
    {
        FolderPath = _testFolder,
        TriggerWord = "ohwx",
        LoraType = type
    };

    private static Issue MakeIssue(IssueSeverity severity, string message) => new()
    {
        Severity = severity,
        Message = message,
        Domain = CheckDomain.Caption,
        CheckName = "Test"
    };

    private static Mock<IDatasetCheck> CreateMockCheck(
        string name,
        CheckDomain domain,
        int order,
        LoraType applicableTo)
    {
        var mock = new Mock<IDatasetCheck>();
        mock.Setup(c => c.Name).Returns(name);
        mock.Setup(c => c.Description).Returns($"{name} description");
        mock.Setup(c => c.Domain).Returns(domain);
        mock.Setup(c => c.Order).Returns(order);
        mock.Setup(c => c.IsApplicable(It.IsAny<LoraType>()))
            .Returns((LoraType t) => t == applicableTo);
        mock.Setup(c => c.Run(It.IsAny<IReadOnlyList<CaptionFile>>(), It.IsAny<DatasetConfig>()))
            .Returns([]);
        return mock;
    }

    private static AnalysisPipeline CreatePipeline(IDatasetCheck[] checks)
    {
        return new AnalysisPipeline(checks, new CaptionLoader());
    }

    private void CreateFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_testFolder, name), content);

    #endregion
}
