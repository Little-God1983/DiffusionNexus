namespace DiffusionNexus.Domain.Services;

public sealed record ImageTagScore(string Name, float Confidence);

public sealed record ImageTagResult(
    bool Success,
    IReadOnlyList<ImageTagScore> Tags,
    string? RatingLabel,
    float RatingScore,
    bool IsNsfw,
    string? ErrorMessage = null)
{
    public static ImageTagResult Succeeded(IReadOnlyList<ImageTagScore> tags, string ratingLabel, float ratingScore, bool isNsfw) =>
        new(true, tags, ratingLabel, ratingScore, isNsfw);

    public static ImageTagResult Failed(string error) =>
        new(false, Array.Empty<ImageTagScore>(), null, 0f, false, error);
}

/// <summary>
/// Tags images with booru-style content tags and a content rating using the
/// WD14 tagger ONNX model. Runs inference locally, GPU-accelerated when
/// available with CPU fallback — mirrors <see cref="IBackgroundRemovalService"/>.
/// </summary>
public interface IImageTaggingService : IDisposable
{
    ModelStatus GetModelStatus();

    Task<bool> DownloadModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> InitializeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Tags an image. <paramref name="tagConfidenceThreshold"/> filters the
    /// general/character tags returned; the rating is always returned
    /// regardless of threshold (a single argmax over a small closed set).
    /// </summary>
    Task<ImageTagResult> TagImageAsync(
        byte[] imageData,
        int width,
        int height,
        float tagConfidenceThreshold = 0.35f,
        CancellationToken cancellationToken = default);

    bool IsGpuAvailable { get; }
    bool IsProcessing { get; }
}
