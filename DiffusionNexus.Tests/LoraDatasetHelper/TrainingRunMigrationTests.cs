using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper;

/// <summary>
/// Unit tests for <see cref="TrainingRunMigrationUtility"/>.
/// Tests legacy layout detection, migration, run folder creation, and discovery.
/// </summary>
public class TrainingRunMigrationTests : IDisposable
{
    private readonly string _testTempPath;

    public TrainingRunMigrationTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"TrainingRunMigrationTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }

    #region IsLegacyLayout Tests

    [Fact]
    public void WhenVersionFolderDoesNotExistThenIsLegacyLayoutReturnsFalse()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_testTempPath, "DoesNotExist");

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(nonExistentPath);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void WhenVersionFolderIsEmptyThenIsLegacyLayoutReturnsFalse()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(versionPath);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void WhenEpochsFolderExistsWithContentThenIsLegacyLayoutReturnsTrue()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);
        File.WriteAllText(Path.Combine(epochsPath, "model-e10.safetensors"), "dummy");

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(versionPath);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void WhenNotesFolderExistsWithContentThenIsLegacyLayoutReturnsTrue()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);
        File.WriteAllText(Path.Combine(notesPath, "training-log.txt"), "dummy");

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(versionPath);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void WhenOutputFolderExistsButIsEmptyThenIsLegacyLayoutReturnsFalse()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "Epochs"));
        Directory.CreateDirectory(Path.Combine(versionPath, "Notes"));

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(versionPath);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void WhenTrainingRunsStructureExistsThenIsLegacyLayoutReturnsFalse()
    {
        // Arrange — even if Epochs exists with content, once TrainingRuns exists it's not legacy
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns", "Default"));
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);
        File.WriteAllText(Path.Combine(epochsPath, "model.safetensors"), "dummy");

        // Act
        var result = TrainingRunMigrationUtility.IsLegacyLayout(versionPath);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void WhenNullPathThenIsLegacyLayoutThrowsArgumentNullException()
    {
        // Act
        var act = () => TrainingRunMigrationUtility.IsLegacyLayout(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region HasTrainingRunsStructure Tests

    [Fact]
    public void WhenTrainingRunsFolderExistsThenHasTrainingRunsStructureReturnsTrue()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns"));

        // Act
        var result = TrainingRunMigrationUtility.HasTrainingRunsStructure(versionPath);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void WhenNoTrainingRunsFolderThenHasTrainingRunsStructureReturnsFalse()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var result = TrainingRunMigrationUtility.HasTrainingRunsStructure(versionPath);

        // Assert
        result.Should().BeFalse();
    }

    #endregion

    #region MigrateLegacyLayout Tests

    [Fact]
    public void WhenLegacyLayoutExistsThenMigrateLegacyLayoutMovesOutputFolders()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);
        File.WriteAllText(Path.Combine(epochsPath, "model-e10.safetensors"), "epoch data");

        var notesPath = Path.Combine(versionPath, "Notes");
        Directory.CreateDirectory(notesPath);
        File.WriteAllText(Path.Combine(notesPath, "log.txt"), "training log");

        var presentationPath = Path.Combine(versionPath, "Presentation");
        Directory.CreateDirectory(presentationPath);
        File.WriteAllText(Path.Combine(presentationPath, "sample.png"), "image data");

        // Act
        var result = TrainingRunMigrationUtility.MigrateLegacyLayout(versionPath);

        // Assert
        result.Should().NotBeNull();
        result!.Name.Should().Be("Default");
        result.Description.Should().Be("Migrated from legacy layout");

        // Legacy folders should be moved
        Directory.Exists(epochsPath).Should().BeFalse("Epochs should be moved");
        Directory.Exists(notesPath).Should().BeFalse("Notes should be moved");
        Directory.Exists(presentationPath).Should().BeFalse("Presentation should be moved");

        // New structure should exist
        var defaultRunPath = Path.Combine(versionPath, "TrainingRuns", "Default");
        Directory.Exists(defaultRunPath).Should().BeTrue();
        File.Exists(Path.Combine(defaultRunPath, "Epochs", "model-e10.safetensors")).Should().BeTrue();
        File.Exists(Path.Combine(defaultRunPath, "Notes", "log.txt")).Should().BeTrue();
        File.Exists(Path.Combine(defaultRunPath, "Presentation", "sample.png")).Should().BeTrue();
    }

    [Fact]
    public void WhenNoLegacyLayoutThenMigrateLegacyLayoutReturnsNull()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var result = TrainingRunMigrationUtility.MigrateLegacyLayout(versionPath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void WhenAlreadyMigratedThenMigrateLegacyLayoutReturnsNull()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns", "Default", "Epochs"));

        // Act
        var result = TrainingRunMigrationUtility.MigrateLegacyLayout(versionPath);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void WhenOnlySomeLegacyFoldersExistThenMigrateLegacyLayoutMovesOnlyExistingOnes()
    {
        // Arrange — only Epochs exists, no Notes/Presentation/Release
        var versionPath = Path.Combine(_testTempPath, "V1");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);
        File.WriteAllText(Path.Combine(epochsPath, "model.safetensors"), "data");

        // Act
        var result = TrainingRunMigrationUtility.MigrateLegacyLayout(versionPath);

        // Assert
        result.Should().NotBeNull();
        var defaultRunPath = Path.Combine(versionPath, "TrainingRuns", "Default");
        Directory.Exists(Path.Combine(defaultRunPath, "Epochs")).Should().BeTrue();
        File.Exists(Path.Combine(defaultRunPath, "Epochs", "model.safetensors")).Should().BeTrue();
    }

    #endregion

    #region CreateTrainingRunFolder Tests

    [Fact]
    public void WhenCreateTrainingRunFolderCalledThenCreatesRunWithSubfolders()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var runPath = TrainingRunMigrationUtility.CreateTrainingRunFolder(versionPath, "SDXL_MyLoRA");

        // Assert
        runPath.Should().EndWith(Path.Combine("TrainingRuns", "SDXL_MyLoRA"));
        Directory.Exists(runPath).Should().BeTrue();
        Directory.Exists(Path.Combine(runPath, "Epochs")).Should().BeTrue();
        Directory.Exists(Path.Combine(runPath, "Notes")).Should().BeTrue();
        Directory.Exists(Path.Combine(runPath, "Presentation")).Should().BeTrue();
        Directory.Exists(Path.Combine(runPath, "Release")).Should().BeTrue();
    }

    [Fact]
    public void WhenEmptyRunNameThenCreateTrainingRunFolderThrowsArgumentException()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var act = () => TrainingRunMigrationUtility.CreateTrainingRunFolder(versionPath, "  ");

        // Assert
        act.Should().Throw<ArgumentException>()
            .WithParameterName("runName");
    }

    [Fact]
    public void WhenNullVersionPathThenCreateTrainingRunFolderThrowsArgumentNullException()
    {
        // Act
        var act = () => TrainingRunMigrationUtility.CreateTrainingRunFolder(null!, "MyRun");

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region GetTrainingRunNames Tests

    [Fact]
    public void WhenMultipleRunsExistThenGetTrainingRunNamesReturnsAllSorted()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns", "Flux_Run"));
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns", "SDXL_Run"));
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns", "Default"));

        // Act
        var names = TrainingRunMigrationUtility.GetTrainingRunNames(versionPath);

        // Assert
        names.Should().HaveCount(3);
        names.Should().BeInAscendingOrder();
        names.Should().Contain("Default");
        names.Should().Contain("Flux_Run");
        names.Should().Contain("SDXL_Run");
    }

    [Fact]
    public void WhenNoTrainingRunsFolderThenGetTrainingRunNamesReturnsEmptyList()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        var names = TrainingRunMigrationUtility.GetTrainingRunNames(versionPath);

        // Assert
        names.Should().BeEmpty();
    }

    [Fact]
    public void WhenTrainingRunsFolderIsEmptyThenGetTrainingRunNamesReturnsEmptyList()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(Path.Combine(versionPath, "TrainingRuns"));

        // Act
        var names = TrainingRunMigrationUtility.GetTrainingRunNames(versionPath);

        // Assert
        names.Should().BeEmpty();
    }

    #endregion

    #region GetTrainingRunPath Tests

    [Fact]
    public void WhenGetTrainingRunPathCalledThenReturnsExpectedPath()
    {
        // Arrange
        var versionPath = Path.Combine(_testTempPath, "V1");

        // Act
        var runPath = TrainingRunMigrationUtility.GetTrainingRunPath(versionPath, "SDXL_Run");

        // Assert
        runPath.Should().Be(Path.Combine(versionPath, "TrainingRuns", "SDXL_Run"));
    }

    [Fact]
    public void WhenNullRunNameThenGetTrainingRunPathThrowsArgumentNullException()
    {
        // Act
        var act = () => TrainingRunMigrationUtility.GetTrainingRunPath(_testTempPath, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
