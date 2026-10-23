namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// A decoded, resized, re-encoded thumbnail ready for BLOB storage.
/// </summary>
public sealed record ThumbnailPayload(byte[] Data, string MimeType, int Width, int Height);

/// <summary>
/// Outcome of a thumbnail fetch/decode attempt. Exactly one of <see cref="Payload"/> or
/// <see cref="Failure"/> is set — <see cref="Failure"/> is a
/// <see cref="DiffusionNexus.Domain.Entities.ThumbnailFailureReason"/> constant so the writer
/// can decide (hard failure vs. retry) without re-deriving the reason.
/// </summary>
public sealed record ThumbnailResult(ThumbnailPayload? Payload, string? Failure)
{
    public bool Succeeded => Payload is not null;

    public static ThumbnailResult Ok(ThumbnailPayload payload) => new(payload, null);

    public static ThumbnailResult Fail(string reason) => new(null, reason);
}
