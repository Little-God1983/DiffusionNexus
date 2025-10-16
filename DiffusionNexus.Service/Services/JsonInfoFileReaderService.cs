using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using ModelMover.Core.Metadata;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

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
                model.ModelId = meta.ModelId;
                model.ModelVersionId = meta.ModelVersionId;
                model.DiffusionBaseModel = meta.DiffusionBaseModel;
                model.ModelType = meta.ModelType;
                model.ModelVersionName = string.IsNullOrWhiteSpace(meta.ModelVersionName) ? model.SafeTensorFileName : meta.ModelVersionName;
                model.Tags = meta.Tags;
                model.Nsfw = meta.Nsfw;
                model.TrainedWords = meta.TrainedWords;
                model.Description = meta.Description;
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
        // Key files by directory + prefix so that identical model names in different folders
        // are treated as separate entries. Previously, only the filename prefix was used, which
        // caused models with the same name in different directories to be merged and displayed
        // only once.
        var fileGroups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            var fileInfo = new FileInfo(filePath);
            var prefix = ModelMetadataUtils.ExtractBaseName(fileInfo.Name);
            var dir = fileInfo.DirectoryName ?? string.Empty;
            var key = Path.Combine(dir, prefix);

            if (!fileGroups.TryGetValue(key, out var list))
            {
                list = new List<FileInfo>();
                fileGroups[key] = list;
            }
            list.Add(fileInfo);
        }

        var modelClasses = new List<ModelClass>();
        foreach (var group in fileGroups)
        {
            if (!group.Value.Any(f => StaticFileTypes.ModelExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            var modelFile = group.Value.FirstOrDefault(f =>
                StaticFileTypes.ModelExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
            var baseName = modelFile != null
                ? ModelMetadataUtils.ExtractBaseName(modelFile.Name)
                : Path.GetFileName(group.Key);

            var model = new ModelClass
            {
                SafeTensorFileName = baseName,
                AssociatedFilesInfo = group.Value,
                CivitaiCategory = CivitaiBaseCategories.UNKNOWN
            };
            model.NoMetaData = !model.HasAnyMetadata;
            modelClasses.Add(model);
        }

        return modelClasses;
    }

}

