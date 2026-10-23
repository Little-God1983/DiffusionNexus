# Metadata Sync Overhaul — Plan B: Thumbnails Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The bulk sync produces thumbnails incrementally with every attempt and failure recorded on `ModelImage`, videos get a ~65 KB CDN poster JPEG instead of a multi-MB download (0 video bytes in bulk), and all thumbnail byte-work collapses into one Service-side provider.

**Architecture:** A new `DiffusionNexus.Service.Services.Sync.Thumbnails` namespace holds three static helpers (`CivitaiImageUrls` URL rewriting, `LocalPreviewFiles` sibling/`file://` handling, `ThumbnailCodec` Skia decode/resize/encode), an HTTP-owning `ThumbnailProvider` behind `IThumbnailProvider` (typed client), a static `ThumbnailWriter` that is the single place `ThumbnailData`/`ThumbnailAttemptedAt`/`ThumbnailFailure` are written, and a `ThumbnailsStep : ISyncStep` (one item per image, no Civitai pacer — the CDN is not the rate-limited API). The UI tile path and `SidecarMetadataApplier` delegate their byte-work to the same helpers; the FFmpeg full-video fallback survives only on the user-initiated single-tile path, cancellation-aware.

**Tech Stack:** .NET 10, EF Core 10 + SQLite, SkiaSharp 3.119.4 (already in Service), `Microsoft.Extensions.Http` typed client, xUnit + FluentAssertions + Moq, Avalonia (UI edits only).

**Spec:** `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` §4.3 (provider ladder), §4.2 step 4, §6 (acceptance). Supplementary code inventory with exact line anchors: `.superpowers/sdd/2026-08-22-metadata-sync-overhaul-B-thumbnails/planB-inventory.md` (git-ignored; read it if an anchor in this plan has drifted).

## Global Constraints

- **Never select `ThumbnailData` into a candidate/projection query** — only null/length flags. Copy the SQL-capture assertion pattern from `SyncStateRepositoryTests.cs` (`.Should().NotContain("ThumbnailData")`).
- **0 video bytes in bulk**: the sync step and the tile scroll path must never download a video file. Full video + FFmpeg only via `AllowVideoDownload: true`, which only the user-initiated single-tile path sets.
- **`user-thumbnail://` rows are user-owned**: excluded from candidate selection, never fetched, never overwritten. `ModelDetailViewModel.UploadThumbnailAsync` is not touched by this plan.
- **`file://` URLs are malformed by construction** (`file://C:\x\y.png` — the drive letter parses as URI authority). Strip the prefix string-wise; NEVER `new Uri(url).LocalPath`. Do not repair existing rows.
- **The deferred sentinel is presence**: `ModelImage.IsThumbnailDeferred` / `HasThumbnail` (`ModelImage.cs:159-182`) are the only correct checks; `ThumbnailData is null` alone is wrong on lightly-loaded entities.
- **No `ICivitaiRequestPacer` anywhere in the thumbnail pipeline** — keep it out of the step's constructor entirely so a reviewer cannot mistake the intent.
- **One imaging stack**: SkiaSharp. No new SixLabors.ImageSharp usage.
- Steps hold no DbContext: fresh `IServiceScope`/`IUnitOfWork` per `SelectAsync` and per `ExecuteOneAsync`; `SelectAsync` never touches the network; `IAppSettingsService` must be resolved *inside* the step's own scope (it is transient over a scoped UoW — see `FetchImagesStep.cs:54-57`).
- Tests: real in-memory SQLite (`DataSource=:memory:`, connection held open, `AddDataAccessLayer`, `EnsureCreated()`), seeded `LocalPath` under the mocked enabled root; no EF InMemory provider, no Avalonia global init, no culture-sensitive asserts (German-locale machine). Test command: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` (never solution-level; Debug only if Release is file-locked — MSB3021/3027).
- Hand-written files: UTF-8 **without** BOM, match each file's existing CRLF/LF. Commits end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Never push; never commit to develop/main.
- Settings UI (concurrency/retry knobs) is **Plan E** — this plan uses constants and the existing `SyncRetryPolicy.ErrorRetryAfter`. Zero schema churn: no new migrations in this plan.

---

### Task 1: URL and local-file helpers

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/CivitaiImageUrls.cs`
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/LocalPreviewFiles.cs`
- Modify: `DiffusionNexus.Domain/Entities/ModelImage.cs` (extract `IsVideoLike`)
- Modify: `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiResultViewModel.cs:309-333` (delegate)
- Test: `DiffusionNexus.Tests/Sync/Service/Thumbnails/CivitaiImageUrlsTests.cs`, `LocalPreviewFilesTests.cs`

**Interfaces (Produces):**
```csharp
public static class CivitaiImageUrls
{
    public const string ThumbnailTransform = "width=450";
    public const string VideoPosterTransform = "width=450,anim=false,transcode=true";
    public static string? WithTransform(string? url, string transform);   // null-safe; non-CDN URLs returned unchanged
    public static string? ToThumbnailUrl(string? url);                    // WithTransform(url, ThumbnailTransform)
    public static string? ToVideoPosterUrl(string? url);                  // transform swap + final extension -> .jpeg; NULL for non-CDN urls
}
public static class LocalPreviewFiles
{
    public const string FileUrlPrefix = "file://";
    public const string UserThumbnailScheme = "user-thumbnail://";
    public static readonly string[] Extensions; // .preview.png,.preview.jpg,.preview.jpeg,.preview.webp,.thumb.jpg,.png,.jpg,.jpeg,.webp
    public static string? FindSibling(string modelFilePath);              // dir + baseName + ext ladder, File.Exists probe, null if none
    public static bool TryGetLocalPath(string? url, out string path);     // strips FileUrlPrefix string-wise
}
// on ModelImage:
public static bool IsVideoLike(string? mediaType, string? url);           // extracted from the IsVideo property; property delegates to it
```

- [ ] **Step 1: Write the failing tests**

`CivitaiImageUrlsTests.cs` (plain xunit class, no fixture):
```csharp
public class CivitaiImageUrlsTests
{
    private const string Base = "https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/abc-123";

    [Theory]
    [InlineData($"{Base}/width=450/img.jpeg", $"{Base}/width=450/img.jpeg")]            // already right
    [InlineData($"{Base}/original=true/img.jpeg", $"{Base}/width=450/img.jpeg")]        // replaces existing transform
    [InlineData($"{Base}/img.jpeg", $"{Base}/width=450/img.jpeg")]                      // inserts when absent
    [InlineData($"{Base}/width=300/img.jpeg?token=x", $"{Base}/width=450/img.jpeg?token=x")] // query preserved
    public void ToThumbnailUrl_NormalisesTheTransformSegment(string input, string expected)
        => CivitaiImageUrls.ToThumbnailUrl(input).Should().Be(expected);

    [Fact]
    public void ToThumbnailUrl_LeavesNonCdnUrlsAlone()
        => CivitaiImageUrls.ToThumbnailUrl("https://example.com/a/b.png").Should().Be("https://example.com/a/b.png");

    [Theory]
    [InlineData($"{Base}/width=450/clip.mp4", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    [InlineData($"{Base}/clip.webm", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    [InlineData($"{Base}/transcode=true,width=320/clip.mp4", $"{Base}/width=450,anim=false,transcode=true/clip.jpeg")]
    public void ToVideoPosterUrl_RewritesTransformAndExtension(string input, string expected)
        => CivitaiImageUrls.ToVideoPosterUrl(input).Should().Be(expected);

    [Fact]
    public void ToVideoPosterUrl_ReturnsNullForNonCdnUrls()
        => CivitaiImageUrls.ToVideoPosterUrl("https://example.com/v.mp4").Should().BeNull();

    [Fact]
    public void ToThumbnailUrl_NullAndWhitespacePassThrough()
    {
        CivitaiImageUrls.ToThumbnailUrl(null).Should().BeNull();
        CivitaiImageUrls.ToThumbnailUrl("  ").Should().Be("  ");
    }
}
```

`LocalPreviewFilesTests.cs`:
```csharp
public class LocalPreviewFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dn-lpf-" + Guid.NewGuid().ToString("N"))).FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void FindSibling_PrefersThePreviewLadderOrder()
    {
        var model = Path.Combine(_dir, "mylora.safetensors");
        File.WriteAllBytes(Path.Combine(_dir, "mylora.png"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "mylora.preview.jpg"), [1]);
        LocalPreviewFiles.FindSibling(model).Should().Be(Path.Combine(_dir, "mylora.preview.jpg"));
    }

    [Fact]
    public void FindSibling_ReturnsNullWhenNothingMatchesOrDirectoryMissing()
    {
        LocalPreviewFiles.FindSibling(Path.Combine(_dir, "none.safetensors")).Should().BeNull();
        LocalPreviewFiles.FindSibling(Path.Combine(_dir, "gone", "x.safetensors")).Should().BeNull();
    }

    [Theory]
    [InlineData(@"file://C:\loras\a.png", @"C:\loras\a.png", true)]   // the malformed-by-construction shape must work
    [InlineData("file:///tmp/a.png", "/tmp/a.png", true)]
    [InlineData("https://x/a.png", "", false)]
    [InlineData("user-thumbnail://abc", "", false)]
    [InlineData(null, "", false)]
    public void TryGetLocalPath_StripsThePrefixStringWise(string? url, string expected, bool ok)
    {
        LocalPreviewFiles.TryGetLocalPath(url, out var path).Should().Be(ok);
        path.Should().Be(expected);
    }
}
```

Also add to the existing `ModelImage` tests (or a new `ModelImageIsVideoLikeTests.cs`): `IsVideoLike("video", null)` true, `IsVideoLike("VIDEO", null)` true (case-insensitive), `IsVideoLike("image", "x.mp4")` — assert whatever `ModelImage.IsVideo` does **today** for that combination (read the property first; the extraction must be behavior-preserving, the test pins it).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~Thumbnails"` — expected: compile failure (types don't exist).

- [ ] **Step 3: Implement**

`CivitaiImageUrls.WithTransform` is the generalisation of `CivitaiResultViewModel.RewriteToResizedImageUrl` (`CivitaiResultViewModel.cs:309-333` — copy its segment logic verbatim, parameterise the `transforms` constant). `ToVideoPosterUrl`: return `null` when the URL doesn't contain `image.civitai.com` (OrdinalIgnoreCase); otherwise `WithTransform(url, VideoPosterTransform)` then replace the extension of the final file part (last segment before any `?`) with `.jpeg` via `Path.ChangeExtension`-style string surgery on the segment (not on the whole URL). `ModelImage.IsVideoLike`: move the body of the existing `IsVideo` property into the static and have the property call `IsVideoLike(MediaType, Url)`.

- [ ] **Step 4: Delegate the browser's copy**

Replace the body of `CivitaiResultViewModel.RewriteToResizedImageUrl` with `return CivitaiImageUrls.ToThumbnailUrl(url);` (UI already references Service). Do NOT delete the private method (its callers stay).

- [ ] **Step 5: Run the scoped filter + build, commit**

Run: the Thumbnails filter above plus `--filter "FullyQualifiedName~CivitaiResultViewModel"` if such tests exist. Expected: PASS.
```bash
git add -A && git commit -m "feat(sync): shared Civitai CDN url rewriting and local preview discovery"
```

### Task 2: ThumbnailCodec and result records

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/ThumbnailCodec.cs`
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/ThumbnailResult.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Thumbnails/ThumbnailCodecTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record ThumbnailPayload(byte[] Data, string MimeType, int Width, int Height);
public sealed record ThumbnailResult(ThumbnailPayload? Payload, string? Failure)
{
    public bool Succeeded => Payload is not null;
    public static ThumbnailResult Ok(ThumbnailPayload p) => new(p, null);
    public static ThumbnailResult Fail(string reason) => new(null, reason);   // reason = a ThumbnailFailureReason constant
}
public static class ThumbnailCodec
{
    public const int TargetWidth = 450;
    public const int JpegQuality = 85;
    public static ThumbnailPayload? Encode(byte[] source);   // null when not decodable
    public static bool LooksLikeVideo(byte[] data);          // moved magic-byte check
}
```

- [ ] **Step 1: Write the failing tests**

```csharp
public class ThumbnailCodecTests
{
    private static byte[] Png(int w, int h)
    {
        using var bmp = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Teal);
        using var img = SKImage.FromBitmap(bmp);
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void Encode_ShrinksWideImagesToTargetWidthAsJpeg()
    {
        var payload = ThumbnailCodec.Encode(Png(900, 600));
        payload.Should().NotBeNull();
        payload!.MimeType.Should().Be("image/jpeg");
        payload.Width.Should().Be(ThumbnailCodec.TargetWidth);
        payload.Height.Should().Be(300);
        SKBitmap.Decode(payload.Data).Should().NotBeNull("the stored bytes must round-trip");
    }

    [Fact]
    public void Encode_KeepsNarrowImagesAtTheirSize()
    {
        var payload = ThumbnailCodec.Encode(Png(200, 300));
        payload!.Width.Should().Be(200);
        payload.Height.Should().Be(300);
    }

    [Fact]
    public void Encode_ReturnsNullForUndecodableBytes()
        => ThumbnailCodec.Encode([0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01]).Should().BeNull();

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x69, 0x73, 0x6F, 0x6D }, true)]  // ....ftypisom
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0 }, true)]                             // EBML (webm)
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, false)]                            // PNG
    public void LooksLikeVideo_RecognisesContainerMagic(byte[] head, bool expected)
        => ThumbnailCodec.LooksLikeVideo(head).Should().Be(expected);
}
```

- [ ] **Step 2: Run to verify failure** — compile failure expected.

- [ ] **Step 3: Implement**

`Encode`: `SKBitmap.Decode(source)`; null → null. If `Width > TargetWidth` resize to `(TargetWidth, round(h * TargetWidth / w))` with `SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)` (same as `SidecarMetadataApplier.cs:736-750`); encode `SKEncodedImageFormat.Jpeg, JpegQuality`; return payload with final dimensions. `LooksLikeVideo`: move the checks from `ModelTileViewModel.IsVideoData` (`ModelTileViewModel.cs:1884-1903`): `ftyp` at offset 4, EBML `1A 45 DF A3` at 0, `RIFF`+`AVI ` — copy the offsets exactly.

- [ ] **Step 4: Run scoped tests, commit**
```bash
git add -A && git commit -m "feat(sync): one thumbnail codec — 450px jpeg, honest decode failures"
```

### Task 3: ThumbnailProvider

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/IThumbnailProvider.cs`
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/ThumbnailProvider.cs`
- Modify: `DiffusionNexus.Service/DiffusionNexus.Service.csproj` (add `Microsoft.Extensions.Http` if not already transitive — check with `dotnet list package --include-transitive` first)
- Test: `DiffusionNexus.Tests/Sync/Service/Thumbnails/ThumbnailProviderTests.cs`

**Interfaces (Produces):**
```csharp
public sealed record ThumbnailRequest(
    string? Url, string? MediaType, string? ModelLocalPath, bool AllowVideoDownload = false);

public interface IThumbnailProvider
{
    /// <summary>Resolves thumbnail bytes for one image following the §4.3 ladder. Never touches the database.</summary>
    Task<ThumbnailResult> ProduceAsync(ThumbnailRequest request, CancellationToken ct = default);
}

public sealed class ThumbnailProvider : IThumbnailProvider
{
    public ThumbnailProvider(HttpClient http, IVideoThumbnailService? video = null, IUnifiedLogger? logger = null);
}
```

**Ladder (implement in this order, each rung returning a `ThumbnailResult`):**
1. `Url` starts with `LocalPreviewFiles.UserThumbnailScheme` → `Fail(UnsupportedScheme)` (defensive — selection excludes these).
2. `LocalPreviewFiles.TryGetLocalPath(Url, out var path)` → read the file (`File.ReadAllBytesAsync(path, ct)`); missing → try `LocalPreviewFiles.FindSibling(ModelLocalPath)` when `ModelLocalPath` is non-null; still nothing → `Fail(LocalFileMissing)`; bytes → `ThumbnailCodec.Encode` → `Ok` or `Fail(NotDecodable)`.
3. `ModelImage.IsVideoLike(MediaType, Url)` → `CivitaiImageUrls.ToVideoPosterUrl(Url)`:
   - poster URL non-null → GET; success + decodable → `Ok`; any HTTP failure or undecodable body → if `AllowVideoDownload` fall through to rung 5, else `Fail(VideoNoPoster)`.
   - poster URL null (non-CDN video) → if `AllowVideoDownload` rung 5, else `Fail(VideoNoPoster)`.
4. Image over http/https → GET `CivitaiImageUrls.ToThumbnailUrl(Url)`;
   - 404 → `Fail(Http404)`; other non-success / `HttpRequestException` / timeout (`TaskCanceledException` when `!ct.IsCancellationRequested`) → `Fail(HttpError)`;
   - body passes `ThumbnailCodec.LooksLikeVideo` → the URL was a video in disguise: retry once via rung 3's poster URL, then the same failure rules;
   - `ThumbnailCodec.Encode` → `Ok` or `Fail(NotDecodable)`.
5. **Video fallback (only reachable with `AllowVideoDownload: true`)**: stream the original `Url` to `Path.GetTempPath()/dn_preview_{guid}` with `ct`, call `IVideoThumbnailService.GenerateThumbnailAsync` exactly as `ModelTileViewModel.cs:1843-1846` does today (MaxWidth 300 → but pass `ThumbnailCodec.TargetWidth`), read + `ThumbnailCodec.Encode` the frame, `finally` delete both temp files. `_video is null` or any failure → `Fail(VideoNoPoster)`. Every await takes `ct`.
6. Anything else (no URL, unknown scheme) → `Fail(UnsupportedScheme)`.
Genuine cancellation (`ct.IsCancellationRequested`) always propagates as `OperationCanceledException` — never a `Fail`.

- [ ] **Step 1: Write the failing tests** (FakeHttpHandler pattern from `CivitaiClientTests.cs:500`)

```csharp
public class ThumbnailProviderTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<string> Urls = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        { Urls.Add(req.RequestUri!.ToString()); return Task.FromResult(respond(req)); }
    }
    private static ThumbnailProvider Provider(FakeHandler handler) => new(new HttpClient(handler));
    private static byte[] Png() { /* same SKBitmap helper as ThumbnailCodecTests, 64x64 */ }
    private static HttpResponseMessage Bytes(byte[] data, string mime = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) { Headers = { ContentType = new("image/png".Equals(mime) ? "image/png" : mime) } } };

    [Fact] public async Task Image_FetchesTheWidth450UrlAndEncodes() { … assert handler.Urls.Single().Contains("width=450") and result.Succeeded … }
    [Fact] public async Task Http404_IsAHardFailure() { … respond 404 → result.Failure == ThumbnailFailureReason.Http404 … }
    [Fact] public async Task ServerError_IsSoftHttpError() { … respond 503 → Failure == HttpError … }
    [Fact] public async Task Video_FetchesOnlyThePosterUrl_NeverTheVideo()
    {
        var handler = new FakeHandler(_ => Bytes(Png()));
        var result = await Provider(handler).ProduceAsync(new(
            "https://image.civitai.com/x/abc/width=450/clip.mp4", "video", null));
        result.Succeeded.Should().BeTrue();
        handler.Urls.Should().ContainSingle().Which.Should().Contain("anim=false,transcode=true").And.EndWith("clip.jpeg");
    }
    [Fact] public async Task Video_PosterFailureWithoutPermissionIsVideoNoPoster() { … 404 on poster, AllowVideoDownload false → VideoNoPoster; handler.Urls has ONLY the poster URL … }
    [Fact] public async Task NonCdnVideo_WithoutPermissionIsVideoNoPoster_NoHttpAtAll() { … Url https://example.com/v.mp4, MediaType video → VideoNoPoster, handler.Urls empty … }
    [Fact] public async Task ImageBytesThatAreVideo_RetryViaPosterUrl() { … first response = ftyp bytes, second = Png → Succeeded, Urls[1] contains transcode=true … }
    [Fact] public async Task FileUrl_ReadsDiskAndNeverHttp() { … write real png to temp, Url = "file://" + path → Succeeded, handler.Urls empty … }
    [Fact] public async Task FileUrl_MissingFallsBackToSiblingThenLocalFileMissing() { … }
    [Fact] public async Task UserThumbnailScheme_IsUnsupported() { … }
    [Fact] public async Task Cancellation_Propagates() { … handler that throws OperationCanceledException with a cancelled ct → await FluentActions … ThrowAsync<OperationCanceledException>() … }
}
```
Write the elided bodies out fully — each is 4-8 lines of arrange/act/assert following the shown patterns.

- [ ] **Step 2: Run to verify failure** — compile failure expected.
- [ ] **Step 3: Implement the provider** per the ladder. HTTP: `await _http.GetAsync(url, ct)`; read bytes with `Content.ReadAsByteArrayAsync(ct)`. Log one Warn per failure (`LogSource "LibrarySync"`), Debug for successes.
- [ ] **Step 4: Run the Thumbnails filter — all green. Commit.**
```bash
git add -A && git commit -m "feat(sync): IThumbnailProvider — the one place thumbnail bytes come from"
```

### Task 4: Online poster canary

**Files:**
- Create: `DiffusionNexus.Tests/OnlineTests/OnlineFactAttribute.cs`
- Create: `DiffusionNexus.Tests/OnlineTests/CivitaiCdnPosterCanaryTests.cs`

- [ ] **Step 1: Create the attribute** — copy the SDK's `OnlineFactAttribute` verbatim (env var `DIFFUSIONNEXUS_SDK_ONLINE_TESTS`; the "SDK" in the name is deliberate — one switch flips canaries in both repos; say so in a class-level comment).
- [ ] **Step 2: Write the canary**
```csharp
public class CivitaiCdnPosterCanaryTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    // any long-lived public civitai video asset; taken from the design's live verification
    private const string VideoUrl = "<pick a real, currently-live image.civitai.com video URL and record it here>";

    [OnlineFact]
    public async Task PosterTransformReturnsJpeg()
    {
        var poster = CivitaiImageUrls.ToVideoPosterUrl(VideoUrl)!;
        using var resp = await Http.GetAsync(poster);
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("image/jpeg");
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().BeLessThan(1_000_000);
    }

    [OnlineFact]
    public async Task WithoutTranscodeTheCdnStillReturnsVideo()   // guards the WHY of transcode=true
    {
        var noTranscode = CivitaiImageUrls.WithTransform(VideoUrl, "width=450,anim=false")!;
        var url = noTranscode[..noTranscode.LastIndexOf('.')] + ".jpeg";
        using var resp = await Http.GetAsync(url);
        resp.Content.Headers.ContentType!.MediaType.Should().NotBe("image/jpeg");
    }
}
```
The implementer must find a real live video URL (e.g. via the Civitai API for a well-known model) and bake it in with a dated comment. If the second assertion turns out to pass as jpeg (CDN behavior changed), keep the first test, delete the second, and note it in the report.
- [ ] **Step 3: Run once with the env var set** (`DIFFUSIONNEXUS_SDK_ONLINE_TESTS=1`, only these two tests) to prove them live; then run the normal suite to prove they skip. Commit.
```bash
git add -A && git commit -m "test(sync): opt-in canary pinning the civitai poster-frame transform"
```

### Task 5: Candidate selection + due predicate

**Files:**
- Modify: `DiffusionNexus.Domain/Services/Sync/SyncCandidates.cs` (add record)
- Modify: `DiffusionNexus.Domain/Services/Sync/SyncRetryPolicy.cs` (add predicate)
- Modify: `DiffusionNexus.DataAccess/Repositories/Interfaces/ISyncStateRepository.cs`, `DiffusionNexus.DataAccess/Repositories/SyncStateRepository.cs`
- Modify: `DiffusionNexus.Domain/Interfaces/IModelRepository.cs` + `DiffusionNexus.DataAccess/Repositories/ModelRepository.cs` (add `GetImageByIdAsync`)
- Test: `DiffusionNexus.Tests/Sync/DataAccess/SyncStateRepositoryThumbnailTests.cs`, additions to `DiffusionNexus.Tests/Sync/Domain/` for the predicate

**Interfaces (Produces):**
```csharp
public sealed record ThumbnailCandidate(
    int ModelId, int VersionId, int ImageId, string Name,
    string Url, string? MediaType, string? LocalPath,
    DateTimeOffset? ThumbnailAttemptedAt, string? ThumbnailFailure);

// ISyncStateRepository:
Task<IReadOnlyList<ThumbnailCandidate>> SelectThumbnailCandidatesAsync(
    SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default);

// IModelRepository:
Task<ModelImage?> GetImageByIdAsync(int imageId, CancellationToken ct = default);

// SyncRetryPolicy:
public bool IsThumbnailDue(DateTimeOffset? attemptedAt, string? failure, DateTimeOffset now, bool force);
```

**Selection semantics (the load-bearing part):**
- Scope via the existing `ApplyScopeAsync` — **no `CivitaiId` filter** (local/sidecar models with `file://` rows deserve thumbnails too; this deliberately differs from `SelectImageCandidatesAsync`).
- SQL projects ALL images of in-scope versions with small columns only: `ModelId, VersionId, ImageId, Name (version name), Url, MediaType, IsNsfw, SortOrder, HasThumb = (i.ThumbnailData != null && i.ThumbnailData.Length > 0), ThumbnailAttemptedAt, ThumbnailFailure`, plus the version's primary-file `LocalPath` (same explicit-join style as `SelectImageCandidatesAsync`, `SyncStateRepository.cs:408-431`). **Never `ThumbnailData` itself.**
- In memory, per version: rank images exactly like `ModelVersion.PrimaryImage` (`ModelVersion.cs:80-84`) using `ModelImage.IsVideoLike(MediaType, Url)` for the video test — rank = `(IsNsfw || IsVideo → tiers)`, tie-break `SortOrder` then `ImageId` (mirror `Images` ordering; check the EF config for an ordering on the collection and match it).
- The version's primary already has `HasThumb` → the version contributes **no** candidate. Otherwise the primary becomes the candidate.
- Exclude `Url.StartsWith("user-thumbnail://")` rows from ranking entirely (they are user-owned; a version whose ONLY image is user-thumbnail contributes nothing).
- Order results by `(ModelId, VersionId)`.

**Due predicate:**
```csharp
public bool IsThumbnailDue(DateTimeOffset? attemptedAt, string? failure, DateTimeOffset now, bool force)
    => force
       || attemptedAt is null
       || failure == ThumbnailFailureReason.Corrupt                       // self-heal: refetch next run
       || (failure is not null
           && !ThumbnailFailureReason.IsHardFailure(failure)
           && now - attemptedAt.Value >= ErrorRetryAfter);
```
Hard failures (`Http404`, `NotDecodable`, `LocalFileMissing`, `UnsupportedScheme`) are final answers — only `force` retries them.

- [ ] **Step 1: Write the failing tests** (fixture pattern from `FetchImagesStepTests.cs:25-56`; seed under `Path.GetTempPath()`):
  - `ThumbnailCandidates_PickThePrimaryImagePerVersion` — version with a video image (SortOrder 0) and a static image (SortOrder 1), neither with BLOB → the static one is the candidate (matches `PrimaryImage`'s non-video preference).
  - `ThumbnailCandidates_SkipVersionsWhosePrimaryHasAThumbnail` — primary has bytes, a secondary doesn't → no candidate.
  - `ThumbnailCandidates_ExcludeUserThumbnailRows` — only image is `user-thumbnail://x` → no candidate.
  - `ThumbnailCandidates_IncludeModelsWithoutCivitaiId` — `CivitaiId = null`, `file://` image row → candidate present.
  - `ThumbnailCandidates_NeverSelectTheBlobColumn` — capture SQL via the fixture's `LogTo` wiring (copy from `SyncStateRepositoryTests.cs` around `:760`), assert `NotContain("ThumbnailData")` **on the SELECT list** — note the flag comparison compiles to `ThumbnailData IS NOT NULL`/`length()`, so assert the projection discipline the same way the existing test does (read that test first and mirror its exact technique).
  - `ThumbnailCandidates_ParityWithPrimaryImageProperty` — seed 4 mixed versions, assert the candidate ImageId equals `version.PrimaryImage.Id` loaded via the full graph for each.
  - Predicate table test: `(null failure, null attempted, no force) → due`; `(attempted now, failure null) → not due`; `(attempted now, HttpError) → not due`; `(attempted now-2d, HttpError) → due`; `(attempted now-2d, Http404) → NOT due`; `(attempted now, Corrupt) → due`; `(attempted now, Http404, force) → due`.
- [ ] **Step 2: RED, Step 3: implement, Step 4: green.** `GetImageByIdAsync` = `Context.ModelImages.FirstOrDefaultAsync(i => i.Id == imageId, ct)` (tracking — the step mutates it).
- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(sync): thumbnail candidate selection mirrors PrimaryImage, with per-image retry windows"
```

### Task 6: ThumbnailWriter + ThumbnailsStep + registration

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Thumbnails/ThumbnailWriter.cs`
- Create: `DiffusionNexus.Service/Services/Sync/Steps/ThumbnailsStep.cs`
- Modify: `DiffusionNexus.Service/Services/Sync/SyncServiceCollectionExtensions.cs` (register step after `FetchImagesStep`; `AddHttpClient<IThumbnailProvider, ThumbnailProvider>` with 30 s timeout + `DiffusionNexus/1.0` UA)
- Test: `DiffusionNexus.Tests/Sync/Service/Steps/ThumbnailsStepTests.cs`, `DiffusionNexus.Tests/Sync/Service/Thumbnails/ThumbnailWriterTests.cs`

**Interfaces (Produces):**
```csharp
public static class ThumbnailWriter
{
    public static void ApplySuccess(ModelImage image, ThumbnailPayload payload, DateTimeOffset now)
    { image.ThumbnailData = payload.Data; image.ThumbnailMimeType = payload.MimeType;
      image.ThumbnailWidth = payload.Width; image.ThumbnailHeight = payload.Height;
      image.ThumbnailAttemptedAt = now; image.ThumbnailFailure = null; }

    public static void ApplyFailure(ModelImage image, string reason, DateTimeOffset now)
    { image.ThumbnailAttemptedAt = now; image.ThumbnailFailure = reason; }   // data left alone
}
```
`ThumbnailsStep` shape (mirror `FetchImagesStep` structure, NOT its grouping):
- ctor `(IServiceScopeFactory scopes, IThumbnailProvider provider, IUnifiedLogger? logger = null)` — **no pacer** (constraint).
- `Kind = SyncStepKind.Thumbnails`, `Description = "Fetch thumbnails"`, `EstimatedPerItem = TimeSpan.FromSeconds(0.4)`.
- `SelectAsync`: own scope → `IAppSettingsService.GetEnabledLoraSourcesAsync` inside the scope → `SelectThumbnailCandidatesAsync` → filter `options.Policy.IsThumbnailDue(c.ThumbnailAttemptedAt, c.ThumbnailFailure, now, options.ForceThumbnails)` → **one `SyncItem` per image**: `new SyncItem(c.ModelId, c.Name, c)`. (Items == HTTP requests, so `LibrarySyncService.BuildPlanStep` needs no new arm — verify the default arm's count/duration and state so in the report.)
- `ExecuteOneAsync`: cast payload to `ThumbnailCandidate` (ArgumentException on mismatch, same as `FetchImagesStep.cs:80-90`); own scope; `now` captured before work; `uow.Models.GetImageByIdAsync(c.ImageId)` → null → `Skip` (deleted mid-run); `image.ThumbnailData is { Length: > 0 }` → `Skip` (filled since selection — note: entity loaded fresh from DB, sentinel never applies here); `provider.ProduceAsync(new(c.Url, c.MediaType, c.LocalPath, AllowVideoDownload: false), ct)`; success → `ApplySuccess` + save + `Success`; failure → `ApplyFailure` + save + `SyncItemResult.Failure(result.Failure)`. Catch ladder: `OperationCanceledException when ct.IsCancellationRequested` → rethrow unstamped; `Exception when SyncFaults.IsItemFault(ex)` → `uow.ClearChangeTracker()`, Warn, `Failure(ex.GetType().Name)`.

- [ ] **Step 1: Write the failing tests**
  - Writer: success sets all six fields + clears failure; failure stamps attempt/reason and leaves existing `ThumbnailData` bytes untouched.
  - Step (fixture + `Mock<IThumbnailProvider>`): `Select_ReturnsOneItemPerDueImage`; `Select_HonoursForceThumbnails` (hard-failed image selected only with force); `Execute_SuccessPersistsBytesAndStamps` (assert via re-read from a fresh scope); `Execute_FailureStampsReasonAndCountsAsFailed`; `Execute_ImageDeletedMidRunSkips`; `Execute_ImageFilledSinceSelectionSkips`; `Execute_NeverPassesAllowVideoDownload` (verify the request the mock received has `AllowVideoDownload == false`); `Execute_CancellationRethrowsUnstamped`.
  - Registration: extend `LibrarySyncServiceTests`' fake-step or DI test to assert the registered `ISyncStep` sequence ends `…, FetchImagesStep, ThumbnailsStep`.
- [ ] **Step 2: RED. Step 3: implement. Step 4: green — run the full Sync filter.**
- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(sync): thumbnails become a sync step — recorded, incremental, poster-only in bulk"
```

### Task 7: Tile scroll path rewired to the provider

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs`
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (per-tile option set `:1437-1439`)
- Test: `DiffusionNexus.Tests/Viewer/ModelTileThumbnailTests.cs` (new; headless-safe pieces only)

**The changes, precisely:**
1. `DownloadThumbnailAsync` (`:1650-1758`): keep the method as the tile's fetch entry, but its body becomes: resolve `IThumbnailProvider` the same way the class resolves `DiffusionNexusCoreDbContext` today (`:1532` — reuse that service-locator seam); build `ThumbnailRequest(image.Url, image.MediaType, <model local path already available on the tile>, AllowVideoDownload: false)`; on success persist via `ThumbnailWriter` + the existing context-save pattern of `PersistThumbnailAsync` (which now also stamps, via the writer) and set the bitmap via `CreateTileBitmap`; on failure persist the failure stamp (`ApplyFailure` + save) and leave the placeholder. **Delete**: `DownloadImageThumbnailAsync` (the naive `width=300` append, `:1782-1791`), `DownloadVideoThumbnailAsync` (`:1798-1871`), `EnsureDecodableBytes` (`:1911-1961`), `IsVideoData` (`:1884-1903`), `ResizeIfOversized` (`:1969-2030` — the provider's 450 px output makes the 1 MB self-heal moot; the `LazyLoadThumbnailFromDbAsync` self-heal call site at `:1405-1418` goes too, replaced by nothing), and `s_thumbnailClient` (`:90-94`) once nothing references it. Videos on the scroll path now cost one ~65 KB poster GET.
2. **Corrupt marking**: at the two `CreateTileBitmap` call sites that decode real DB bytes (`:1311` and the lazy-load one `:1426`), a `null` return on non-empty input → set `image.ThumbnailData = null; ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.Corrupt, DateTimeOffset.UtcNow);` and persist via the same context pattern; log one Warn. Guard so a single tile activation marks at most once (the nulled BLOB makes the branch unreachable on re-entry — assert that in the test).
3. **Scheme guards in `LoadThumbnailFromVersion`** (`:1293-1339`): branch 3's condition becomes "Url starts with http:// or https://"; `user-thumbnail://` with no BLOB → placeholder, no fetch, no exception (fixes the latent `new Uri` crash).
4. `TryLoadLocalPreviewAsync` (`:1468-1598`): delete its private `LocalPreviewExtensions` copy and Skia block; discovery via `LocalPreviewFiles.FindSibling`, encoding via `ThumbnailCodec.Encode`, persistence via `ThumbnailWriter.ApplySuccess` (keep its synthetic `file://` row creation and DB plumbing).
5. Per-tile option set (`LoraViewerViewModel.cs:1437-1439`): add `SyncStepKind.Thumbnails` to the set and `ForceThumbnails: true` (the user asked for this model explicitly; selection still skips images that already have bytes, so force only retries failures).

- [ ] **Step 1: Write the failing tests.** UI VM tests are limited (no Avalonia init): test what is testable headlessly — `CreateTileBitmap` stays `internal static` (existing); add `internal static` seam(s) if the corrupt-marking decision logic is extracted (extract `static bool ShouldMarkCorrupt(byte[]? data, Bitmap? decoded)` or similar and test that); assert the per-tile option set via the existing `LoraViewerViewModelSyncTests` pattern (capture the `SyncOptions` passed to the mocked `ILibrarySyncService` and assert `Steps` contains `Thumbnails` and `ForceThumbnails` is true).
- [ ] **Step 2: RED. Step 3: implement. Step 4: run the Viewer + Sync filters, then build the UI project.**
Run: `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release --no-incremental` — 0 warnings.
- [ ] **Step 5: Commit**
```bash
git add -A && git commit -m "feat(viewer): tiles fetch through the provider — posters for videos, corrupt blobs marked"
```

### Task 8: User-initiated path + sidecar applier delegation

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs` (`TryDownloadMissingThumbnailAsync:1616-1644`)
- Modify: `DiffusionNexus.Service/Services/Sync/SidecarMetadataApplier.cs` (`:677-786`)
- Test: extend `DiffusionNexus.Tests/Sync/Service/SidecarMetadataApplierTests.cs`; extend Task 7's test file

**Changes:**
1. `TryDownloadMissingThumbnailAsync`: fix the sentinel bug — the sibling filter `i.ThumbnailData is null` (`:1620`) becomes `!i.HasThumbnail`; route the chosen image through `DownloadThumbnailAsync`-style provider call but with `AllowVideoDownload: true` (this is the one user-initiated path where the FFmpeg fallback is allowed — plumb a `bool allowVideoDownload` parameter through rather than duplicating the method).
2. `SidecarMetadataApplier.TryApplyLocalThumbnailAsync`: delete the private `LocalPreviewExtensions` (`:677-682`) and inline Skia resize (`:736-750`); use `LocalPreviewFiles.FindSibling` + `ThumbnailCodec.Encode` + `ThumbnailWriter.ApplySuccess` (keep the S4 never-overwrite guard at `:729`, the synthetic-row creation `:759-766`, and the swallow-to-false error contract). Net behavior change: the applier's thumbnails are now 450 px (was 340) and stamp `ThumbnailAttemptedAt` — both intended.
3. On the applier's failure paths that mean "found a sibling but could not use it" (decode failure), stamp the synthetic/existing row with `ApplyFailure(NotDecodable)` only when an image row exists to stamp; a missing sibling stays a plain `false` (no row to stamp — the sync step records `LocalFileMissing` for `file://` rows it selects).

- [ ] **Step 1: Failing tests**: sidecar applier — existing `ApplyAsync_AppliesLocalThumbnail`-family tests (`SidecarMetadataApplierTests.cs:473-545`) updated to also assert `ThumbnailAttemptedAt != null && ThumbnailFailure == null` and payload width ≤ 450; the never-overwrite test must keep passing untouched. New: `ApplyAsync_UndecodableSiblingStampsNotDecodable`. Tile: sentinel-filter regression test on the extracted selection logic (extract `internal static ModelImage? PickStaticSibling(IEnumerable<ModelImage> images)` if needed for testability).
- [ ] **Step 2: RED. Step 3: implement. Step 4: green (Sync + Viewer filters). Step 5: Commit**
```bash
git add -A && git commit -m "feat(sync): sidecar and user paths share the thumbnail pipeline"
```

### Task 9: Docs + full verification

**Files:**
- Modify: `DiffusionNexus.UI/Doc/LoraViewer.md`
- Modify: `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` (tick WP3 in §5)

- [ ] **Step 1: Document** in `LoraViewer.md`: the thumbnails step (per-image items, no pacer, failure reasons + retry table row: soft 1 d / hard force-only / Corrupt immediate), the video poster mechanism and the 0-video-bytes-in-bulk guarantee, the `AllowVideoDownload` boundary, `user-thumbnail://` ownership, the removed `width=300`/oversize self-heal machinery, and a release-note bullet: first sync after this update fetches thumbnails for images that never had one — expected one-time cost ~N × 0.4 s.
- [ ] **Step 2: Full verification**:
  - `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` (known flakes: `GenerationGalleryViewModelTests.TagCloudSearchText…`, `CheckScoreAdapterTests` — rerun once if they fail alone)
  - `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release --no-incremental` — 0 warnings
  - BOM check over every file this plan created (must have none) and CRLF consistency.
- [ ] **Step 3: Commit**
```bash
git add -A && git commit -m "docs(viewer): thumbnail pipeline — reasons, retries, and the poster guarantee"
```

---

## Deviations from the spec, decided here

- **Bounded parallelism (spec §4.2 "thumbnail concurrency (default 4)")**: deferred. Items execute sequentially in the orchestrator's existing loop; at ~0.3-0.4 s per 65 KB CDN GET with no pacer this is already ~4× faster than the old per-item pacing, and cancel/progress semantics stay exact. If the Task 9 acceptance numbers hurt, parallelism belongs in `LibrarySyncService` (Plan E, next to the settings that would tune it).
- **`ImageCacheService` (Infrastructure)**: left alone — nothing in this pipeline consumed it before and nothing does now. Candidate for deletion in Plan D's clone sweep.
- **Xabe.FFmpeg licensing**: out of scope; the dependency stays, but after Task 7 it is only reachable from the explicit user-initiated path.

## Manual acceptance (user, after merge — record numbers in the PR)
1. Bulk sync on the reference library: report shows a Thumbnails row; **0 video bytes** (watch the network or the log — every video URL fetched must contain `transcode=true`).
2. Second run: Thumbnails plans 0.
3. A video-primary tile scrolled into view shows a poster without downloading the MP4.
4. A corrupt BLOB (if one exists) shows placeholder once, is marked, and is re-fetched by the next sync.
