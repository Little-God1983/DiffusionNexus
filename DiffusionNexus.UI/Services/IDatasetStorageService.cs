using System.Collections.Generic;

namespace DiffusionNexus.UI.Services;

public interface IDatasetStorageService
{
    bool DirectoryExists(string path);
    bool FileExists(string path);
    string[] GetDirectories(string path);
    IReadOnlyList<string> GetFiles(string path);
    IReadOnlyList<string> EnumerateFiles(string path);
    void CreateDirectory(string path);
    void DeleteDirectory(string path, bool recursive);
    void DeleteFile(string path);
    void DeleteMediaFiles(string imagePath, string? captionPath, string? thumbnailPath);
    void CopyFile(string sourcePath, string destinationPath, bool overwrite);
    void CopyFileIfExists(string sourcePath, string destinationPath, bool overwrite);
    string GetUniqueFilePath(string folderPath, string fileName);
    void EnsureVersionSubfolders(string versionFolderPath);
    int ExportAsSingleFiles(IEnumerable<DatasetExportItem> files, string destinationFolder);
    int ExportAsZip(IEnumerable<DatasetExportItem> files, string zipPath);
}
