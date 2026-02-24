namespace DiffusionNexus.UI.Utilities;

/// <summary>
/// Default implementation of <see cref="IFileOperations"/> that delegates to <see cref="System.IO"/>.
/// </summary>
public sealed class FileOperations : IFileOperations
{
    /// <inheritdoc />
    public bool FileExists(string path) => File.Exists(path);

    /// <inheritdoc />
    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    /// <inheritdoc />
    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        try
        {
            File.Move(sourcePath, destinationPath, overwrite);
        }
        catch (IOException)
        {
            // Cross-volume moves are not supported by File.Move; fall back to copy + delete.
            File.Copy(sourcePath, destinationPath, overwrite);
            File.Delete(sourcePath);
        }
    }

    /// <inheritdoc />
    public string[] GetFiles(string directoryPath) => Directory.GetFiles(directoryPath);

    /// <inheritdoc />
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    /// <inheritdoc />
    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
