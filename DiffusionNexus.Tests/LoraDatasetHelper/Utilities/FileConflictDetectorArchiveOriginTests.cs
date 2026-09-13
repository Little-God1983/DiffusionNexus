using DiffusionNexus.UI.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

public class FileConflictDetectorArchiveOriginTests
{
    private const string Destination = @"C:\Dataset\v1";

    [Fact]
    public void DetectConflicts_StampsArchiveOrigin_OnConflictsThatCameFromAZip()
    {
        var loose = @"C:\Downloads\grok.jpg";
        var fromZip = @"C:\Temp\DiffusionNexus_ZipExtract_abcd1234\grok.jpg";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "grok.jpg" };
        var origins = new Dictionary<string, ArchiveOrigin>(StringComparer.OrdinalIgnoreCase)
        {
            [fromZip] = new(@"C:\Downloads\shoot.zip", "day2/grok.jpg"),
        };

        var result = FileConflictDetector.DetectConflicts([loose, fromZip], existing, Destination, origins);

        result.Conflicts.Should().HaveCount(2);

        var looseItem = result.Conflicts.Single(c => c.NewFilePath == loose);
        looseItem.IsFromArchive.Should().BeFalse();
        looseItem.SourceArchivePath.Should().BeNull();

        var zipItem = result.Conflicts.Single(c => c.NewFilePath == fromZip);
        zipItem.IsFromArchive.Should().BeTrue();
        zipItem.SourceArchivePath.Should().Be(@"C:\Downloads\shoot.zip");
        zipItem.SourceArchiveEntryName.Should().Be("day2/grok.jpg");
    }

    /// <summary>
    /// PR #570 review: the group-by was ordinal while the dictionary was case-insensitive, so
    /// <c>Cat.jpg</c> + <c>cat.txt</c> in one call threw "same key has already been added" from an
    /// async void handler. Reachable now that detection runs over the accumulated selection.
    /// </summary>
    [Fact]
    public void DetectConflicts_BaseNamesDifferingOnlyInCase_AreOnePair_AndDoNotThrow()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cat.jpg" };

        var act = () => FileConflictDetector.DetectConflicts(
            [@"C:\Downloads\Cat.jpg", @"C:\Downloads\cat.txt"], existing, Destination);

        var result = act.Should().NotThrow().Subject;
        result.Conflicts.Select(c => c.ConflictingName)
            .Should().BeEquivalentTo(["Cat.jpg", "cat.txt"], "the caption follows its image into the comparer");
        result.NonConflictingFiles.Should().BeEmpty();
    }

    [Fact]
    public void DetectConflicts_WithoutOriginMap_LeavesOriginUnset()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "grok.jpg" };

        var result = FileConflictDetector.DetectConflicts([@"C:\Downloads\grok.jpg"], existing, Destination);

        result.Conflicts.Single().IsFromArchive.Should().BeFalse();
    }
}
