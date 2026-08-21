namespace DiffusionNexus.Domain.Entities;

/// <summary>
/// Values for <see cref="ModelImage.ThumbnailFailure"/>. Strings (not an enum) so a future reason
/// needs no migration. Hard failures are never retried automatically — only by an explicit Force.
/// </summary>
public static class ThumbnailFailureReason
{
    public const string Http404 = "Http404";
    public const string HttpError = "HttpError";
    public const string NotDecodable = "NotDecodable";
    /// <summary>An existing BLOB failed to decode; it was nulled and will be re-fetched once.</summary>
    public const string Corrupt = "Corrupt";
    public const string LocalFileMissing = "LocalFileMissing";
    public const string VideoNoPoster = "VideoNoPoster";
    /// <summary>URL scheme the thumbnail pipeline cannot fetch (anything but http/https/file).</summary>
    public const string UnsupportedScheme = "UnsupportedScheme";

    public static bool IsHardFailure(string? reason) => reason is Http404 or NotDecodable or LocalFileMissing or UnsupportedScheme;
}
