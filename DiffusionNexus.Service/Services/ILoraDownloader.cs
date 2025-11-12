using System;
using System.Threading;
using System.Threading.Tasks;

namespace DiffusionNexus.Service.Services;

public interface ILoraDownloader
{
    Task<LoraDownloadResult> DownloadAsync(LoraDownloadRequest request, IProgress<LoraDownloadProgress>? progress, CancellationToken cancellationToken);
}

public record LoraDownloadRequest
{
    public required int ModelId { get; init; }
    public int? ModelVersionId { get; init; }
    public required string TargetDirectory { get; init; }
    public string? ApiKey { get; init; }
    public Func<LoraDownloadConflictContext, Task<LoraDownloadConflictResolution>>? ConflictResolver { get; init; }
}

public record LoraDownloadProgress(long BytesReceived, long? TotalBytes);

public enum LoraDownloadResultStatus
{
    Completed,
    Skipped
}

public record LoraDownloadResult(LoraDownloadResultStatus Status, string? FilePath = null);

public enum LoraDownloadConflictResolutionType
{
    Overwrite,
    Skip,
    Rename
}

public record LoraDownloadConflictContext(string ExistingFilePath, string SuggestedFileName, long? ExistingFileSize);

public record LoraDownloadConflictResolution(LoraDownloadConflictResolutionType Type, string? FileName = null);
