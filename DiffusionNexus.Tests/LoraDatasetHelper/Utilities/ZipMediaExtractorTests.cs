using System.IO.Compression;
using DiffusionNexus.UI.Utilities;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

public sealed class ZipMediaExtractorTests : IDisposable
{
    private static readonly string[] MediaAndText = [".png", ".jpg", ".txt", ".zip"];

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "dn-zipextract-tests", Guid.NewGuid().ToString("N"));

    public ZipMediaExtractorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Extract_FlattensNestedEntries_AndKeepsOnlyAllowedExtensions()
    {
        var zip = MakeZip(("a.png", "1"), ("sub/dir/b.jpg", "2"), ("notes.md", "3"), ("inner.zip", "4"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.TempDirectory.Should().NotBeNull();
        Directory.Exists(result.TempDirectory).Should().BeTrue();
        result.ExtractedFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo(["a.png", "b.jpg"]);
        result.ExtractedFiles.Should().OnlyContain(f => File.Exists(f));
        result.ExtractedFiles.Should().OnlyContain(f =>
            Path.GetDirectoryName(f) == result.TempDirectory,
            "entries are flattened into the temp directory");
    }

    [Fact]
    public void Extract_RecordsArchivePathAndEntryNameForEveryExtractedFile()
    {
        var zip = MakeZip(("a.png", "1"), ("sub/dir/b.jpg", "2"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ArchivePath.Should().Be(zip);
        result.EntryNames.Should().HaveCount(2);
        var b = result.ExtractedFiles.Single(f => Path.GetFileName(f) == "b.jpg");
        result.EntryNames[b].Should().Be("sub/dir/b.jpg", "the flattened temp path loses the folder, the entry name keeps it");
        var a = result.ExtractedFiles.Single(f => Path.GetFileName(f) == "a.png");
        result.EntryNames[a].Should().Be("a.png");
    }

    [Fact]
    public void Extract_SkipsDirectoryEntries()
    {
        var zip = MakeZip(("folder/", ""), ("folder/a.png", "1"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ExtractedFiles.Select(Path.GetFileName).Should().BeEquivalentTo(["a.png"]);
    }

    [Fact]
    public void Extract_SuffixesDuplicateFileNamesFromDifferentFolders()
    {
        var zip = MakeZip(("x/a.png", "1"), ("y/a.png", "2"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ExtractedFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo(["a.png", "a_1.png"]);
    }

    [Fact]
    public void Extract_WhenNothingMatches_ReturnsEmptyAndLeavesNoTempDirectory()
    {
        var zip = MakeZip(("readme.md", "1"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ExtractedFiles.Should().BeEmpty();
        result.TempDirectory.Should().BeNull();
        Directory.EnumerateDirectories(_root).Should().BeEmpty();
    }

    [Fact]
    public void Extract_WithNoExtensionFilter_ExtractsEverythingExceptArchives()
    {
        var zip = MakeZip(("a.png", "1"), ("notes.md", "2"), ("inner.zip", "3"));

        var result = ZipMediaExtractor.Extract(zip, [], _root);

        result.ExtractedFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo(["a.png", "notes.md"]);
    }

    [Fact]
    public void Extract_CorruptArchive_ReturnsEmptyWithoutThrowing()
    {
        var bogus = Path.Combine(_root, "broken.zip");
        File.WriteAllText(bogus, "this is not a zip");

        var act = () => ZipMediaExtractor.Extract(bogus, MediaAndText, _root);

        var result = act.Should().NotThrow().Subject;
        result.ExtractedFiles.Should().BeEmpty();
        result.TempDirectory.Should().BeNull();
    }

    /// <summary>
    /// Issue #569: a ZIP whose entries already exist in the dataset used to bypass conflict
    /// detection and crash the import. Extracted files must be detectable as conflicts.
    /// </summary>
    [Fact]
    public void ExtractedFiles_FeedConflictDetection_SplittingDuplicatesFromNewFiles()
    {
        var zip = MakeZip(("shots/dup.png", "1"), ("shots/dup.txt", "c"), ("shots/fresh.png", "2"));
        var existingInDataset = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dup.png" };

        var extracted = ZipMediaExtractor.Extract(zip, MediaAndText, _root);
        var detection = FileConflictDetector.DetectConflicts(
            extracted.ExtractedFiles, existingInDataset, @"C:\Dataset\v1");

        detection.Conflicts.Select(c => c.ConflictingName)
            .Should().BeEquivalentTo(["dup.png", "dup.txt"], "the caption follows its image into the comparer");
        detection.NonConflictingFiles.Select(Path.GetFileName)
            .Should().BeEquivalentTo(["fresh.png"]);
    }

    // -------------------------------------------------------------------
    //  PR #570 review findings
    // -------------------------------------------------------------------

    [Theory]
    [InlineData(@"..\..\evil.png", "evil.png")]
    [InlineData("../../evil.png", "evil.png")]
    [InlineData("day2/cat.jpg", "cat.jpg")]
    [InlineData(@"C:\abs\cat.jpg", "cat.jpg")]
    [InlineData("cat.jpg", "cat.jpg")]
    public void SafeLeafName_StripsEveryDirectoryComponent_RegardlessOfSeparator(string entryName, string expected)
    {
        ZipMediaExtractor.SafeLeafName(entryName).Should().Be(expected);
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("folder/")]
    [InlineData(@"..\..\")]
    public void SafeLeafName_ReturnsNull_WhenNothingUsableRemains(string entryName)
    {
        ZipMediaExtractor.SafeLeafName(entryName).Should().BeNull();
    }

    [Fact]
    public void Extract_NeverWritesOutsideTheTempDirectory()
    {
        // .NET stamps archives made on Windows as Windows-made, and then strips backslashes from
        // entry.Name itself; a Unix-made archive keeps them. Both spellings must land inside tempDir.
        var zip = MakeZip((@"..\..\evil.png", "1"), ("../../evil2.png", "2"), ("ok.png", "3"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ExtractedFiles.Should().OnlyContain(f =>
            Path.GetFullPath(f).StartsWith(Path.GetFullPath(result.TempDirectory!) + Path.DirectorySeparatorChar));
        result.ExtractedFiles.Select(Path.GetFileName).Should().BeEquivalentTo(["evil.png", "evil2.png", "ok.png"]);
        File.Exists(Path.Combine(_root, "evil.png")).Should().BeFalse();
        File.Exists(Path.Combine(Path.GetDirectoryName(_root)!, "evil.png")).Should().BeFalse();
    }

    [Fact]
    public void Extract_SkipsAnEntryThatCannotBeWritten_AndKeepsTheRest()
    {
        // '<' and '>' are illegal in Windows file names, so this entry's ExtractToFile throws.
        var zip = MakeZip(("ok1.png", "1"), ("bad<name>.png", "2"), ("ok2.png", "3"));

        var result = ZipMediaExtractor.Extract(zip, MediaAndText, _root);

        result.ExtractedFiles.Select(Path.GetFileName).Should().BeEquivalentTo(["ok1.png", "ok2.png"]);
        result.SkippedEntryCount.Should().Be(1);
        result.TempDirectory.Should().NotBeNull();
    }

    [Fact]
    public void Extract_SweepsStaleExtractionDirectories_ButKeepsRecentOnes()
    {
        var stale = Path.Combine(_root, "DiffusionNexus_ZipExtract_stale001");
        var recent = Path.Combine(_root, "DiffusionNexus_ZipExtract_recent01");
        var unrelated = Path.Combine(_root, "SomethingElse");
        foreach (var d in new[] { stale, recent, unrelated })
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "x.png"), "x");
        }
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(10));
        File.SetLastWriteTimeUtc(Path.Combine(stale, "x.png"), DateTime.UtcNow - TimeSpan.FromDays(10));
        Directory.SetLastWriteTimeUtc(unrelated, DateTime.UtcNow - TimeSpan.FromDays(10));

        ZipMediaExtractor.Extract(MakeZip(("a.png", "1")), MediaAndText, _root);

        Directory.Exists(stale).Should().BeFalse("an extraction folder older than the retention window is orphaned");
        Directory.Exists(recent).Should().BeTrue("a recent folder may belong to a dialog that is still open");
        Directory.Exists(unrelated).Should().BeTrue("only our own prefix is ever swept");
    }

    [Fact]
    public void DeleteExtractionDirectories_RemovesOwnFolders_SkipsForeignAndMissingOnes()
    {
        var ours = Path.Combine(_root, "DiffusionNexus_ZipExtract_deleteme");
        var foreign = Path.Combine(_root, "SomeoneElsesFolder");
        var missing = Path.Combine(_root, "DiffusionNexus_ZipExtract_gone");
        foreach (var d in new[] { ours, foreign })
        {
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "x.png"), "x");
        }

        var act = () => ZipMediaExtractor.DeleteExtractionDirectories([ours, foreign, missing]);

        act.Should().NotThrow("a folder that is already gone is not an error");
        Directory.Exists(ours).Should().BeFalse("callers hand back the folders the dialog created for them");
        Directory.Exists(foreign).Should().BeTrue("only folders carrying our prefix are ever deleted");
    }

    private string MakeZip(params (string Name, string Content)[] entries)
    {
        var zipPath = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name);
            if (name.EndsWith('/')) continue;
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return zipPath;
    }
}
