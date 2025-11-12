namespace DiffusionNexus.Service.Services;

/// <summary>
/// Represents a progress update for a download operation.
/// </summary>
/// <param name="BytesReceived">The cumulative number of bytes written to disk.</param>
/// <param name="TotalBytes">Optional total size reported by the server.</param>
/// <param name="Percentage">Optional percentage from 0-100.</param>
/// <param name="SpeedMbps">Current download speed in megabytes per second.</param>
/// <param name="EstimatedRemaining">Estimated time remaining.</param>
public readonly record struct LoraDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    double? Percentage,
    double SpeedMbps,
    TimeSpan? EstimatedRemaining
);
