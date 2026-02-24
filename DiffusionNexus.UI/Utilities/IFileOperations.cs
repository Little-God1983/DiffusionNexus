namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Abstraction over file system operations needed by import/copy workflows.
/// Enables unit testing without touching the real file system.
/// </summary>
public interface IFileOperations
{
    /// <summary>
    /// Determines whether the specified file exists.
    /// </summary>
    bool FileExists(string path);

    /// <summary>
    /// Copies a file to a new location.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="overwrite">Whether to overwrite an existing file at the destination.</param>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Moves a file to a new location, with overwrite support.
    /// Falls back to copy-then-delete when a cross-volume move fails.
    /// </summary>
    /// <param name="sourcePath">The source file path.</param>
    /// <param name="destinationPath">The destination file path.</param>
    /// <param name="overwrite">Whether to overwrite an existing file at the destination.</param>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Gets all file paths in the specified directory.
    /// </summary>
    string[] GetFiles(string directoryPath);

    /// <summary>
    /// Creates the directory if it does not already exist.
    /// </summary>
    void CreateDirectory(string path);

    /// <summary>
    /// Deletes the file at the specified path. Does nothing if the file does not exist.
    /// </summary>
    void DeleteFile(string path);
}
