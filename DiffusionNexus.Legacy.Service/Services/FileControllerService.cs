using DiffusionNexus.Legacy.DataAccess.Data;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Services;
using DiffusionNexus.Service.Services.IO;

namespace DiffusionNexus.Legacy.Service;

public class FileControllerService
{
    private readonly IModelMetadataProvider[] _metadataProviders;
    private readonly DiskUtility _diskUtility;
    private readonly DiffusionNexusDbContext? _dbContext;

    public FileControllerService(DiskUtility? diskUtility = null, params IModelMetadataProvider[] metadataProviders)
    {
        _diskUtility = diskUtility ?? new DiskUtility();
        _metadataProviders = metadataProviders ?? Array.Empty<IModelMetadataProvider>();
        _dbContext = null;
    }

    public FileControllerService(params IModelMetadataProvider[] metadataProviders)
        : this(null, metadataProviders)
    {
    }

    public FileControllerService(DiffusionNexusDbContext dbContext, DiskUtility? diskUtility = null, params IModelMetadataProvider[] metadataProviders)
    {
        _dbContext = dbContext;
        _diskUtility = diskUtility ?? new DiskUtility();
        _metadataProviders = metadataProviders ?? Array.Empty<IModelMetadataProvider>();
    }

    private async Task ComputeFolderInternal(IProgress<ProgressReport>? progress, CancellationToken token, SelectedOptions options)
    {
        progress?.Report(new ProgressReport { Percentage = 0, StatusMessage = "Start processing models", LogLevel = LogSeverity.Info });
        token.ThrowIfCancellationRequested();

        var reader = new EnhancedJsonInfoFileReaderService(
            options.BasePath,
            GetModelMetadataWithFallbackAsync,
            _dbContext,
            useDatabaseIfAvailable: _dbContext != null);

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
