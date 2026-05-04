using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Contract for a single image-level quality check that operates on actual
/// pixel data (blur, exposure, noise, duplicates, etc.).
///
/// <para>
/// Unlike <see cref="IDatasetCheck"/> which works on caption text,
/// implementations of this interface load and analyze image pixels.
/// Checks run asynchronously with progress reporting and cancellation support
/// to stay responsive on large datasets.
/// </para>
/// </summary>
public interface IImageQualityCheck
{
    /// <summary>
    /// Human-readable name shown in the UI and included on every <see cref="Issue"/>.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Short description of what this check measures.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Execution order within the pipeline. Lower values run first.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Whether this check benefits from GPU acceleration.
    /// When true, the UI can warn the user that the check may be slow without a GPU.
    /// </summary>
    bool RequiresGpu { get; }

    /// <summary>
    /// The scoring category this check contributes to.
    /// </summary>
    QualityScoreCategory Category { get; }

    /// <summary>
    /// Determines whether this check should run for the given LoRA type.
    /// </summary>
    bool IsApplicable(LoraType loraType);

    /// <summary>
    /// Runs the check against all images in the dataset.
    /// </summary>
    /// <param name="images">Image files with their dimensions.</param>
    /// <param name="config">Dataset configuration.</param>
    /// <param name="progress">Optional progress reporter (0.0 – 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result containing a score, issues, and per-image breakdowns.</returns>
    Task<ImageCheckResult> RunAsync(
        IReadOnlyList<ImageFileInfo> images,
        DatasetConfig config,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
