/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services.IO;

namespace DiffusionNexus.Service.Services
{
    public class FileControllerService
    {
        private readonly IModelMetadataProvider[] _metadataProviders;
        private readonly DiskUtility _diskUtility;

        public FileControllerService(DiskUtility? diskUtility = null, params IModelMetadataProvider[] metadataProviders)
        {
            _diskUtility = diskUtility ?? new DiskUtility();
            _metadataProviders = metadataProviders ?? Array.Empty<IModelMetadataProvider>();
        }

        public FileControllerService(params IModelMetadataProvider[] metadataProviders)
            : this(null, metadataProviders)
        {
        }

        private async Task ComputeFolderInternal(IProgress<ProgressReport>? progress, CancellationToken token, SelectedOptions options)
        {
            progress?.Report(new ProgressReport { Percentage = 0, StatusMessage = "Start processing LoRA's", LogLevel = LogSeverity.Info });
            token.ThrowIfCancellationRequested();

            var reader = new JsonInfoFileReaderService(options.BasePath, GetModelMetadataWithFallbackAsync);
            var models = await reader.GetModelData(progress, token);

            if (models == null || models.Count == 0)
            {
                progress?.Report(new ProgressReport { Percentage = 0, StatusMessage = "No Models in selected folders", IsSuccessful = false, LogLevel = LogSeverity.Error });
                return;
            }

            progress?.Report(new ProgressReport { Percentage = 0, StatusMessage = "Starting processing copy/paste <==========", LogLevel = LogSeverity.Info });
            await Task.Run(() => new FileCopyService().ProcessModelClasses(progress, token, models, options));
            progress?.Report(new ProgressReport { Percentage = 100, StatusMessage = "==========> Finished processing. To close the log click the upper right corner", IsSuccessful = true, LogLevel = LogSeverity.Info });
        }

        public async Task<ModelClass> GetModelMetadataWithFallbackAsync(string identifier, CancellationToken cancellationToken)
        {
            ModelClass model = new ModelClass();
            foreach (var provider in _metadataProviders)
            {
                model = await provider.GetModelMetadataAsync(identifier, cancellationToken, model);
                if (model.HasFullMetadata)
                    return model;
            }
            return model;
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

        public bool EnoughFreeSpaceOnDisk(string sourcePath, string targetPath) =>
            _diskUtility.EnoughFreeSpace(sourcePath, targetPath);

        public string ComputeFileHash(string filePath)
        {
            var hashing = new HashingService();
            return hashing.ComputeFileHash(filePath);
        }

        public Task DeleteEmptyDirectoriesAsync(string path) =>
            _diskUtility.DeleteEmptyDirectoriesAsync(path);
    }
}
