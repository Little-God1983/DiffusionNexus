using System.Net;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.Service.Services.Sync.Thumbnails;

/// <summary>
/// The §4.3 resolution ladder in one class: local file, video poster, still image, and — only
/// with explicit permission — an FFmpeg frame off the original video. Each rung either produces
/// bytes or names a <see cref="ThumbnailFailureReason"/>; nothing here reads or writes the
/// database, so the caller owns what a failure means for the row.
/// </summary>
/// <remarks>
/// Two rules the ladder exists to enforce. First, a video is never downloaded unless
/// <see cref="ThumbnailRequest.AllowVideoDownload"/> says so — the CDN's poster transform is a few
/// KB, the original is several MB, and a library-wide sync would otherwise pull gigabytes nobody
/// asked for. Second, a cancelled run is not a failed thumbnail: genuine cancellation propagates
/// as <see cref="OperationCanceledException"/> so the caller records nothing, while an HttpClient
/// timeout (a <see cref="TaskCanceledException"/> raised with the caller's token still unsignalled)
/// is an ordinary <see cref="ThumbnailFailureReason.HttpError"/>.
/// </remarks>
public sealed class ThumbnailProvider : IThumbnailProvider
{
    private const string LogSource = "LibrarySync";

    private readonly HttpClient _http;
    private readonly IVideoThumbnailService? _video;
    private readonly IUnifiedLogger? _logger;

    public ThumbnailProvider(HttpClient http, IVideoThumbnailService? video = null, IUnifiedLogger? logger = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _video = video;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ThumbnailResult> ProduceAsync(ThumbnailRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await ResolveAsync(request, ct).ConfigureAwait(false);

        // Logged here rather than at each rung so a single attempt produces exactly one line,
        // whichever rung answered — including the rungs that fall through to one another.
        if (result.Succeeded)
        {
            _logger?.Debug(LogCategory.Network, LogSource,
                $"Thumbnail {result.Payload!.Width}x{result.Payload.Height} ({result.Payload.Data.Length / 1024.0:F1} KB) for {Describe(request.Url)}");
        }
        else
        {
            _logger?.Warn(LogCategory.Network, LogSource,
                $"No thumbnail for {Describe(request.Url)}: {result.Failure}");
        }

        return result;
    }

    /// <summary>The ladder proper. Rung order is normative — rung 3 claims videos before rung 4 ever sees them.</summary>
    private async Task<ThumbnailResult> ResolveAsync(ThumbnailRequest request, CancellationToken ct)
    {
        var url = request.Url;

        // Rung 1 — defensive only: candidate selection already excludes user-uploaded thumbnails,
        // which have no fetchable source at all (the bytes are already the record).
        if (url is not null && url.StartsWith(LocalPreviewFiles.UserThumbnailScheme, StringComparison.OrdinalIgnoreCase))
            return ThumbnailResult.Fail(ThumbnailFailureReason.UnsupportedScheme);

        // Rung 2 — a preview already on disk. Checked before the media type because a local video
        // preview is a file to decode-or-fail, not something to go asking the CDN about.
        if (LocalPreviewFiles.TryGetLocalPath(url, out var localPath))
            return await ProduceFromDiskAsync(localPath, request.ModelLocalPath, ct).ConfigureAwait(false);

        // Rung 3 — a known video: poster transform, never the original.
        if (ModelImage.IsVideoLike(request.MediaType, url))
            return await ProduceFromVideoAsync(request, posterFailuresAreHttp: false, ct).ConfigureAwait(false);

        // Rung 4 — an ordinary still image.
        if (IsHttp(url))
            return await ProduceFromImageAsync(request, ct).ConfigureAwait(false);

        // Rung 6 — no URL, or a scheme this pipeline cannot fetch. (Rung 5 is only ever reached by
        // falling out of rung 3 or 4 with AllowVideoDownload set.)
        return ThumbnailResult.Fail(ThumbnailFailureReason.UnsupportedScheme);
    }

    /// <summary>
    /// Rung 2. The recorded path wins; when it has gone (models get reorganised more often than
    /// their metadata gets rewritten) the model's own directory is probed for a sibling preview.
    /// </summary>
    private async Task<ThumbnailResult> ProduceFromDiskAsync(string localPath, string? modelLocalPath, CancellationToken ct)
    {
        var resolved = File.Exists(localPath) ? localPath : null;

        if (resolved is null && !string.IsNullOrWhiteSpace(modelLocalPath))
            resolved = LocalPreviewFiles.FindSibling(modelLocalPath);

        if (resolved is null) return ThumbnailResult.Fail(ThumbnailFailureReason.LocalFileMissing);

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(resolved, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A path we cannot open is, for every purpose the caller has, a preview that is not there.
            _logger?.Debug(LogCategory.FileSystem, LogSource, $"Local preview unreadable: {resolved}", ex.ToString());
            return ThumbnailResult.Fail(ThumbnailFailureReason.LocalFileMissing);
        }

        return Encode(bytes);
    }

    /// <summary>
    /// Rung 3 (and rung 4's video-in-disguise retry). The CDN only yields a still frame when the
    /// transform carries <c>transcode=true</c>; a non-CDN video has no derivable poster at all.
    /// </summary>
    /// <param name="posterFailuresAreHttp">
    /// How to name a poster fetch that fails. False for rung 3, where the honest answer is "this
    /// video has no poster". True when rung 4 arrived here after an image URL served video bytes —
    /// the brief keeps rung 4's failure mapping for that retry, so a 404 stays a hard
    /// <see cref="ThumbnailFailureReason.Http404"/> rather than becoming a soft, forever-retried
    /// <see cref="ThumbnailFailureReason.VideoNoPoster"/>.
    /// </param>
    private async Task<ThumbnailResult> ProduceFromVideoAsync(ThumbnailRequest request, bool posterFailuresAreHttp, CancellationToken ct)
    {
        var posterUrl = CivitaiImageUrls.ToVideoPosterUrl(request.Url);

        if (posterUrl is null)
        {
            // Not a Civitai CDN URL: there is no transform to ask for, so it is the original or nothing.
            return request.AllowVideoDownload
                ? await ProduceFromVideoDownloadAsync(request, ct).ConfigureAwait(false)
                : ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster);
        }

        var (bytes, httpFailure) = await FetchAsync(posterUrl, ct).ConfigureAwait(false);

        if (bytes is not null && !ThumbnailCodec.LooksLikeVideo(bytes))
        {
            var payload = ThumbnailCodec.Encode(bytes);
            if (payload is not null) return ThumbnailResult.Ok(payload);
        }

        // Permission granted beats any poster failure — the frame is still obtainable, just expensively.
        if (request.AllowVideoDownload)
            return await ProduceFromVideoDownloadAsync(request, ct).ConfigureAwait(false);

        if (!posterFailuresAreHttp) return ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster);

        // Rung 4's mapping: an HTTP fault keeps its own name; a body that arrived but would not
        // decode (or was video a second time) is simply not decodable.
        return ThumbnailResult.Fail(httpFailure ?? ThumbnailFailureReason.NotDecodable);
    }

    /// <summary>Rung 4. One GET of the 450px transform, with one allowance for the CDN handing back a video.</summary>
    private async Task<ThumbnailResult> ProduceFromImageAsync(ThumbnailRequest request, CancellationToken ct)
    {
        var thumbnailUrl = CivitaiImageUrls.ToThumbnailUrl(request.Url);
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
            return ThumbnailResult.Fail(ThumbnailFailureReason.UnsupportedScheme);

        var (bytes, httpFailure) = await FetchAsync(thumbnailUrl, ct).ConfigureAwait(false);
        if (bytes is null) return ThumbnailResult.Fail(httpFailure!);

        // The record said "image" but the bytes are a container: the media type was wrong, so
        // re-run the video rung. Once — the poster fetch itself is not retried again from there.
        if (ThumbnailCodec.LooksLikeVideo(bytes))
            return await ProduceFromVideoAsync(request, posterFailuresAreHttp: true, ct).ConfigureAwait(false);

        return Encode(bytes);
    }

    /// <summary>
    /// Rung 5. The expensive last resort, reachable only with
    /// <see cref="ThumbnailRequest.AllowVideoDownload"/>: stream the original to a temp file and let
    /// FFmpeg cut a mid-frame out of it. Mirrors <c>ModelTileViewModel.DownloadVideoThumbnailAsync</c>,
    /// with the cancellation token threaded through every await and both temp files always removed.
    /// </summary>
    private async Task<ThumbnailResult> ProduceFromVideoDownloadAsync(ThumbnailRequest request, CancellationToken ct)
    {
        if (_video is null || string.IsNullOrWhiteSpace(request.Url) || !IsHttp(request.Url))
            return ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster);

        var tempVideoPath = Path.Combine(Path.GetTempPath(), $"dn_preview_{Guid.NewGuid():N}.mp4");
        string? framePath = null;
        try
        {
            // Checked before the download: finding out FFmpeg is missing after pulling 8 MB is
            // the same answer for a much higher price.
            await _video.EnsureFFmpegAvailableAsync(ct).ConfigureAwait(false);

            // Streamed to disk rather than buffered — a preview clip can be tens of megabytes.
            await using (var source = await _http.GetStreamAsync(request.Url, ct).ConfigureAwait(false))
            await using (var file = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true))
            {
                await source.CopyToAsync(file, ct).ConfigureAwait(false);
            }

            var frame = await _video.GenerateThumbnailAsync(
                tempVideoPath,
                new VideoThumbnailOptions { MaxWidth = ThumbnailCodec.TargetWidth, OutputFormat = ThumbnailFormat.WebP },
                ct).ConfigureAwait(false);

            if (!frame.Success || string.IsNullOrEmpty(frame.ThumbnailPath))
            {
                _logger?.Debug(LogCategory.General, LogSource,
                    $"FFmpeg frame extraction failed: {frame.ErrorMessage ?? "unknown error"}");
                return ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster);
            }

            framePath = frame.ThumbnailPath;
            var bytes = await File.ReadAllBytesAsync(framePath, ct).ConfigureAwait(false);

            // Re-encoded rather than stored as-is so a frame is the same 450px JPEG as everything else.
            var payload = ThumbnailCodec.Encode(bytes);
            return payload is null
                ? ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster)
                : ThumbnailResult.Ok(payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Download, FFmpeg, and disk all fail the same way from the caller's side: no poster.
            _logger?.Debug(LogCategory.General, LogSource, $"Video frame extraction failed for {Describe(request.Url)}", ex.ToString());
            return ThumbnailResult.Fail(ThumbnailFailureReason.VideoNoPoster);
        }
        finally
        {
            TryDelete(tempVideoPath);
            if (framePath is not null) TryDelete(framePath);
        }
    }

    /// <summary>
    /// One GET, reduced to bytes-or-reason. Exactly one of the two is non-null.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Failure)> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);

            // A 404 is a hard answer — the asset is gone and asking again tomorrow changes nothing.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return (null, ThumbnailFailureReason.Http404);

            if (!response.IsSuccessStatusCode)
                return (null, ThumbnailFailureReason.HttpError);

            return (await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled: not a thumbnail that failed, a thumbnail nobody waited for.
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient's own timeout surfaces as a TaskCanceledException with our token unsignalled.
            return (null, ThumbnailFailureReason.HttpError);
        }
        catch (HttpRequestException)
        {
            return (null, ThumbnailFailureReason.HttpError);
        }
    }

    private static ThumbnailResult Encode(byte[] bytes)
    {
        var payload = ThumbnailCodec.Encode(bytes);
        return payload is null
            ? ThumbnailResult.Fail(ThumbnailFailureReason.NotDecodable)
            : ThumbnailResult.Ok(payload);
    }

    private static bool IsHttp(string? url) =>
        url is not null
        && (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    private static string Describe(string? url) => string.IsNullOrWhiteSpace(url) ? "(no url)" : url;

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Best-effort cleanup — a leftover temp file is not worth failing a sync over. */ }
    }
}
