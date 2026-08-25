namespace DiffusionNexus.Domain.Enums;

/// <summary>
/// Extension → <see cref="FileFormat"/>. Was copied verbatim in LoraDownloadService,
/// ModelDetailViewModel and ModelFileSyncService; single implementation now.
/// </summary>
public static class FileFormatMapper
{
    public static FileFormat FromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".safetensors" => FileFormat.SafeTensor,
        ".pt" => FileFormat.PickleTensor,
        ".ckpt" => FileFormat.Other,
        ".pth" => FileFormat.PickleTensor,
        _ => FileFormat.Unknown
    };
}
