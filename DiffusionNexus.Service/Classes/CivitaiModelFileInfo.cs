namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Describes a downloadable file associated with a Civitai model version.
/// </summary>
/// <param name="Name">The file name supplied by Civitai.</param>
/// <param name="Type">The logical type (e.g. "Model").</param>
/// <param name="Format">The file format, if known.</param>
/// <param name="IsPrimary">Indicates whether the file is marked as primary.</param>
/// <param name="DownloadUrl">The absolute URL used to download the file.</param>
/// <param name="SizeBytes">The reported file size in bytes.</param>
/// <param name="Sha256">The SHA256 hash provided by Civitai, if available.</param>
public record CivitaiModelFileInfo(
    string Name,
    string Type,
    string? Format,
    bool IsPrimary,
    string DownloadUrl,
    long? SizeBytes,
    string? Sha256);
