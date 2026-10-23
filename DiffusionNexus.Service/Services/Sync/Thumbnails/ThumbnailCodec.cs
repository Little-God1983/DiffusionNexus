using SkiaSharp;

namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// The single decode/resize/encode path for thumbnails: SkiaSharp decodes the source bytes,
/// shrinks anything wider than <see cref="TargetWidth"/>, and re-encodes to JPEG. Both the
/// provider (Task 3, fetched CDN/local bytes) and the writer/step (Task 6) go through this so
/// there is exactly one place that defines "what a thumbnail looks like".
/// </summary>
public static class ThumbnailCodec
{
    public const int TargetWidth = 450;
    public const int JpegQuality = 85;

    /// <summary>
    /// Decodes <paramref name="source"/>, resizing to <see cref="TargetWidth"/> when wider,
    /// and re-encodes as JPEG. Returns <c>null</c> when the bytes are not a decodable image —
    /// callers map that to <c>ThumbnailFailureReason.NotDecodable</c>.
    /// </summary>
    /// <remarks>
    /// Never throws, and the <c>is null</c> checks around <see cref="SKImage.FromBitmap"/> and
    /// <see cref="SKImage.Encode(SKEncodedImageFormat, int)"/> are what make that true: both are
    /// declared to return null, and an unguarded dereference would send a
    /// <see cref="NullReferenceException"/> out through <c>ThumbnailProvider.ProduceAsync</c> —
    /// whose contract is bytes-or-reason — and on past <c>SyncFaults.IsItemFault</c>, which does
    /// not classify it, so the row would never be stamped and would come back as a candidate on
    /// every subsequent run. A null from either is answered the same way a null decode is:
    /// no payload, hence <c>NotDecodable</c>.
    /// </remarks>
    public static ThumbnailPayload? Encode(byte[] source)
    {
        // SKBitmap.Decode throws (rather than returning null) when SkiaSharp can't build a
        // codec for the bytes at all — e.g. a handful of garbage bytes with no recognisable
        // header. That is just as "not decodable" as a codec that decodes to a null bitmap.
        SKBitmap? bitmap;
        try
        {
            bitmap = SKBitmap.Decode(source);
        }
        catch (ArgumentNullException)
        {
            return null;
        }

        using var _ = bitmap;
        if (bitmap is null) return null;

        if (bitmap.Width <= TargetWidth)
        {
            using var originalImage = SKImage.FromBitmap(bitmap);
            if (originalImage is null) return null;

            using var originalEncoded = originalImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
            if (originalEncoded is null) return null;

            var originalBytes = originalEncoded.ToArray();
            if (originalBytes.Length == 0) return null;

            return new ThumbnailPayload(originalBytes, "image/jpeg", bitmap.Width, bitmap.Height);
        }

        var scale = (float)TargetWidth / bitmap.Width;
        // An extreme aspect ratio (e.g. 9000x10) can round to 0; SKBitmap.Resize returns null
        // for a zero-height target, which must not be mistaken for a genuine decode failure.
        var targetHeight = Math.Max(1, (int)MathF.Round(bitmap.Height * scale));

        using var resized = bitmap.Resize(
            new SKImageInfo(TargetWidth, targetHeight),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null) return null;

        using var resizedImage = SKImage.FromBitmap(resized);
        if (resizedImage is null) return null;

        using var resizedEncoded = resizedImage.Encode(SKEncodedImageFormat.Jpeg, JpegQuality);
        if (resizedEncoded is null) return null;

        var resizedBytes = resizedEncoded.ToArray();
        if (resizedBytes.Length == 0) return null;

        return new ThumbnailPayload(resizedBytes, "image/jpeg", TargetWidth, targetHeight);
    }

    /// <summary>
    /// Returns <c>true</c> when the byte payload looks like a video container (MP4/WebM/AVI/MKV)
    /// rather than a decodable image. Used to reject CDN responses that return the full video
    /// stream instead of a poster frame. Moved from <c>ModelTileViewModel.IsVideoData</c> —
    /// checks and offsets copied exactly.
    /// </summary>
    public static bool LooksLikeVideo(byte[] data) => LooksLikeVideo((ReadOnlySpan<byte>)data);

    private static bool LooksLikeVideo(ReadOnlySpan<byte> data)
    {
        // MP4 / MOV — "ftyp" box at offset 4
        if (data.Length >= 8
            && data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return true;

        // WebM / MKV — EBML header (0x1A 0x45 0xDF 0xA3)
        if (data.Length >= 4
            && data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3)
            return true;

        // AVI — "RIFF....AVI "
        if (data.Length >= 11
            && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
            && data[8] == (byte)'A' && data[9] == (byte)'V' && data[10] == (byte)'I')
            return true;

        return false;
    }
}
