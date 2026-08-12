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
    /// Tags the image at <paramref name="imagePath"/>. The service decodes the
    /// file itself (the model only ever sees a ~448² letterboxed square, so
    /// shipping a full-resolution pixel buffer across this interface would be
    /// pure allocation churn). <paramref name="tagConfidenceThreshold"/>
    /// filters the general/character tags returned; the rating is always
    /// returned regardless of threshold (a single argmax over a small closed
    /// set). Decode failures are reported as a failed result, not thrown.
    /// </summary>
    Task<ImageTagResult> TagImageAsync(
        string imagePath,
        float tagConfidenceThreshold = 0.35f,
        CancellationToken cancellationToken = default);

    bool IsGpuAvailable { get; }
    bool IsProcessing { get; }
}
