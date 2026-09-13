using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

/// <summary>
/// The comparer must tell the user where each conflicting file came from. Two rows can share a
/// file name (a loose file and a same-named entry inside a dropped ZIP, or two ZIPs), and without
/// the source they are indistinguishable.
/// </summary>
public class FileConflictItemSourceTests
{
    [Fact]
    public void LooseFile_ShowsItsFullPath_AndNoArchiveBadge()
    {
        var item = new FileConflictItem
        {
            ConflictingName = "grok.jpg",
            NewFilePath = @"C:\Users\me\Downloads\grok.jpg",
        };

        item.IsFromArchive.Should().BeFalse();
        item.SourceArchiveName.Should().BeNull();
        item.SourceDisplayText.Should().Be(@"C:\Users\me\Downloads\grok.jpg");
        item.SourceToolTip.Should().Be(@"C:\Users\me\Downloads\grok.jpg");
    }

    [Fact]
    public void ArchiveEntry_ShowsArchiveNameAndEntryPath_NotTheTempExtractionPath()
    {
        var item = new FileConflictItem
        {
            ConflictingName = "grok.jpg",
            NewFilePath = @"C:\Users\me\AppData\Local\Temp\DiffusionNexus_ZipExtract_1a2b3c4d\grok.jpg",
            SourceArchivePath = @"C:\Users\me\Downloads\shoot 3.zip",
            SourceArchiveEntryName = "day2/grok.jpg",
        };

        item.IsFromArchive.Should().BeTrue();
        item.SourceArchiveName.Should().Be("shoot 3.zip");
        item.SourceDisplayText.Should().Be("shoot 3.zip » day2/grok.jpg");
        item.SourceToolTip.Should().Be(@"C:\Users\me\Downloads\shoot 3.zip » day2/grok.jpg");
        item.SourceDisplayText.Should().NotContain("ZipExtract");
    }

    [Fact]
    public void ArchiveEntry_WithoutEntryName_FallsBackToConflictingName()
    {
        var item = new FileConflictItem
        {
            ConflictingName = "grok.jpg",
            NewFilePath = @"C:\Temp\x\grok.jpg",
            SourceArchivePath = @"D:\shoot.zip",
        };

        item.SourceDisplayText.Should().Be("shoot.zip » grok.jpg");
    }
}
