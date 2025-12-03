using DiffusionNexus.DataAccess.Entities;
using DiffusionNexus.Service.Classes;

namespace DiffusionNexus.Service.Mapping;

/// <summary>
/// Maps between database entities and legacy ModelClass for backward compatibility
/// </summary>
public static class ModelMapper
{
    /// <summary>
    /// Converts a database Model entity to a ModelClass for use in existing UI/services
    /// </summary>
    public static ModelClass ToModelClass(Model dbModel, ModelVersion? version = null, ModelFile? primaryFile = null)
    {
        version ??= dbModel.Versions.OrderByDescending(v => v.PublishedAt).FirstOrDefault();
        primaryFile ??= version?.Files.FirstOrDefault(f => f.IsPrimary) ?? version?.Files.FirstOrDefault();

        var modelClass = new ModelClass
        {
            ModelId = dbModel.CivitaiModelId,
            ModelVersionName = version?.Name ?? dbModel.Name,
            DiffusionBaseModel = version?.BaseModel ?? "UNKNOWN",
            ModelType = ParseModelType(dbModel.Type),
            Nsfw = dbModel.Nsfw,
            Tags = dbModel.Tags.Select(t => t.Tag).ToList(),
            CivitaiCategory = GetCategoryFromTags(dbModel.Tags.Select(t => t.Tag).ToList()),
            SHA256Hash = primaryFile?.SHA256Hash
        };

        if (primaryFile != null)
        {
            modelClass.SafeTensorFileName = Path.GetFileNameWithoutExtension(primaryFile.LocalFilePath ?? primaryFile.Name);
            
            if (!string.IsNullOrEmpty(primaryFile.LocalFilePath))
            {
                var directory = Path.GetDirectoryName(primaryFile.LocalFilePath);
                if (directory != null)
                {
                    modelClass.AssociatedFilesInfo = GetAssociatedFiles(directory, modelClass.SafeTensorFileName);
                }
            }
        }

        if (version != null)
        {
            modelClass.TrainedWords = version.TrainedWords.Select(w => w.Word).ToList();
        }

        modelClass.NoMetaData = !modelClass.HasAnyMetadata;

        return modelClass;
    }

    /// <summary>
    /// Converts multiple database models to ModelClass list
    /// </summary>
    public static List<ModelClass> ToModelClassList(IEnumerable<Model> dbModels)
    {
        return dbModels.Select(m => ToModelClass(m)).ToList();
    }

    /// <summary>
    /// Converts a ModelClass back to database entities (for import scenarios)
    /// </summary>
    public static (Model model, ModelVersion version, ModelFile? file) FromModelClass(ModelClass modelClass, string? localFilePath = null)
    {
        var model = new Model
        {
            CivitaiModelId = modelClass.ModelId ?? Guid.NewGuid().ToString(),
            Name = modelClass.ModelVersionName,
            Type = modelClass.ModelType.ToString(),
            Nsfw = modelClass.Nsfw ?? false,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var tag in modelClass.Tags)
        {
            model.Tags.Add(new ModelTag { Tag = tag });
        }

        var version = new ModelVersion
        {
            CivitaiVersionId = modelClass.ModelId ?? Guid.NewGuid().ToString(),
            Name = modelClass.ModelVersionName,
            BaseModel = modelClass.DiffusionBaseModel,
            CreatedAt = DateTime.UtcNow
        };

        foreach (var word in modelClass.TrainedWords)
        {
            version.TrainedWords.Add(new TrainedWord { Word = word });
        }

        ModelFile? file = null;
        if (!string.IsNullOrEmpty(localFilePath) || !string.IsNullOrEmpty(modelClass.SafeTensorFileName))
        {
            file = new ModelFile
            {
                CivitaiFileId = Guid.NewGuid().ToString(),
                Name = modelClass.SafeTensorFileName + ".safetensors",
                LocalFilePath = localFilePath,
                SHA256Hash = modelClass.SHA256Hash,
                IsPrimary = true,
                Type = "Model"
            };
            version.Files.Add(file);
        }

        model.Versions.Add(version);

        return (model, version, file);
    }

    private static List<FileInfo> GetAssociatedFiles(string directory, string baseName)
    {
        if (!Directory.Exists(directory))
            return new List<FileInfo>();

        return Directory.GetFiles(directory, $"{baseName}*")
            .Select(f => new FileInfo(f))
            .ToList();
    }

    private static DiffusionTypes ParseModelType(string? typeToken)
    {
        if (string.IsNullOrWhiteSpace(typeToken))
            return DiffusionTypes.UNASSIGNED;
        return Enum.TryParse(typeToken.Replace(" ", string.Empty), true, out DiffusionTypes dt)
            ? dt
            : DiffusionTypes.UNASSIGNED;
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
}
