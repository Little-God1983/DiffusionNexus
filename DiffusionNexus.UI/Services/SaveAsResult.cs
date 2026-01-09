namespace DiffusionNexus.UI.Services;

using DiffusionNexus.UI.ViewModels;

/// <summary>
/// Result from the Save As dialog containing the new filename and rating.
/// </summary>
public sealed record SaveAsResult
{
    /// <summary>
    /// Gets whether the dialog was cancelled.
    /// </summary>
    public bool IsCancelled { get; init; }

    /// <summary>
    /// Gets the new filename (without path or extension).
    /// </summary>
    public string? FileName { get; init; }

    /// <summary>
    /// Gets the rating to apply to the saved image.
    /// </summary>
    public ImageRatingStatus Rating { get; init; } = ImageRatingStatus.Unrated;

    /// <summary>
    /// Creates a cancelled result.
    /// </summary>
    public static SaveAsResult Cancelled() => new() { IsCancelled = true };

    /// <summary>
    /// Creates a successful result with the specified filename and rating.
    /// </summary>
    /// <param name="fileName">The new filename (without path or extension).</param>
    /// <param name="rating">The rating to apply.</param>
    public static SaveAsResult Success(string fileName, ImageRatingStatus rating) =>
        new() { FileName = fileName, Rating = rating };
}
