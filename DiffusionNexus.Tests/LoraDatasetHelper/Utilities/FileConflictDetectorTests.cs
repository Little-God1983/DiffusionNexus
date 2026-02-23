using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

public class FileConflictDetectorTests
{
    private readonly string _testDestination = @"C:\Test\Destination";

    [Fact]
    public void DetectConflicts_Should_Identify_Simple_Conflict()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\image.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(1);
        result.Conflicts[0].ConflictingName.Should().Be("image.png");
        result.NonConflictingFiles.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_Should_Identify_NonConflicting()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\image.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().BeEmpty();
        result.NonConflictingFiles.Should().HaveCount(1);
        result.NonConflictingFiles[0].Should().Be(@"C:\Source\image.png");
    }

    [Fact]
    public void DetectConflicts_Should_Group_Pairs_When_One_Conflicts()
    {
        // Arrange
        // Dropping image and text. Destination has image ONLY.
        // We expect BOTH to be flagged as conflicts so they can be renamed together.
        var dropped = new[] 
        { 
            @"C:\Source\image.png",
            @"C:\Source\image.txt" 
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(2);
        
        // Both files should be in conflicts list
        result.Conflicts.Should().Contain(c => c.ConflictingName == "image.png");
        result.Conflicts.Should().Contain(c => c.ConflictingName == "image.txt");
        
        result.NonConflictingFiles.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_Should_Pass_NonConflicting_Pairs()
    {
        // Arrange
        // Dropping image and text. Destination has neither.
        var dropped = new[] 
        { 
            @"C:\Source\new.png",
            @"C:\Source\new.txt" 
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "existing.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().BeEmpty();
        result.NonConflictingFiles.Should().HaveCount(2);
    }

    [Fact]
    public void DetectConflicts_Should_Handle_CaseIntepensitive_Matching()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\IMAGE.PNG" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(1);
        result.Conflicts[0].ConflictingName.Should().Be("IMAGE.PNG"); // The dropped file name
    }

    [Fact]
    public void DetectConflicts_WhenEmptyDroppedFiles_ReturnsNoConflicts()
    {
        // Arrange
        var dropped = Array.Empty<string>();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "image.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().BeEmpty();
        result.NonConflictingFiles.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_WhenEmptyExistingFiles_AllAreNonConflicting()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\a.png", @"C:\Source\b.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().BeEmpty();
        result.NonConflictingFiles.Should().HaveCount(2);
    }

    [Fact]
    public void DetectConflicts_WhenCaptionConflictsButImageDoesNot_BothAreConflicts()
    {
        // Arrange — only the caption exists in destination, but both should be flagged.
        var dropped = new[]
        {
            @"C:\Source\photo.png",
            @"C:\Source\photo.txt"
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "photo.txt" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(2);
        result.NonConflictingFiles.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_WhenMultipleGroups_SeparatesCorrectly()
    {
        // Arrange — group "a" conflicts, group "b" does not.
        var dropped = new[]
        {
            @"C:\Source\a.png",
            @"C:\Source\a.txt",
            @"C:\Source\b.png",
            @"C:\Source\b.txt"
        };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(2);
        result.Conflicts.Should().Contain(c => c.ConflictingName == "a.png");
        result.Conflicts.Should().Contain(c => c.ConflictingName == "a.txt");

        result.NonConflictingFiles.Should().HaveCount(2);
        result.NonConflictingFiles.Should().Contain(@"C:\Source\b.png");
        result.NonConflictingFiles.Should().Contain(@"C:\Source\b.txt");
    }

    [Fact]
    public void DetectConflicts_SetsExistingFilePathCorrectly()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\img.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "img.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().ContainSingle()
            .Which.ExistingFilePath.Should().Be(Path.Combine(_testDestination, "img.png"));
    }

    [Fact]
    public void DetectConflicts_SetsNewFilePathToSource()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\img.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "img.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().ContainSingle()
            .Which.NewFilePath.Should().Be(@"C:\Source\img.png");
    }

    [Fact]
    public void DetectConflicts_WhenSingleFileNoConflict_AddsToNonConflicting()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\unique.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "other.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.NonConflictingFiles.Should().ContainSingle()
            .Which.Should().Be(@"C:\Source\unique.png");
    }

    [Fact]
    public void DetectConflicts_WhenAllFilesConflict_NonConflictingIsEmpty()
    {
        // Arrange
        var dropped = new[] { @"C:\Source\a.png", @"C:\Source\b.png" };
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.png", "b.png" };

        // Act
        var result = FileConflictDetector.DetectConflicts(dropped, existing, _testDestination);

        // Assert
        result.Conflicts.Should().HaveCount(2);
        result.NonConflictingFiles.Should().BeEmpty();
    }
}
