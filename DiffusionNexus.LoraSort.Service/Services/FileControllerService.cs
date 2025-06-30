/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */
using DiffusionNexus.LoraSort.Service.Classes;
using System;
using System.Security.Cryptography;

namespace DiffusionNexus.LoraSort.Service.Services
{
    public class FileControllerService
    {
        private async Task ComputeFolderInternal(IProgress<ProgressReport>? progress, CancellationToken cancellationToken, SelectedOptions options)
        {
            progress?.Report(new ProgressReport
            {
                Percentage = 0,
                StatusMessage = "Start processing LoRA's"
            });
            // Throw if cancellation is requested
            cancellationToken.ThrowIfCancellationRequested();

            var jsonReader = new JsonInfoFileReaderService(options.BasePath, options.ApiKey);
            List<ModelClass> models = await jsonReader.GetModelData(progress, options.BasePath, cancellationToken);

            if (models == null || models.Count == 0)
            {
                // Report error and stop processing.
                progress?.Report(new ProgressReport
                {
                    Percentage = 0,
                    StatusMessage = "No Models in selected folders",
                    IsSuccessful = false
                });
                return;
            }

            var fileCopyService = new FileCopyService();
            // ProcessModelClasses now reports progress and uses our new ProgressReport type.
            progress?.Report(new ProgressReport
            {
                Percentage = 0,
                StatusMessage = "Starting processing copy/paste <=========="
            });

            await Task.Run(() =>
            {
                fileCopyService.ProcessModelClasses(progress, cancellationToken, models, options);
            });

            progress?.Report(new ProgressReport
            {
                Percentage = 100,
                StatusMessage = "==========> Finished processing.",
                IsSuccessful = true
            });
        }

        public async Task ComputeFolder(IProgress<double>? progress, CancellationToken cancellationToken, SelectedOptions options)
        {
            Progress<ProgressReport>? wrapper = progress != null ? new Progress<ProgressReport>(p => progress.Report(p.Percentage ?? 0)) : null;
            await ComputeFolderInternal(wrapper!, cancellationToken, options);
        }

        public async Task ComputeFolder(IProgress<ProgressReport>? progress, CancellationToken cancellationToken, SelectedOptions options)
        {
            await ComputeFolderInternal(progress, cancellationToken, options);
        }

        public bool EnoughFreeSpaceOnDisk(string sourcePath, string targetPath)
        {
            long folderSize = GetDirectorySize(sourcePath);
            long availableSpace = GetAvailableSpace(targetPath);

            return folderSize <= availableSpace;
        }
        // Method to get the size of a directory
        public static long GetDirectorySize(string folderPath)
        {
            if (!Directory.Exists(folderPath))
            {
                throw new DirectoryNotFoundException($"The directory '{folderPath}' does not exist.");
            }

            long size = 0;

            // Get the size of files in the directory and its subdirectories
            foreach (string file in Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories))
            {
                FileInfo fileInfo = new FileInfo(file);
                size += fileInfo.Length;
            }

            return size;
        }

        // Method to get the available space on the drive
        public static long GetAvailableSpace(string folderPath)
        {
            DriveInfo drive = new DriveInfo(Path.GetPathRoot(folderPath));
            return drive.AvailableFreeSpace;
        }
        public string ComputeFileHash(string filePath)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = File.OpenRead(filePath))
                {
                    byte[] hashBytes = md5.ComputeHash(stream);
                    return BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
            }
        }
    }
}
