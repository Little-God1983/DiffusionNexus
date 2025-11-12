namespace DiffusionNexus.Service.Classes;

public enum ModelDownloadResultType
{
    Success,
    AlreadyExists,
    Cancelled,
    Error
}

public record CivitaiModelDownloadResult(
    ModelDownloadResultType ResultType,
    string? FilePath = null,
    CivitaiModelVersionInfo? VersionInfo = null,
    CivitaiModelFileInfo? FileInfo = null,
    string? ErrorMessage = null);
