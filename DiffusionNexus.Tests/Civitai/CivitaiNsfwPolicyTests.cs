using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai rates content with a numeric nsfwLevel bitmask (1=PG, 2=PG13, 4=R,
/// 8=X, 16=XXX) on both models and images. The model-level "nsfw" boolean only
/// marks models *designated* mature — models with nsfw=false routinely carry
/// X/XXX gallery images (and with our nsfw=true request the API returns every
/// image unfiltered). These tests pin the client-side gating policy.
/// </summary>
public class CivitaiNsfwPolicyTests
{
    private static CivitaiModelImage Image(int? level, string type = "image", string url = "https://img/x.jpeg")
        => new() { Url = url, Type = type, NsfwLevel = level };

    private static CivitaiModel Model(
        bool nsfw = false,
        int nsfwLevel = 0,
        params CivitaiModelImage[] images)
        => new()
        {
            Id = 1,
            Name = "m",
            Nsfw = nsfw,
            NsfwLevel = nsfwLevel,
            ModelVersions = [new CivitaiModelVersion { Images = images }]
        };

    #region Deserialization — numeric nsfwLevel bitmask

    [Fact]
    public void ModelNsfwLevel_DeserializesNumericBitmask()
    {
        var json = """{"id":2782544,"name":"x","nsfw":false,"nsfwLevel":26}""";

        var model = JsonSerializer.Deserialize<CivitaiModel>(json)!;

        model.NsfwLevel.Should().Be(26);
    }

    [Fact]
    public void ImageNsfwLevel_DeserializesNumericBitmask()
    {
        var json = """{"url":"https://img/x.jpeg","nsfwLevel":16}""";

        var image = JsonSerializer.Deserialize<CivitaiModelImage>(json)!;

        image.NsfwLevel.Should().Be(16);
    }

    #endregion

    #region IsCardNsfw

    [Fact]
    public void IsCardNsfw_FlaggedModel_IsNsfw_EvenWithSafeImages()
    {
        var model = Model(nsfw: true, nsfwLevel: 60, Image(2));

        CivitaiNsfwPolicy.IsCardNsfw(model).Should().BeTrue();
    }

    [Fact]
    public void IsCardNsfw_UnflaggedModelWithAtLeastOneSafeImage_IsNotNsfw()
    {
        // Real-world shape (model 2782544): nsfw=false, first image XXX, second PG13.
        var model = Model(nsfw: false, nsfwLevel: 26, Image(16), Image(2));

        CivitaiNsfwPolicy.IsCardNsfw(model).Should().BeFalse();
    }

    [Fact]
    public void IsCardNsfw_UnflaggedModelWithOnlyAdultImages_IsNsfw()
    {
        var model = Model(nsfw: false, nsfwLevel: 24, Image(8), Image(16));

        CivitaiNsfwPolicy.IsCardNsfw(model).Should().BeTrue();
    }

    [Fact]
    public void IsCardNsfw_NoImages_UsesModelLevelBitmask()
    {
        CivitaiNsfwPolicy.IsCardNsfw(Model(nsfwLevel: 24)).Should().BeTrue();   // X|XXX only
        CivitaiNsfwPolicy.IsCardNsfw(Model(nsfwLevel: 3)).Should().BeFalse();   // PG|PG13
        CivitaiNsfwPolicy.IsCardNsfw(Model(nsfwLevel: 0)).Should().BeFalse();   // unrated, not flagged
    }

    #endregion

    #region SelectPreview

    [Fact]
    public void SelectPreview_NsfwOff_SkipsAdultImages_PicksFirstSafe()
    {
        var xxx = Image(16);
        var safe = Image(2);
        var model = Model(images: [xxx, safe]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: false).Should().BeSameAs(safe);
    }

    [Fact]
    public void SelectPreview_NsfwOn_KeepsFirstImage_EvenIfAdult()
    {
        var xxx = Image(16);
        var safe = Image(2);
        var model = Model(images: [xxx, safe]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: true).Should().BeSameAs(xxx);
    }

    [Fact]
    public void SelectPreview_PrefersStillImageOverVideo_AmongCandidates()
    {
        var safeVideo = Image(2, type: "video", url: "https://img/x.mp4");
        var safeStill = Image(1);
        var model = Model(images: [safeVideo, safeStill]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: false).Should().BeSameAs(safeStill);
    }

    [Fact]
    public void SelectPreview_NsfwOff_UnratedImage_IsNotConsideredSafe()
    {
        // With nsfw=true image passthrough an unrated image could be anything —
        // treat it as unsafe rather than risk an adult thumbnail.
        var unrated = Image(null);
        var safe = Image(2);
        var model = Model(images: [unrated, safe]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: false).Should().BeSameAs(safe);
    }

    [Fact]
    public void SelectPreview_NsfwOff_NoSafeImage_ReturnsNull()
    {
        var model = Model(images: [Image(8), Image(16)]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: false).Should().BeNull();
    }

    [Fact]
    public void SelectPreview_IgnoresImagesWithoutUrl()
    {
        var blank = Image(2, url: " ");
        var safe = Image(2);
        var model = Model(images: [blank, safe]);

        CivitaiNsfwPolicy.SelectPreview(model, showNsfw: false).Should().BeSameAs(safe);
    }

    #endregion
}
