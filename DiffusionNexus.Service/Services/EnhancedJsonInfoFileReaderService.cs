using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Mapping;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Enhanced reader service that can work with database or legacy file-based approach
/// </summary>
public class EnhancedJsonInfoFileReaderService
{
    private readonly string _basePath;
    private readonly DiffusionNexusDbContext? _context;
    private readonly Func<string, IProgress<ProgressReport>?, CancellationToken, Task<ModelClass>> _metadataFetcher;
    private readonly bool _useDatabaseIfAvailable;

    public EnhancedJsonInfoFileReaderService(
        string basePath,
        Func<string, IProgress<ProgressReport>?, CancellationToken, Task<ModelClass>> metadataFetcher,
        DiffusionNexusDbContext? context = null,
        bool useDatabaseIfAvailable = true)
    {
        _basePath = basePath;
        _metadataFetcher = metadataFetcher;
        _context = context;
        _useDatabaseIfAvailable = useDatabaseIfAvailable;
    }

    public async Task<List<ModelClass>> GetModelData(IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
    {
        if (_useDatabaseIfAvailable && _context != null)
        {
            return await GetModelDataFromDatabase(progress, cancellationToken);
        }
        else
        {
            return await GetModelDataFromFiles(progress, cancellationToken);
        }
    }

    private async Task<List<ModelClass>> GetModelDataFromDatabase(IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            StatusMessage = "Loading models from database...",
            LogLevel = LogSeverity.Info
        });

        var dbModels = await _context!.ModelFiles
            .Where(f => f.LocalFilePath != null && f.LocalFilePath.StartsWith(_basePath))
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.Model)
                    .ThenInclude(m => m.Tags)
            .Include(f => f.ModelVersion)
                .ThenInclude(v => v.TrainedWords)
            .ToListAsync(cancellationToken);

        var modelClasses = new List<ModelClass>();
        foreach (var dbFile in dbModels)
        {
            var modelClass = ModelMapper.ToModelClass(
                dbFile.ModelVersion.Model,
                dbFile.ModelVersion,
                dbFile);
            modelClasses.Add(modelClass);
        }

        progress?.Report(new ProgressReport
        {
            Percentage = 100,
            StatusMessage = $"Loaded {modelClasses.Count} models from database",
            LogLevel = LogSeverity.Success
        });

        return modelClasses;
    }

    private async Task<List<ModelClass>> GetModelDataFromFiles(IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
    {
        var models = JsonInfoFileReaderService.GroupFilesByPrefix(_basePath);
        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            StatusMessage = $"Number of models found: {models.Count}",
            LogLevel = LogSeverity.Info
        });

        foreach (ModelClass model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo? safetensors = model.AssociatedFilesInfo.FirstOrDefault(f => f.Extension == ".safetensors" || f.Extension == ".pt");
            if (safetensors == null)
            {
                model.NoMetaData = true;
                continue;
            }

            try
            {
                progress?.Report(new ProgressReport { StatusMessage = $"Processing metadata for {safetensors.Name}", LogLevel = LogSeverity.Info });
                ModelClass meta = await _metadataFetcher(safetensors.FullName, progress, cancellationToken);
                model.ModelId = meta.ModelId;
                model.DiffusionBaseModel = meta.DiffusionBaseModel;
                model.ModelType = meta.ModelType;
                model.ModelVersionName = string.IsNullOrWhiteSpace(meta.ModelVersionName) ? model.SafeTensorFileName : meta.ModelVersionName;
                model.Tags = meta.Tags;
                model.Nsfw = meta.Nsfw;
                model.TrainedWords = meta.TrainedWords;
                model.CivitaiCategory = MetaDataUtilService.GetCategoryFromTags(model.Tags);
                var completeness = meta.HasFullMetadata ? "complete" : meta.HasAnyMetadata ? "partial" : "none";
                var level = meta.HasFullMetadata ? LogSeverity.Success : LogSeverity.Warning;
                progress?.Report(new ProgressReport { StatusMessage = $"Metadata {completeness} for {safetensors.Name}", LogLevel = level });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving metadata for {Model}", model.SafeTensorFileName);
                model.NoMetaData = true;
            }

            model.NoMetaData = !model.HasAnyMetadata;
        }

        return models;
    }
}
