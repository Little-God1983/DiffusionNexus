using System.Collections.Generic;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Provides an abstraction over file system operations related to datasets,
/// including directory and file management, media cleanup, and export helpers.
/// </summary>
/// <remarks>
/// Implementations of this interface should encapsulate all dataset storage
/// concerns so that higher-level components do not depend directly on the
/// underlying file system or storage mechanism.
/// </remarks>
public interface IDatasetStorageService
{
    /// <summary>
    /// Determines whether the specified directory exists.
    /// </summary>
    /// <param name="path">The absolute or relative path of the directory to check.</param>
    /// <returns>
    /// <see langword="true"/> if the directory exists; otherwise, <see langword="false"/>.
    /// </returns>
    bool DirectoryExists(string path);

    /// <summary>
    /// Determines whether the specified file exists.
    /// </summary>
    /// <param name="path">The absolute or relative path of the file to check.</param>
    /// <returns>
    /// <see langword="true"/> if the file exists; otherwise, <see langword="false"/>.
    /// </returns>
    bool FileExists(string path);

    /// <summary>
    /// Gets the subdirectories contained within the specified directory.
    /// </summary>
    /// <param name="path">The path of the directory whose subdirectories are to be retrieved.</param>
    /// <returns>
    /// An array of directory paths contained within the specified directory.
    /// </returns>
    string[] GetDirectories(string path);

    /// <summary>
    /// Gets the files contained within the specified directory.
    /// </summary>
    /// <param name="path">The path of the directory whose files are to be retrieved.</param>
    /// <returns>
    /// A read-only list of file paths contained within the specified directory.
    /// </returns>
    IReadOnlyList<string> GetFiles(string path);

    /// <summary>
    /// Lazily enumerates the files contained within the specified directory.
    /// </summary>
    /// <param name="path">The path of the directory whose files are to be enumerated.</param>
    /// <returns>
    /// A read-only list of file paths representing the enumeration result.
    /// </returns>
    IReadOnlyList<string> EnumerateFiles(string path);

    /// <summary>
    /// Creates a directory at the specified path if it does not already exist.
    /// </summary>
    /// <param name="path">The path at which to create the directory.</param>
    void CreateDirectory(string path);

    /// <summary>
    /// Deletes the directory at the specified path.
    /// </summary>
    /// <param name="path">The path of the directory to delete.</param>
    /// <param name="recursive">
    /// <see langword="true"/> to delete all subdirectories and files within the directory;
    /// <see langword="false"/> to delete only an empty directory.
    /// </param>
    void DeleteDirectory(string path, bool recursive);

    /// <summary>
    /// Deletes the file at the specified path.
    /// </summary>
    /// <param name="path">The path of the file to delete.</param>
    void DeleteFile(string path);

    /// <summary>
    /// Deletes related media files for a dataset item, such as the image, caption, and thumbnail.
    /// </summary>
    /// <param name="imagePath">The path to the image file to delete.</param>
    /// <param name="captionPath">The path to the caption file to delete, or <see langword="null"/> if none.</param>
    /// <param name="thumbnailPath">The path to the thumbnail file to delete, or <see langword="null"/> if none.</param>
    void DeleteMediaFiles(string imagePath, string? captionPath, string? thumbnailPath);

    /// <summary>
    /// Copies a file to a new location.
    /// </summary>
    /// <param name="sourcePath">The path of the file to copy.</param>
    /// <param name="destinationPath">The destination path for the copied file.</param>
    /// <param name="overwrite">
    /// <see langword="true"/> to overwrite an existing file at the destination path;
    /// <see langword="false"/> to throw if the destination file already exists.
    /// </param>
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Copies a file to a new location if the source file exists.
    /// </summary>
    /// <param name="sourcePath">The path of the file to copy, if it exists.</param>
    /// <param name="destinationPath">The destination path for the copied file.</param>
    /// <param name="overwrite">
    /// <see langword="true"/> to overwrite an existing file at the destination path;
    /// <see langword="false"/> to skip copying if the destination file already exists.
    /// </param>
    void CopyFileIfExists(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Generates a unique file path within the specified folder for the given file name.
    /// </summary>
    /// <param name="folderPath">The target folder in which the file will be created.</param>
    /// <param name="fileName">The desired file name, without any uniqueness suffix.</param>
    /// <returns>
    /// A file path that does not currently exist within the specified folder.
    /// </returns>
    string GetUniqueFilePath(string folderPath, string fileName);

    /// <summary>
    /// Ensures that any required version-specific subfolders exist under the given folder path.
    /// </summary>
    /// <param name="versionFolderPath">The root folder for versioned dataset content.</param>
    void EnsureVersionSubfolders(string versionFolderPath);

    /// <summary>
    /// Exports the specified dataset items as individual files into the given destination folder.
    /// </summary>
    /// <param name="files">The collection of dataset items to export.</param>
    /// <param name="destinationFolder">The target folder where the files will be created.</param>
    /// <returns>
    /// The number of dataset items successfully exported.
    /// </returns>
    /// <example>
    /// <code>
    /// var count = storageService.ExportAsSingleFiles(items, exportFolder);
    /// </code>
    /// </example>
    int ExportAsSingleFiles(IEnumerable<DatasetExportItem> files, string destinationFolder);

    /// <summary>
    /// Exports the specified dataset items into a single ZIP archive.
    /// </summary>
    /// <param name="files">The collection of dataset items to include in the archive.</param>
    /// <param name="zipPath">The full path of the ZIP file to create.</param>
    /// <returns>
    /// The number of dataset items successfully added to the archive.
    /// </returns>
    /// <example>
    /// <code>
    /// var count = storageService.ExportAsZip(items, zipFilePath);
    /// </code>
    /// </example>
    int ExportAsZip(IEnumerable<DatasetExportItem> files, string zipPath);
}
