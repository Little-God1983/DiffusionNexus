namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// Everything the thumbnail ladder needs to know about one image record, decoupled from the
/// entity so the provider never sees (or is tempted to touch) the database.
/// </summary>
/// <param name="Url">The recorded preview URL — a CDN address, a <c>file://</c> path, or null.</param>
/// <param name="MediaType">Civitai's media type; <c>"video"</c> selects the poster path.</param>
/// <param name="ModelLocalPath">
/// The model file on disk, used only to recover a <c>file://</c> preview whose recorded path has
/// since moved (see <see cref="LocalPreviewFiles.FindSibling"/>). Null when unknown.
/// </param>
/// <param name="AllowVideoDownload">
/// Permission to fall back to downloading the original video and extracting a frame with FFmpeg.
/// Off by default: that costs megabytes per model, so it is the caller's decision, never a default.
/// </param>
public sealed record ThumbnailRequest(
    string? Url, string? MediaType, string? ModelLocalPath, bool AllowVideoDownload = false);

/// <summary>
/// The one place thumbnail bytes come from. Every caller — the sync step, a repair pass, the UI —
/// resolves through this so "how do we get a thumbnail" has exactly one answer.
/// </summary>
public interface IThumbnailProvider
{
    /// <summary>Resolves thumbnail bytes for one image following the §4.3 ladder. Never touches the database.</summary>
    Task<ThumbnailResult> ProduceAsync(ThumbnailRequest request, CancellationToken ct = default);
}
