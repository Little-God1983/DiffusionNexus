using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Handles migration from legacy dataset folder layouts (Epochs/Notes/Presentation at version root)
/// to the new TrainingRuns structure (TrainingRuns/{RunName}/Epochs|Notes|Presentation).
/// </summary>
public static class TrainingRunMigrationUtility
{
    private static readonly string[] OutputFolders = ["Epochs", "Notes", "Presentation", "Release"];
    private const string DefaultRunName = "Default";
    private const string TrainingRunsFolder = "TrainingRuns";

    /// <summary>
    /// Checks whether the version folder uses the legacy layout (output folders directly under version root).
    /// A version folder is legacy if any of the output folders exist with content directly under it.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <returns>True if any legacy output folders exist with content.</returns>
    public static bool IsLegacyLayout(string versionFolderPath)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);

        if (!Directory.Exists(versionFolderPath))
            return false;

        // Legacy if any output folder exists directly under version AND TrainingRuns does not exist
        if (HasTrainingRunsStructure(versionFolderPath))
            return false;

        foreach (var folder in OutputFolders)
        {
            var folderPath = Path.Combine(versionFolderPath, folder);
            if (Directory.Exists(folderPath) && DirectoryHasContent(folderPath))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether the version folder already has the TrainingRuns structure.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <returns>True if a TrainingRuns directory exists under the version folder.</returns>
    public static bool HasTrainingRunsStructure(string versionFolderPath)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);

        var trainingRunsPath = Path.Combine(versionFolderPath, TrainingRunsFolder);
        return Directory.Exists(trainingRunsPath);
    }

    /// <summary>
    /// Migrates the legacy layout to the new TrainingRuns structure.
    /// Moves Epochs/, Notes/, Presentation/, Release/ into TrainingRuns/Default/.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <returns>A <see cref="TrainingRunInfo"/> for the migrated "Default" run, or null if no migration was needed.</returns>
    public static TrainingRunInfo? MigrateLegacyLayout(string versionFolderPath)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);

        if (!IsLegacyLayout(versionFolderPath))
            return null;

        var defaultRunPath = Path.Combine(versionFolderPath, TrainingRunsFolder, DefaultRunName);
        // TODO: Linux Implementation for Training Run folder creation
        Directory.CreateDirectory(defaultRunPath);

        foreach (var folder in OutputFolders)
        {
            var sourcePath = Path.Combine(versionFolderPath, folder);
            if (!Directory.Exists(sourcePath))
                continue;

            var destPath = Path.Combine(defaultRunPath, folder);
            // Move the entire directory
            Directory.Move(sourcePath, destPath);
        }

        return new TrainingRunInfo
        {
            Name = DefaultRunName,
            BaseModel = null,
            CreatedAt = DateTimeOffset.Now,
            Description = "Migrated from legacy layout"
        };
    }

    /// <summary>
    /// Creates a new training run folder under the version's TrainingRuns directory.
    /// Returns the full path to the created run folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <param name="runName">Name of the training run (used as folder name).</param>
    /// <returns>Full path to the created training run folder.</returns>
    public static string CreateTrainingRunFolder(string versionFolderPath, string runName)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);
        if (string.IsNullOrWhiteSpace(runName))
            throw new ArgumentException("Run name cannot be empty.", nameof(runName));

        var runPath = Path.Combine(versionFolderPath, TrainingRunsFolder, runName);
        // TODO: Linux Implementation for Training Run folder creation
        Directory.CreateDirectory(runPath);

        // Create sub-folders
        foreach (var folder in OutputFolders)
        {
            Directory.CreateDirectory(Path.Combine(runPath, folder));
        }

        return runPath;
    }

    /// <summary>
    /// Gets the names of all training runs in a version folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <returns>List of training run folder names, or empty if none exist.</returns>
    public static List<string> GetTrainingRunNames(string versionFolderPath)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);

        var trainingRunsPath = Path.Combine(versionFolderPath, TrainingRunsFolder);
        if (!Directory.Exists(trainingRunsPath))
            return [];

        return Directory.GetDirectories(trainingRunsPath)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .OrderBy(name => name)
            .ToList()!;
    }

    /// <summary>
    /// Gets the full path to a specific training run folder.
    /// </summary>
    /// <param name="versionFolderPath">Path to the version folder (e.g., Dataset/V1).</param>
    /// <param name="runName">Name of the training run.</param>
    /// <returns>Full path to the training run folder.</returns>
    public static string GetTrainingRunPath(string versionFolderPath, string runName)
    {
        ArgumentNullException.ThrowIfNull(versionFolderPath);
        ArgumentNullException.ThrowIfNull(runName);

        return Path.Combine(versionFolderPath, TrainingRunsFolder, runName);
    }

    private static bool DirectoryHasContent(string directoryPath)
    {
        return Directory.EnumerateFileSystemEntries(directoryPath).Any();
    }
}
