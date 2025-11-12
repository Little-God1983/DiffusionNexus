namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Represents the metadata for a specific Civitai model version.
/// </summary>
public record CivitaiModelVersionInfo(
    int ModelId,
    int VersionId,
    string VersionName,
    string? BaseModel,
    string? ModelType,
    IReadOnlyList<string> TrainedWords,
    IReadOnlyList<CivitaiModelFileInfo> Files,
    string? Description);
