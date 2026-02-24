using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

public class DatasetFileImporterTests
{
    private const string DestFolder = @"C:\Dest";

    private readonly MockFileOperations _fileOps = new();
    private readonly DatasetFileImporter _importer;

    public DatasetFileImporterTests()
    {
        _importer = new DatasetFileImporter(_fileOps);
    }

    // -------------------------------------------------------------------
    //  Non-conflicting file copy
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenNoConflicts_CopiesAllFiles()
    {
        // Arrange
        var files = new[] { @"C:\Src\a.png", @"C:\Src\b.png" };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(2);
        result.TotalAdded.Should().Be(2);
        result.ProcessedSourceFiles.Should().BeEquivalentTo(files);
        _fileOps.CopiedFiles.Should().HaveCount(2);
        _fileOps.CopiedFiles.Should().Contain((@"C:\Src\a.png", @"C:\Dest\a.png", false));
        _fileOps.CopiedFiles.Should().Contain((@"C:\Src\b.png", @"C:\Dest\b.png", false));
    }

    [Fact]
    public async Task ImportResolved_WhenNoConflicts_CopiesWithOverwriteFalse()
    {
        // Arrange
        var files = new[] { @"C:\Src\image.png" };

        // Act
        await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        _fileOps.CopiedFiles.Should().ContainSingle()
            .Which.Overwrite.Should().BeFalse();
    }

    [Fact]
    public async Task ImportResolved_WhenMoveFiles_MovesInsteadOfCopy()
    {
        // Arrange
        var files = new[] { @"C:\Src\a.png" };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: true);

        // Assert
        result.Copied.Should().Be(1);
        _fileOps.CopiedFiles.Should().BeEmpty();
        _fileOps.MovedFiles.Should().ContainSingle()
            .Which.Should().Be((@"C:\Src\a.png", @"C:\Dest\a.png", false));
    }

    [Fact]
    public async Task ImportResolved_WhenEmptyFileList_ReturnsZeroCounts()
    {
        // Arrange & Act
        var result = await _importer.ImportResolvedAsync(
            [], conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(0);
        result.TotalAdded.Should().Be(0);
        result.ProcessedSourceFiles.Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    //  Override resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenOverride_CopiesWithOverwriteTrue()
    {
        // Arrange
        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Override));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Overridden.Should().Be(1);
        result.TotalAdded.Should().Be(1);
        _fileOps.CopiedFiles.Should().ContainSingle()
            .Which.Overwrite.Should().BeTrue();
    }

    [Fact]
    public async Task ImportResolved_WhenOverrideAndMove_MovesWithOverwriteTrue()
    {
        // Arrange
        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Override));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: true);

        // Assert
        result.Overridden.Should().Be(1);
        _fileOps.MovedFiles.Should().ContainSingle()
            .Which.Overwrite.Should().BeTrue();
    }

    [Fact]
    public async Task ImportResolved_WhenOverride_DoesNotDeleteBeforeCopy()
    {
        // Arrange — the old code used to delete + copy(overwrite:false). 
        // We verify delete is NOT called; the copy with overwrite:true is sufficient.
        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Override));

        // Act
        await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        _fileOps.DeletedFiles.Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    //  Rename resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenRename_GeneratesUniqueFileName()
    {
        // Arrange — existing file on disk means _1 suffix is needed.
        _fileOps.ExistingFiles.Add(@"C:\Dest\img.png");

        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Rename));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Renamed.Should().Be(1);
        _fileOps.CopiedFiles.Should().ContainSingle()
            .Which.Destination.Should().Be(@"C:\Dest\img_1.png");
    }

    [Fact]
    public async Task ImportResolved_WhenRename_SkipsExistingSuffixes()
    {
        // Arrange — _1 already exists on disk, should jump to _2.
        _fileOps.ExistingFiles.Add(@"C:\Dest\img.png");
        _fileOps.ExistingFiles.Add(@"C:\Dest\img_1.png");

        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Rename));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        _fileOps.CopiedFiles.Should().ContainSingle()
            .Which.Destination.Should().Be(@"C:\Dest\img_2.png");
    }

    [Fact]
    public async Task ImportResolved_WhenRenameAndMove_MovesWithRenamedPath()
    {
        // Arrange
        _fileOps.ExistingFiles.Add(@"C:\Dest\img.png");

        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Rename));

        // Act
        await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: true);

        // Assert
        _fileOps.MovedFiles.Should().ContainSingle()
            .Which.Destination.Should().Be(@"C:\Dest\img_1.png");
    }

    // -------------------------------------------------------------------
    //  Paired file rename synchronization
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenRenamePair_BothFilesGetSameBaseName()
    {
        // Arrange — image + caption share base name, both set to Rename.
        _fileOps.ExistingFiles.Add(@"C:\Dest\photo.png");

        var resolution = MakeResolution(
            MakeConflict("photo.png", @"C:\Src\photo.png", @"C:\Dest\photo.png",
                FileConflictResolution.Rename),
            MakeConflict("photo.txt", @"C:\Src\photo.txt", @"C:\Dest\photo.txt",
                FileConflictResolution.Rename));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Renamed.Should().Be(2);
        _fileOps.CopiedFiles.Should().HaveCount(2);

        var pngDest = _fileOps.CopiedFiles.Single(c => c.Destination.EndsWith(".png")).Destination;
        var txtDest = _fileOps.CopiedFiles.Single(c => c.Destination.EndsWith(".txt")).Destination;

        Path.GetFileNameWithoutExtension(pngDest).Should().Be("photo_1");
        Path.GetFileNameWithoutExtension(txtDest).Should().Be("photo_1");
    }

    // -------------------------------------------------------------------
    //  Ignore resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenIgnore_SkipsFileAndIncreasesIgnoredCount()
    {
        // Arrange
        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Ignore));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Ignored.Should().Be(1);
        result.TotalAdded.Should().Be(0);
        _fileOps.CopiedFiles.Should().BeEmpty();
        _fileOps.MovedFiles.Should().BeEmpty();
    }

    // -------------------------------------------------------------------
    //  Mixed resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenMixedResolutions_CountsCorrectly()
    {
        // Arrange
        _fileOps.ExistingFiles.Add(@"C:\Dest\a.png");

        var resolution = MakeResolution(
            MakeConflict("a.png", @"C:\Src\a.png", @"C:\Dest\a.png",
                FileConflictResolution.Override),
            MakeConflict("b.png", @"C:\Src\b.png", @"C:\Dest\b.png",
                FileConflictResolution.Rename),
            MakeConflict("c.png", @"C:\Src\c.png", @"C:\Dest\c.png",
                FileConflictResolution.Ignore));

        var nonConflicting = new[] { @"C:\Src\d.png" };

        // Act
        var result = await _importer.ImportResolvedAsync(
            nonConflicting, resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(1);
        result.Overridden.Should().Be(1);
        result.Renamed.Should().Be(1);
        result.Ignored.Should().Be(1);
        result.TotalAdded.Should().Be(3);
        result.ProcessedSourceFiles.Should().HaveCount(3);
    }

    // -------------------------------------------------------------------
    //  Intra-batch duplicate filename detection
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenDuplicateFileNamesInBatch_RenamesSecondFile()
    {
        // Arrange — two source files from different directories with the same file name.
        var files = new[]
        {
            @"C:\Dir1\image.png",
            @"C:\Dir2\image.png"
        };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(2);
        _fileOps.CopiedFiles.Should().HaveCount(2);

        var destinations = _fileOps.CopiedFiles.Select(c => c.Destination).ToList();
        destinations.Should().Contain(@"C:\Dest\image.png");
        destinations.Should().Contain(@"C:\Dest\image_1.png");
    }

    [Fact]
    public async Task ImportResolved_WhenThreeDuplicateFileNamesInBatch_RenamesAllAfterFirst()
    {
        // Arrange
        var files = new[]
        {
            @"C:\Dir1\photo.png",
            @"C:\Dir2\photo.png",
            @"C:\Dir3\photo.png"
        };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(3);
        var destinations = _fileOps.CopiedFiles.Select(c => c.Destination).OrderBy(d => d).ToList();
        destinations.Should().Contain(@"C:\Dest\photo.png");
        destinations.Should().Contain(@"C:\Dest\photo_1.png");
        destinations.Should().Contain(@"C:\Dest\photo_2.png");
    }

    // -------------------------------------------------------------------
    //  Batch tracking prevents rename collisions with non-conflicting copies
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenRenameCollidesWithNonConflictingCopy_AvoidsCollision()
    {
        // Arrange — non-conflicting "img_1.png" is copied first.
        // Then a rename for "img.png" should not generate "img_1" because
        // that name was already used in this batch.
        _fileOps.ExistingFiles.Add(@"C:\Dest\img.png");

        var nonConflicting = new[] { @"C:\Src\img_1.png" };
        var resolution = MakeResolution(
            MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Rename));

        // Act
        var result = await _importer.ImportResolvedAsync(
            nonConflicting, resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(1);
        result.Renamed.Should().Be(1);

        var destinations = _fileOps.CopiedFiles.Select(c => c.Destination).ToList();
        destinations.Should().Contain(@"C:\Dest\img_1.png"); // non-conflicting
        destinations.Should().Contain(@"C:\Dest\img_2.png"); // rename skipped _1 because batch-tracked
    }

    // -------------------------------------------------------------------
    //  Cancelled / null resolution
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenConflictResolutionIsNull_OnlyCopiesNonConflicting()
    {
        // Arrange
        var files = new[] { @"C:\Src\safe.png" };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, conflictResolutions: null, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(1);
        result.Overridden.Should().Be(0);
        result.Renamed.Should().Be(0);
        result.Ignored.Should().Be(0);
    }

    [Fact]
    public async Task ImportResolved_WhenNotConfirmed_OnlyCopiesNonConflicting()
    {
        // Arrange
        var resolution = new FileConflictResolutionResult
        {
            Confirmed = false,
            Conflicts = [MakeConflict("img.png", @"C:\Src\img.png", @"C:\Dest\img.png",
                FileConflictResolution.Override)]
        };

        var files = new[] { @"C:\Src\safe.png" };

        // Act
        var result = await _importer.ImportResolvedAsync(
            files, resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Copied.Should().Be(1);
        result.Overridden.Should().Be(0);
        _fileOps.CopiedFiles.Should().ContainSingle();
    }

    // -------------------------------------------------------------------
    //  ProcessedSourceFiles tracking
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_ProcessedSourceFiles_ContainsOverriddenAndRenamed()
    {
        // Arrange
        _fileOps.ExistingFiles.Add(@"C:\Dest\b.png");

        var resolution = MakeResolution(
            MakeConflict("a.png", @"C:\Src\a.png", @"C:\Dest\a.png",
                FileConflictResolution.Override),
            MakeConflict("b.png", @"C:\Src\b.png", @"C:\Dest\b.png",
                FileConflictResolution.Rename),
            MakeConflict("c.png", @"C:\Src\c.png", @"C:\Dest\c.png",
                FileConflictResolution.Ignore));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert — ignored files should NOT be in processed sources.
        result.ProcessedSourceFiles.Should().HaveCount(2);
        result.ProcessedSourceFiles.Should().Contain(@"C:\Src\a.png");
        result.ProcessedSourceFiles.Should().Contain(@"C:\Src\b.png");
        result.ProcessedSourceFiles.Should().NotContain(@"C:\Src\c.png");
    }

    // -------------------------------------------------------------------
    //  DatasetImportResult
    // -------------------------------------------------------------------

    [Fact]
    public void DatasetImportResult_CancelledResult_HasCancelledTrue()
    {
        var result = DatasetImportResult.CancelledResult();
        result.Cancelled.Should().BeTrue();
        result.TotalAdded.Should().Be(0);
    }

    [Fact]
    public void DatasetImportResult_TotalAdded_SumsCorrectly()
    {
        var result = new DatasetImportResult
        {
            Copied = 2,
            Overridden = 3,
            Renamed = 1,
            Ignored = 5
        };
        result.TotalAdded.Should().Be(6);
    }

    // -------------------------------------------------------------------
    //  Constructor validation
    // -------------------------------------------------------------------

    [Fact]
    public void Constructor_WhenNullFileOperations_ThrowsArgumentNullException()
    {
        var act = () => new DatasetFileImporter(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // -------------------------------------------------------------------
    //  Multiple renames produce distinct names within batch
    // -------------------------------------------------------------------

    [Fact]
    public async Task ImportResolved_WhenMultipleRenamesForDifferentBaseNames_EachGetsUniqueName()
    {
        // Arrange
        _fileOps.ExistingFiles.Add(@"C:\Dest\alpha.png");
        _fileOps.ExistingFiles.Add(@"C:\Dest\beta.png");

        var resolution = MakeResolution(
            MakeConflict("alpha.png", @"C:\Src\alpha.png", @"C:\Dest\alpha.png",
                FileConflictResolution.Rename),
            MakeConflict("beta.png", @"C:\Src\beta.png", @"C:\Dest\beta.png",
                FileConflictResolution.Rename));

        // Act
        var result = await _importer.ImportResolvedAsync(
            [], resolution, DestFolder,
            videoThumbnailService: null, moveFiles: false);

        // Assert
        result.Renamed.Should().Be(2);
        var destinations = _fileOps.CopiedFiles.Select(c => c.Destination).ToList();
        destinations.Should().Contain(@"C:\Dest\alpha_1.png");
        destinations.Should().Contain(@"C:\Dest\beta_1.png");
    }

    // -------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------

    private static FileConflictItem MakeConflict(
        string conflictingName, string newPath, string existingPath,
        FileConflictResolution resolution)
    {
        return new FileConflictItem
        {
            ConflictingName = conflictingName,
            NewFilePath = newPath,
            ExistingFilePath = existingPath,
            Resolution = resolution
        };
    }

    private static FileConflictResolutionResult MakeResolution(
        params FileConflictItem[] conflicts)
    {
        return new FileConflictResolutionResult
        {
            Confirmed = true,
            Conflicts = conflicts.ToList()
        };
    }

    // -------------------------------------------------------------------
    //  Mock IFileOperations — records all operations for assertions.
    // -------------------------------------------------------------------

    private sealed class MockFileOperations : IFileOperations
    {
        public HashSet<string> ExistingFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Source, string Destination, bool Overwrite)> CopiedFiles { get; } = [];
        public List<(string Source, string Destination, bool Overwrite)> MovedFiles { get; } = [];
        public List<string> DeletedFiles { get; } = [];
        public List<string> CreatedDirectories { get; } = [];

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (!overwrite && ExistingFiles.Contains(destinationPath))
            {
                throw new IOException($"The file '{destinationPath}' already exists.");
            }

            CopiedFiles.Add((sourcePath, destinationPath, overwrite));
            ExistingFiles.Add(destinationPath);
        }

        public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
        {
            if (!overwrite && ExistingFiles.Contains(destinationPath))
            {
                throw new IOException($"The file '{destinationPath}' already exists.");
            }

            MovedFiles.Add((sourcePath, destinationPath, overwrite));
            ExistingFiles.Remove(sourcePath);
            ExistingFiles.Add(destinationPath);
        }

        public string[] GetFiles(string directoryPath) => [];

        public void CreateDirectory(string path) => CreatedDirectories.Add(path);

        public void DeleteFile(string path)
        {
            DeletedFiles.Add(path);
            ExistingFiles.Remove(path);
        }
    }
}
