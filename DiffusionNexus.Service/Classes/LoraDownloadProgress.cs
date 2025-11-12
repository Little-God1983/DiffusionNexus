namespace DiffusionNexus.Service.Classes;

public record LoraDownloadProgress(long BytesReceived, long? TotalBytes, double? BytesPerSecond)
{
    public double? Percentage => TotalBytes.HasValue && TotalBytes.Value > 0
        ? BytesReceived * 100d / TotalBytes.Value
        : null;
}
