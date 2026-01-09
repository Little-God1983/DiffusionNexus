namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents the quality rating/status of an image.
/// Used for marking images as production-ready, failed, or unrated.
/// </summary>
public enum ImageRatingStatus
{
    /// <summary>
    /// Image has not been rated yet.
    /// </summary>
    Unrated = 0,

    /// <summary>
    /// Image is marked as production-ready (approved).
    /// </summary>
    Approved = 1,

    /// <summary>
    /// Image is marked as failed/rejected.
    /// </summary>
    Rejected = -1
}
