/*
 * Licensed under the terms found in the LICENSE file in the root directory.
 * For non-commercial use only. See LICENSE for details.
 */
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services.IO;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace DiffusionNexus.Service.Services
{
    public class FileControllerService
    {
        private readonly IModelMetadataProvider[] _metadataProviders;
        private readonly DiskUtility _diskUtility;
        private readonly LoraMetadataDownloadService _metadataDownloader = new LoraMetadataDownloadService(new CivitaiApiClient(new HttpClient()));
        private SelectedOptions? _currentOptions;

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
            _currentOptions = options;
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
            _currentOptions = null;
        }

        public async Task<ModelClass> GetModelMetadataWithFallbackAsync(string identifier, IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
        {
            ModelClass model = new ModelClass();
            foreach (var provider in _metadataProviders)
            {
                progress?.Report(new ProgressReport { StatusMessage = $"Reading metadata using {provider.GetType().Name}", LogLevel = LogSeverity.Info });
                try
                {
                    if (provider is CivitaiApiMetadataProvider)
                    {
                        var name = Path.GetFileNameWithoutExtension(identifier);
                        progress?.Report(new ProgressReport { StatusMessage = $"Invoking Civitai API for {name}", LogLevel = LogSeverity.Info });
                    }

                    model = await provider.GetModelMetadataAsync(identifier, cancellationToken, model);

                    if (provider is CivitaiApiMetadataProvider)
                    {
                        var outcome = model.HasAnyMetadata ? "success" : "no data";
                        progress?.Report(new ProgressReport
                        {
                            StatusMessage = $"Civitai API {outcome} for {model.SafeTensorFileName}",
                            LogLevel = model.HasAnyMetadata ? LogSeverity.Success : LogSeverity.Warning
                        });
                    }
                }
                catch (Exception ex)
                {
                    progress?.Report(new ProgressReport { StatusMessage = $"Provider {provider.GetType().Name} failed: {ex.Message}", LogLevel = LogSeverity.Error });
                    continue;
                }

                if (model.HasFullMetadata)
                {
                    progress?.Report(new ProgressReport { StatusMessage = $"Metadata complete for {model.SafeTensorFileName}", LogLevel = LogSeverity.Success });
                    return model;
                }
                else
                {
                    progress?.Report(new ProgressReport { StatusMessage = $"Metadata incomplete after {provider.GetType().Name} for {model.SafeTensorFileName}", LogLevel = LogSeverity.Warning });
                }
            }
            await SaveMetadataIfRequestedAsync(identifier, model, cancellationToken);
            return model;
        }

        private async Task SaveMetadataIfRequestedAsync(string identifier, ModelClass model, CancellationToken cancellationToken)
        {
            if (_currentOptions?.StoreDownloadedMetadata != true)
                return;

            var folder = Path.GetDirectoryName(identifier);
            var baseName = Path.GetFileNameWithoutExtension(identifier);
            if (folder == null)
                return;

            model.AssociatedFilesInfo = Directory.GetFiles(folder, baseName + ".*")
                                               .Select(f => new FileInfo(f))
                                               .ToList();

            await _metadataDownloader.EnsureMetadataAsync(model, _currentOptions.ApiKey);

            model.AssociatedFilesInfo = Directory.GetFiles(folder, baseName + ".*")
                                               .Select(f => new FileInfo(f))
                                               .ToList();
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
