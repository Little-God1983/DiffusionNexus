namespace DiffusionNexus.Service.Classes;

/// <summary>
/// Reports progress updates during file downloads.
/// </summary>
/// <param name="BytesReceived">Total bytes written to disk so far.</param>
/// <param name="TotalBytes">Total bytes reported by the server, if available.</param>
/// <param name="BytesPerSecond">Current transfer speed in bytes per second, if calculable.</param>
public record ModelDownloadProgress(long BytesReceived, long? TotalBytes, double? BytesPerSecond);
