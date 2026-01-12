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
}
