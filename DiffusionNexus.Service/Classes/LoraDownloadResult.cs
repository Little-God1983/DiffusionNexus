namespace DiffusionNexus.Service.Classes;

public record LoraDownloadResult(
    string FilePath,
    string ModelId,
    string ModelVersionId);
