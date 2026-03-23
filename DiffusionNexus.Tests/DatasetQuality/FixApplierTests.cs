using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="FixApplier"/>.
/// </summary>
public class FixApplierTests : IDisposable
{
    private readonly string _testFolder;

    public FixApplierTests()
    {
        _testFolder = Path.Combine(
            Path.GetTempPath(),
            "DiffusionNexus_Tests",
            $"FixApplier_{Guid.NewGuid():N}");

        Directory.CreateDirectory(_testFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testFolder, recursive: true); } catch { /* intentional */ }
    }

    #region Apply — Happy Path

    [Fact]
    public void WhenEditMatchesThenFileIsUpdated()
    {
        // Arrange
        var filePath = CreateFile("caption.txt", "1girl, bad tag, blue eyes");
        var suggestion = MakeSuggestion(filePath, "bad tag, ", "");

        // Act
        var results = FixApplier.Apply(suggestion);

        // Assert
        results.Should().HaveCount(1);
        results[0].Success.Should().BeTrue();
        File.ReadAllText(filePath).Should().Be("1girl, blue eyes");
    }

    [Fact]
    public void WhenBackupEnabledThenBakFileIsCreated()
    {
        // Arrange
        var filePath = CreateFile("caption.txt", "old text");
        var suggestion = MakeSuggestion(filePath, "old text", "new text");

        // Act
        var results = FixApplier.Apply(suggestion, createBackup: true);

        // Assert
        results[0].BackupPath.Should().NotBeNull();
        File.Exists(results[0].BackupPath).Should().BeTrue();
        File.ReadAllText(results[0].BackupPath!).Should().Be("old text");
    }

    [Fact]
    public void WhenBackupDisabledThenNoBakFileIsCreated()
    {
        // Arrange
        var filePath = CreateFile("caption.txt", "old text");
        var suggestion = MakeSuggestion(filePath, "old text", "new text");

        // Act
        var results = FixApplier.Apply(suggestion, createBackup: false);

        // Assert
        results[0].BackupPath.Should().BeNull();
        File.Exists(filePath + ".bak").Should().BeFalse();
    }

    [Fact]
    public void WhenMultipleEditsExistThenAllAreApplied()
    {
        // Arrange
        var file1 = CreateFile("a.txt", "hello world");
        var file2 = CreateFile("b.txt", "foo bar");

        var suggestion = new FixSuggestion
        {
            Description = "multi-fix",
            Edits =
            [
                new FileEdit { FilePath = file1, OriginalText = "hello", NewText = "hi" },
                new FileEdit { FilePath = file2, OriginalText = "foo", NewText = "baz" }
            ]
        };

        // Act
        var results = FixApplier.Apply(suggestion);

        // Assert
        results.Should().HaveCount(2);
        results.Should().AllSatisfy(r => r.Success.Should().BeTrue());
        File.ReadAllText(file1).Should().Be("hi world");
        File.ReadAllText(file2).Should().Be("baz bar");
    }

    #endregion

    #region Apply — Error Cases

    [Fact]
    public void WhenFileDoesNotExistThenReturnsFailure()
    {
        // Arrange
        var fakePath = Path.Combine(_testFolder, "missing.txt");
        var suggestion = MakeSuggestion(fakePath, "x", "y");

        // Act
        var results = FixApplier.Apply(suggestion);

        // Assert
        results.Should().HaveCount(1);
        results[0].Success.Should().BeFalse();
        results[0].ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public void WhenOriginalTextNotFoundThenReturnsFailure()
    {
        // Arrange
        var filePath = CreateFile("caption.txt", "current content");
        var suggestion = MakeSuggestion(filePath, "nonexistent text", "replacement");

        // Act
        var results = FixApplier.Apply(suggestion);

        // Assert
        results[0].Success.Should().BeFalse();
        results[0].ErrorMessage.Should().Contain("changed since analysis");
    }

    [Fact]
    public void WhenSuggestionIsNullThenThrowsArgumentNullException()
    {
        // Act
        var act = () => FixApplier.Apply(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region ReplaceFirst

    [Fact]
    public void WhenMultipleOccurrencesExistThenOnlyFirstIsReplaced()
    {
        var result = FixApplier.ReplaceFirst("cat, cat, dog", "cat", "bird");

        result.Should().Be("bird, cat, dog");
    }

    [Fact]
    public void WhenNoOccurrenceExistsThenSourceIsReturnedUnchanged()
    {
        var result = FixApplier.ReplaceFirst("hello world", "missing", "x");

        result.Should().Be("hello world");
    }

    #endregion

    #region Helpers

    private string CreateFile(string name, string content)
    {
        var path = Path.Combine(_testFolder, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static FixSuggestion MakeSuggestion(string filePath, string original, string replacement) => new()
    {
        Description = "Test fix",
        Edits = [new FileEdit { FilePath = filePath, OriginalText = original, NewText = replacement }]
    };

    #endregion
}
