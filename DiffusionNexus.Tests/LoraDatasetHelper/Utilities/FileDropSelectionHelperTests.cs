using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

public class FileDropSelectionHelperTests
{
    [Fact]
    public void MergeDistinct_KeepsOrder_AndDropsCaseInsensitiveDuplicates()
    {
        var selected = new[] { @"C:\A\one.png", @"C:\A\two.png" };
        var incoming = new[] { @"C:\A\TWO.PNG", @"C:\B\three.png", @"C:\B\three.png" };

        var merged = FileDropSelectionHelper.MergeDistinct(selected, incoming);

        merged.Should().Equal(@"C:\A\one.png", @"C:\A\two.png", @"C:\B\three.png");
    }

    [Fact]
    public void MergeDistinct_IgnoresEmptyEntries()
    {
        var merged = FileDropSelectionHelper.MergeDistinct([], ["", "  ", @"C:\A\one.png"]);

        merged.Should().Equal(@"C:\A\one.png");
    }

    [Fact]
    public void BuildFinalFileList_IncludesNonConflicting_AndResolvedButNotIgnored()
    {
        var nonConflicting = new List<string> { @"C:\Src\safe.png" };
        var resolution = new FileConflictResolutionResult
        {
            Confirmed = true,
            Conflicts =
            [
                Conflict(@"C:\Src\over.png", FileConflictResolution.Override),
                Conflict(@"C:\Src\ren.png", FileConflictResolution.Rename),
                Conflict(@"C:\Src\skip.png", FileConflictResolution.Ignore),
            ]
        };

        var files = FileDropSelectionHelper.BuildFinalFileList(resolution, nonConflicting);

        files.Should().Equal(@"C:\Src\safe.png", @"C:\Src\over.png", @"C:\Src\ren.png");
    }

    [Fact]
    public void BuildSkippedEntriesNotice_ReturnsNull_WhenNothingWasSkipped()
    {
        FileDropSelectionHelper.BuildSkippedEntriesNotice([]).Should().BeNull();
        FileDropSelectionHelper.BuildSkippedEntriesNotice([("a.zip", 0)]).Should().BeNull();
    }

    [Fact]
    public void BuildSkippedEntriesNotice_SingleArchive_SingularAndPlural()
    {
        FileDropSelectionHelper.BuildSkippedEntriesNotice([("shoot.zip", 1)])
            .Should().Be("1 entry in shoot.zip could not be extracted and was skipped.");
        FileDropSelectionHelper.BuildSkippedEntriesNotice([("shoot.zip", 3)])
            .Should().Be("3 entries in shoot.zip could not be extracted and were skipped.");
    }

    [Fact]
    public void BuildSkippedEntriesNotice_MultipleArchives_ListsEachAndSumsTheTotal()
    {
        FileDropSelectionHelper.BuildSkippedEntriesNotice([("shoot.zip", 2), ("more.zip", 0), ("last.zip", 1)])
            .Should().Be("3 entries could not be extracted and were skipped (shoot.zip: 2, last.zip: 1).");
    }

    private static FileConflictItem Conflict(string newPath, FileConflictResolution resolution) => new()
    {
        ConflictingName = Path.GetFileName(newPath),
        NewFilePath = newPath,
        ExistingFilePath = Path.Combine(@"C:\Dest", Path.GetFileName(newPath)),
        Resolution = resolution
    };
}
