using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;

namespace DiffusionNexus.Tests.OnlineTests;

/// <summary>
/// Opt-in canary (see <see cref="OnlineFactAttribute"/>) that pins the live Civitai CDN
/// behaviour <see cref="CivitaiImageUrls.ToVideoPosterUrl"/> depends on: appending
/// <c>transcode=true</c> to the transform segment is what makes the CDN return a JPEG
/// still frame instead of the original video bytes. Not part of the normal test run — it
/// hits the real network and will go red the day Civitai changes CDN behaviour, which is
/// the point.
/// </summary>
public class CivitaiCdnPosterCanaryTests
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    // Verified live 2026-08-22. "Juggernaut XL" (model 133005, version 1759168) is one of
    // Civitai's most-downloaded checkpoints and has carried an example video for a long time,
    // so this asset should stay up far longer than a niche upload would.
    // https://civitai.com/models/133005 (modelVersions[].images[] entry with type == "video")
    private const string VideoUrl =
        "https://image.civitai.com/xG1nkqKTMzGDvpLrqFT7WA/1dbfbc3e-ffaf-49aa-83e1-38222a6d9a73/original=true/75044257.mp4";

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
