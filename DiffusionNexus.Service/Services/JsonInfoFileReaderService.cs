using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using ModelMover.Core.Metadata;
using Serilog;

namespace DiffusionNexus.Service.Services;

public class JsonInfoFileReaderService
{
    private readonly string _basePath;
    private readonly Func<string, IProgress<ProgressReport>?, CancellationToken, Task<ModelClass>> _metadataFetcher;

    public JsonInfoFileReaderService(string basePath, Func<string, IProgress<ProgressReport>?, CancellationToken, Task<ModelClass>> metadataFetcher)
    {
        _basePath = basePath;
        _metadataFetcher = metadataFetcher;
    }

    public async Task<List<ModelClass>> GetModelData(IProgress<ProgressReport>? progress, CancellationToken cancellationToken)
    {
        var models = GroupFilesByPrefix(_basePath);
        progress?.Report(new ProgressReport
        {
            Percentage = 0,
            StatusMessage = $"Number of LoRa's found: {models.Count}",
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
                model.DiffusionBaseModel = meta.DiffusionBaseModel;
                model.ModelType = meta.ModelType;
                model.ModelVersionName = string.IsNullOrWhiteSpace(meta.ModelVersionName) ? model.SafeTensorFileName : meta.ModelVersionName;
                model.Tags = meta.Tags;
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



    public static List<ModelClass> GroupFilesByPrefix(string rootDirectory)
    {
        var fileGroups = new Dictionary<string, List<FileInfo>>();
        string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            var fileInfo = new FileInfo(filePath);
            var prefix = ModelMetadataUtils.ExtractBaseName(fileInfo.Name).ToLower();

            if (!fileGroups.ContainsKey(prefix))
            {
                fileGroups[prefix] = new List<FileInfo>();
            }
            fileGroups[prefix].Add(fileInfo);
        }

        var modelClasses = new List<ModelClass>();
        foreach (var group in fileGroups)
        {
            if (!group.Value.Any(f => StaticFileTypes.ModelExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var model = new ModelClass
            {
                SafeTensorFileName = group.Key,
                AssociatedFilesInfo = group.Value,
                CivitaiCategory = CivitaiBaseCategories.UNKNOWN
            };
            model.NoMetaData = !model.HasAnyMetadata;
            modelClasses.Add(model);
        }

        return modelClasses;
    }

}

