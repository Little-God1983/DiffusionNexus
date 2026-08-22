using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The tile's own thumbnail decisions, after the scroll path was rewired onto the shared provider
/// pipeline (#521 Plan B, Task 7). The fetch itself is not testable here — no Avalonia platform is
/// initialised, so nothing that decodes a <c>Bitmap</c> can run — but every decision the tile makes
/// before and after that fetch is a static predicate, and those are what this file pins.
/// </summary>
/// <remarks>
/// Two of these guard historical production incidents that the deleted code used to carry:
/// oversized legacy BLOBs (up to 25 MB, written by the old naive <c>width=300</c> fetch) must still
/// shrink on first read, and a BLOB that cannot be decoded must now be recorded rather than shown
/// as an empty tile forever.
/// <para>
/// Task 8 adds three more: the self-heal may only persist a re-encode that actually shrank, the
/// scroll path must consult the recorded failure before spending another request, and the
/// user-initiated sibling pick must not mistake a deferred sentinel or an empty BLOB for a
/// thumbnail that exists.
/// </para>
/// </remarks>
public class ModelTileThumbnailTests
{
    private static readonly byte[] Garbage = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

    [Fact]
    public void CorruptMarking_TriggersWhenStoredBytesCannotBeDecoded()
    {
        ModelTileViewModel.ShouldMarkCorrupt(Garbage, decoded: false).Should().BeTrue(
            "bytes are in the database and nothing can render them — that is a fact about the row, not about this tile");
    }

    /// <summary>
    /// The at-most-once guard, and the whole reason marking nulls the BLOB rather than merely
    /// stamping it: a second activation re-enters the same decode path, and with no bytes left the
    /// branch that marks is unreachable. There is no counter to keep in sync.
    /// </summary>
    [Fact]
    public void CorruptMarking_IsUnreachableOnceTheBlobHasBeenNulled()
    {
        var image = new ModelImage { Url = "https://image.civitai.com/abc/width=450/still.jpeg", ThumbnailData = Garbage };

        ModelTileViewModel.ShouldMarkCorrupt(image.ThumbnailData, decoded: false).Should().BeTrue();

        // What the marking site does to the entity.
        image.ThumbnailData = null;
        ThumbnailWriter.ApplyFailure(image, ThumbnailFailureReason.Corrupt, DateTimeOffset.UtcNow);

        ModelTileViewModel.ShouldMarkCorrupt(image.ThumbnailData, decoded: false).Should().BeFalse(
            "one tile activation marks at most once");
        image.ThumbnailFailure.Should().Be(ThumbnailFailureReason.Corrupt);
    }

    [Fact]
    public void CorruptMarking_LeavesADecodedThumbnailAlone()
    {
        ModelTileViewModel.ShouldMarkCorrupt(Garbage, decoded: true).Should().BeFalse();
    }

    [Fact]
    public void CorruptMarking_SaysNothingAboutARowThatHasNoBytes()
    {
        ModelTileViewModel.ShouldMarkCorrupt(null, decoded: false).Should().BeFalse();
        ModelTileViewModel.ShouldMarkCorrupt([], decoded: false).Should().BeFalse(
            "an empty BLOB is a missing thumbnail, which the fetch path answers — not a corrupt one");
    }

    /// <summary>
    /// The deferred sentinel is a marker, not a thumbnail. Decoding it fails by construction, and
    /// mistaking that for corruption would null a row whose real bytes are sitting in the database.
    /// </summary>
    [Fact]
    public void CorruptMarking_NeverMistakesTheDeferredSentinelForCorruption()
    {
        ModelTileViewModel.ShouldMarkCorrupt(ModelImage.ThumbnailNotLoadedSentinel, decoded: false)
            .Should().BeFalse();
    }

    /// <summary>
    /// Branch 3 of the tile's load ladder used to be "any URL that is not <c>file://</c>", which
    /// sent <c>user-thumbnail://{guid}</c> rows into an HTTP fetch. Only http(s) is fetchable.
    /// </summary>
    [Theory]
    [InlineData("https://image.civitai.com/abc/width=450/still.jpeg", true)]
    [InlineData("HTTPS://image.civitai.com/abc/width=450/still.jpeg", true)]
    [InlineData("http://example.com/a.png", true)]
    [InlineData(@"file://C:\loras\a.preview.png", false)]
    [InlineData("user-thumbnail://8d0a1f4c9b3e4f1aa0c2d5e6f7081923", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void FetchableUrl_IsHttpOrHttpsAndNothingElse(string? url, bool expected)
    {
        ModelTileViewModel.IsFetchableUrl(url).Should().Be(expected);
    }

    [Fact]
    public void OversizeSelfHeal_TriggersOnlyAboveTheOneMegabyteLegacyCap()
    {
        ModelTileViewModel.NeedsOversizeSelfHeal(new byte[1_048_576]).Should().BeFalse(
            "the cap itself is fine — only what exceeds it is legacy bloat");
        ModelTileViewModel.NeedsOversizeSelfHeal(new byte[1_048_577]).Should().BeTrue();
        ModelTileViewModel.NeedsOversizeSelfHeal(null).Should().BeFalse();
    }

    /// <summary>
    /// The OOM / DB-bloat incident, re-pinned on the shared pipeline: the old naive fetch appended
    /// <c>width=300</c> to a URL the CDN was free to ignore, and stored whatever came back — up to
    /// 25 MB per row. The sync step only ever selects images <i>without</i> bytes, so nothing else
    /// will ever shrink those rows; the tile's first read of one has to.
    /// </summary>
    [Fact]
    public void OversizeSelfHeal_ShrinksALegacyBlobThroughTheSharedCodec()
    {
        var oversized = CreateNoisyPng(1200, 1200);
        oversized.Length.Should().BeGreaterThan(1_048_576, "the test input must exceed the self-heal threshold");
        ModelTileViewModel.NeedsOversizeSelfHeal(oversized).Should().BeTrue();

        var reencoded = ThumbnailCodec.Encode(oversized);

        reencoded.Should().NotBeNull();
        reencoded!.Data.Length.Should().BeLessThan(oversized.Length);
        reencoded.Data.Length.Should().BeLessThanOrEqualTo(1_048_576, "the re-encoded thumbnail must fit under the cap");
        reencoded.Width.Should().Be(ThumbnailCodec.TargetWidth, "the self-heal produces the same thumbnail as every other producer");
    }

    /// <summary>
    /// A re-encode that did not shrink the row must not be written back. Nothing else stops it:
    /// the oversize check reads the stored length, so a BLOB that cannot get smaller stays over
    /// the threshold and would be decoded, re-encoded and saved again on every single activation
    /// of the tile — for the whole life of the row.
    /// </summary>
    [Fact]
    public void SelfHealPersist_OnlyWhenTheReEncodeActuallyShrank()
    {
        var original = new byte[2_000_000];

        ModelTileViewModel.ShouldPersistSelfHeal(original, Payload(1_999_999))
            .Should().BeTrue("one byte smaller is still smaller — the row converges");

        ModelTileViewModel.ShouldPersistSelfHeal(original, Payload(2_000_000))
            .Should().BeFalse("a re-encode that changed nothing is a write, a save and a decode for nothing");

        ModelTileViewModel.ShouldPersistSelfHeal(original, Payload(2_000_001))
            .Should().BeFalse("a narrow-but-enormous BLOB can grow under JPEG — never persist that");
    }

    [Fact]
    public void SelfHealPersist_NeverForAnUndecodableBlob()
    {
        ModelTileViewModel.ShouldPersistSelfHeal(new byte[2_000_000], null).Should().BeFalse(
            "an oversize BLOB that will not decode is corrupt, and the corrupt path owns it");
    }

    // ------------------------------------------------- the scroll path's retry gate (Task 8, RC)

    private static readonly DateTimeOffset Now = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ScrollFetch_IsDueForARowNothingHasEverTried()
    {
        ModelTileViewModel.IsScrollFetchDue(new ModelImage(), Now).Should().BeTrue();
    }

    /// <summary>
    /// The incident this gate exists for: a poster URL that 404s was re-fetched on every scroll
    /// past the tile — a GET, a DI scope and a SaveChanges each time — because the failure stamp
    /// was written and then never read by anything on the UI side.
    /// </summary>
    [Fact]
    public void ScrollFetch_IsNeverDueAgainForAHardFailure()
    {
        var image = new ModelImage
        {
            ThumbnailAttemptedAt = Now - TimeSpan.FromDays(365),
            ThumbnailFailure = ThumbnailFailureReason.Http404,
        };

        ModelTileViewModel.IsScrollFetchDue(image, Now).Should().BeFalse(
            "the asset is gone; asking again a year later costs a request to learn nothing");
    }

    [Fact]
    public void ScrollFetch_ComesBackForASoftFailureOnceTheWindowHasPassed()
    {
        var image = new ModelImage
        {
            ThumbnailAttemptedAt = Now - SyncRetryPolicy.Default.ErrorRetryAfter + TimeSpan.FromMinutes(1),
            ThumbnailFailure = ThumbnailFailureReason.HttpError,
        };

        ModelTileViewModel.IsScrollFetchDue(image, Now).Should().BeFalse("still inside the retry window");

        image.ThumbnailAttemptedAt = Now - SyncRetryPolicy.Default.ErrorRetryAfter;

        ModelTileViewModel.IsScrollFetchDue(image, Now).Should().BeTrue("the CDN coming back is exactly the case to catch");
    }

    [Fact]
    public void ScrollFetch_IsDueImmediatelyAfterACorruptBlobWasDropped()
    {
        var image = new ModelImage { ThumbnailAttemptedAt = Now, ThumbnailFailure = ThumbnailFailureReason.Corrupt };

        ModelTileViewModel.IsScrollFetchDue(image, Now).Should().BeTrue(
            "nothing failed at the source — the row simply has no bytes any more");
    }

    // ------------------------------------------- the user-initiated static-sibling pick (Task 8)

    /// <summary>
    /// The sentinel bug. The filter asked <c>ThumbnailData is null</c>, and the light tile query
    /// hands every row that has bytes a one-byte marker instead of them — so a sibling that was
    /// merely deferred looked identical to one holding a real thumbnail, and an empty BLOB (bytes
    /// present, thumbnail absent) was mistaken for a thumbnail outright.
    /// </summary>
    [Fact]
    public void StaticSibling_TreatsAnEmptyBlobAsTheMissingThumbnailItIs()
    {
        var empty = Still(id: 1, sortOrder: 0, thumbnail: []);

        ModelTileViewModel.PickStaticSibling([empty]).Should().BeSameAs(empty);
    }

    [Fact]
    public void StaticSibling_SkipsARowThatCanAlreadyBeDisplayed()
    {
        var has = Still(id: 1, sortOrder: 0, thumbnail: [1, 2, 3]);
        var hasnt = Still(id: 2, sortOrder: 1);

        ModelTileViewModel.PickStaticSibling([has, hasnt]).Should().BeSameAs(hasnt);
        ModelTileViewModel.PickStaticSibling([has]).Should().BeNull();
    }

    /// <summary>
    /// The other half of the sentinel, and the one that bites the other way. A deferred row has
    /// real bytes sitting in the database — the light query simply did not load them — so selecting
    /// it would send the download straight into <c>ApplySuccess</c>, overwriting stored bytes with
    /// freshly fetched ones. Including, on a version whose sort-0 row is not the primary image,
    /// a thumbnail the user uploaded by hand.
    /// </summary>
    [Fact]
    public void StaticSibling_IsNeverADeferredRowWhoseBytesAreOnlyUnloaded()
    {
        var deferred = Still(id: 1, sortOrder: 0, thumbnail: ModelImage.ThumbnailNotLoadedSentinel);
        var genuinelyEmpty = Still(id: 2, sortOrder: 1);

        deferred.IsThumbnailDeferred.Should().BeTrue("guard the fixture: reference equality, not content");

        ModelTileViewModel.PickStaticSibling([deferred]).Should().BeNull(
            "those bytes exist and re-fetching would overwrite them");
        ModelTileViewModel.PickStaticSibling([deferred, genuinelyEmpty]).Should().BeSameAs(genuinelyEmpty);
    }

    /// <summary>
    /// A stored URL is not guaranteed to parse. A truncated download and a legacy relative path are
    /// both in the wild, and the video test used to hand one straight to <c>new Uri(...)</c> —
    /// which throws, out of the user's click on "download the missing thumbnail".
    /// </summary>
    [Theory]
    [InlineData("https://")]                     // truncated: scheme, no host
    [InlineData("images/preview.png")]           // legacy relative path: no scheme at all
    [InlineData("https://[unclosed/a.png")]      // malformed IPv6 literal
    public void StaticSibling_SurvivesAMalformedStoredUrl(string url)
    {
        // MediaType null, so the video test falls back to inspecting the URL.
        var malformed = new ModelImage { Id = 1, Url = url, SortOrder = 0 };

        var act = () => ModelTileViewModel.PickStaticSibling([malformed]);

        act.Should().NotThrow("a URL nobody can parse is a bad candidate, not an exception");
        act().Should().BeSameAs(malformed, "unparseable is not video — it stays eligible");
    }

    [Fact]
    public void StaticSibling_IsNeverAnotherVideoAndNeverAUrllessRow()
    {
        var video = new ModelImage { Id = 1, Url = "https://image.civitai.com/abc/a.mp4", MediaType = "video", SortOrder = 0 };
        var urlless = new ModelImage { Id = 2, Url = string.Empty, MediaType = "image", SortOrder = 1 };

        ModelTileViewModel.PickStaticSibling([video, urlless]).Should().BeNull();
    }

    [Fact]
    public void StaticSibling_PrefersSfwThenSortOrder()
    {
        var nsfwFirst = Still(id: 1, sortOrder: 0, nsfw: true);
        var sfwLate = Still(id: 2, sortOrder: 9);
        var sfwEarly = Still(id: 3, sortOrder: 4);

        ModelTileViewModel.PickStaticSibling([nsfwFirst, sfwLate, sfwEarly]).Should().BeSameAs(sfwEarly);
        ModelTileViewModel.PickStaticSibling([nsfwFirst]).Should().BeSameAs(nsfwFirst, "an NSFW still beats no still at all");
    }

    private static ModelImage Still(int id, int sortOrder, byte[]? thumbnail = null, bool nsfw = false) => new()
    {
        Id = id,
        Url = FormattableString.Invariant($"https://image.civitai.com/abc/width=450/{id}.jpeg"),
        MediaType = "image",
        SortOrder = sortOrder,
        IsNsfw = nsfw,
        ThumbnailData = thumbnail,
    };

    private static ThumbnailPayload Payload(int size) => new(new byte[size], "image/jpeg", 450, 450);

    /// <summary>
    /// Builds a PNG of random noise so it does not compress away — a reliable way to get a
    /// decodable image comfortably over the 1 MB threshold without any test asset.
    /// </summary>
    private static byte[] CreateNoisyPng(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);

        var pixels = new byte[info.BytesSize];
        new Random(42).NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
