using DiffusionNexus.Domain.Entities;
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
