namespace DiffusionNexus.Service.Classes;

public enum MetadataDownloadResultType
{
    AlreadyExists,
    Downloaded,
    NotFound,
    Error
}

public record MetadataDownloadResult(MetadataDownloadResultType ResultType, string? ModelId = null, string? ErrorMessage = null);
