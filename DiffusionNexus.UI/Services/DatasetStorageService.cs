using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DiffusionNexus.UI.Services;

public class DatasetStorageService : IDatasetStorageService
{
    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    public string[] GetDirectories(string path) => Directory.GetDirectories(path);

    public IReadOnlyList<string> GetFiles(string path) => Directory.GetFiles(path);

    public IReadOnlyList<string> EnumerateFiles(string path) => Directory.EnumerateFiles(path).ToList();

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive);
        }
    }

    public void DeleteFile(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public void DeleteMediaFiles(string imagePath, string? captionPath, string? thumbnailPath)
    {
        DeleteFile(imagePath);

        if (!string.IsNullOrWhiteSpace(captionPath))
        {
            DeleteFile(captionPath);
        }

        if (!string.IsNullOrWhiteSpace(thumbnailPath))
        {
            DeleteFile(thumbnailPath);
        }
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
        => File.Copy(sourcePath, destinationPath, overwrite);

    public void CopyFileIfExists(string sourcePath, string destinationPath, bool overwrite)
    {
        if (File.Exists(sourcePath) && (overwrite || !File.Exists(destinationPath)))
        {
            File.Copy(sourcePath, destinationPath, overwrite);
        }
    }

    public string GetUniqueFilePath(string folderPath, string fileName)
    {
        var nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        var counter = 1;
        string newPath;

        do
        {
            var newName = $"{nameWithoutExt}_{counter}{extension}";
            newPath = Path.Combine(folderPath, newName);
            counter++;
        } while (File.Exists(newPath));

        return newPath;
    }

    public void EnsureVersionSubfolders(string versionFolderPath)
    {
        var epochsPath = Path.Combine(versionFolderPath, "Epochs");
        var notesPath = Path.Combine(versionFolderPath, "Notes");
        var presentationPath = Path.Combine(versionFolderPath, "Presentation");

        Directory.CreateDirectory(epochsPath);
        Directory.CreateDirectory(notesPath);
        Directory.CreateDirectory(presentationPath);
    }

    public int ExportAsSingleFiles(IEnumerable<DatasetExportItem> files, string destinationFolder)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(destinationFolder);

        var exportedCount = 0;
        Directory.CreateDirectory(destinationFolder);

        foreach (var mediaFile in files)
        {
            if (File.Exists(mediaFile.ImagePath))
            {
                var destMediaPath = Path.Combine(destinationFolder, mediaFile.FileName);
                File.Copy(mediaFile.ImagePath, destMediaPath, overwrite: true);
                exportedCount++;
            }

            if (!string.IsNullOrWhiteSpace(mediaFile.CaptionPath) && File.Exists(mediaFile.CaptionPath))
            {
                var captionFileName = mediaFile.CaptionFileName ?? Path.GetFileName(mediaFile.CaptionPath);
                var destCaptionPath = Path.Combine(destinationFolder, captionFileName);
                File.Copy(mediaFile.CaptionPath, destCaptionPath, overwrite: true);
            }
        }

        return exportedCount;
    }

    public int ExportAsZip(IEnumerable<DatasetExportItem> files, string zipPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(zipPath);

        var exportedCount = 0;

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var mediaFile in files)
            {
                if (File.Exists(mediaFile.ImagePath))
                {
                    archive.CreateEntryFromFile(mediaFile.ImagePath, mediaFile.FileName);
                    exportedCount++;
                }

                if (!string.IsNullOrWhiteSpace(mediaFile.CaptionPath) && File.Exists(mediaFile.CaptionPath))
                {
                    var captionFileName = mediaFile.CaptionFileName ?? Path.GetFileName(mediaFile.CaptionPath);
                    archive.CreateEntryFromFile(mediaFile.CaptionPath, captionFileName);
                }
            }
        }

        return exportedCount;
    }
}
