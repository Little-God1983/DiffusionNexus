namespace DiffusionNexus.Service.Classes;

public record CivitaiModelInfo(
    string ModelId,
    string ModelVersionId,
    string FileName,
    string DownloadUrl,
    string VersionJson,
    string ModelJson,
    string? PreviewImageUrl);
