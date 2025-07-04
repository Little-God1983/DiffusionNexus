using DiffusionNexus.Service.Classes;
using DiffusionNexus.Service.Helper;
using Serilog;

namespace DiffusionNexus.Service.Services;

public class JsonInfoFileReaderService
{
    private readonly string _basePath;
    private readonly ModelMetadataService _metadataService;

    public JsonInfoFileReaderService(string basePath, ModelMetadataService metadataService)
    {
        _basePath = basePath;
        _metadataService = metadataService;
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

        foreach (var model in models)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var safetensors = model.AssociatedFilesInfo.FirstOrDefault(f => f.Extension == ".safetensors");
            if (safetensors == null)
            {
                model.NoMetaData = true;
                continue;
            }

            try
            {
                var meta = await _metadataService.GetModelMetadataAsync(safetensors.FullName, cancellationToken);
                model.DiffusionBaseModel = meta.DiffusionBaseModel;
                model.ModelType = meta.ModelType;
                model.ModelVersionName = string.IsNullOrWhiteSpace(meta.ModelVersionName) ? model.SafeTensorFileName : meta.ModelVersionName;
                model.Tags = meta.Tags;
                model.CivitaiCategory = GetCategoryFromTags(model.Tags);
                model.NoMetaData = meta.NoMetaData;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error retrieving metadata for {Model}", model.SafeTensorFileName);
                model.ErrorOnRetrievingMetaData = true;
                model.NoMetaData = true;
            }
        }

        return models;
    }

    private static CivitaiBaseCategories GetCategoryFromTags(List<string> tags)
    {
        foreach (var tag in tags)
        {
            if (Enum.TryParse(tag.Replace(" ", "_").ToUpper(), out CivitaiBaseCategories category))
            {
                return category;
            }
        }
        return CivitaiBaseCategories.UNKNOWN;
    }

    public static List<ModelClass> GroupFilesByPrefix(string rootDirectory)
    {
        var fileGroups = new Dictionary<string, List<FileInfo>>();
        string[] files = Directory.GetFiles(rootDirectory, "*", SearchOption.AllDirectories);

        foreach (var filePath in files)
        {
            var fileInfo = new FileInfo(filePath);
            var prefix = ExtractBaseName(fileInfo.Name).ToLower();

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
            if (model.AssociatedFilesInfo.Count <= 1)
                model.NoMetaData = true;
            else
                model.NoMetaData = !model.HasAnyMetadata;

            modelClasses.Add(model);
        }

        return modelClasses;
    }

    private static string ExtractBaseName(string fileName)
    {
        var extension = StaticFileTypes.GeneralExtensions
            .OrderByDescending(e => e.Length)
            .FirstOrDefault(e => fileName.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        if (extension != null)
        {
            return fileName.Substring(0, fileName.Length - extension.Length);
        }

        return fileName;
    }
}

