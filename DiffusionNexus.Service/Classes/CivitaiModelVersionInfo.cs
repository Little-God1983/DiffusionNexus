using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Represents the metadata for a specific Civitai model version.
/// </summary>
public class CivitaiModelVersionInfo : CivitaiModelMetadataBase
{
    public CivitaiModelVersionInfo(
        int modelId,
        int versionId,
        string versionName,
        string? baseModel,
        string? modelType,
        IReadOnlyList<string> trainedWords,
        IReadOnlyList<CivitaiModelFileInfo> files,
        string? description)
    {
        ModelId = modelId;
        VersionId = versionId;
        ModelVersionName = versionName;
        BaseModel = baseModel;
        ModelType = modelType;
        Description = description;
        MutableTrainedWords = trainedWords?.ToList() ?? new List<string>();
        Files = files?.ToList() ?? new List<CivitaiModelFileInfo>();
    }

    /// <summary>
    /// Identifier of the parent model.
    /// </summary>
    public int ModelId { get; }

    /// <summary>
    /// Identifier of the specific model version.
    /// </summary>
    public int VersionId { get; }

    /// <summary>
    /// Convenience alias matching the original record API.
    /// </summary>
    public string VersionName => ModelVersionName;

    /// <summary>
    /// Classification of the asset as reported by Civitai.
    /// </summary>
    public string? ModelType { get; }

    /// <summary>
    /// Files published for this model version.
    /// </summary>
    public IReadOnlyList<CivitaiModelFileInfo> Files { get; }
}
