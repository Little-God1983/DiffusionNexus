using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

/// <summary>
/// #647: a LoRA is SFW or NSFW. The rating comes from the user when they gave one, otherwise from
/// Civitai (the creator's flag, then the picture the tile shows). A LoRA with no rating at all is
/// unrated — hidden with "Show NSFW" off, and listed so the user can rate it.
/// </summary>
public sealed class NsfwVerdictTests
{
    private static readonly DateTimeOffset Checked = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static ModelImage Web(int id, NsfwLevel level = NsfwLevel.None) =>
        new() { Id = id, Url = $"https://image.civitai.com/t/u{id}/original=true/{id}.jpeg", MediaType = "image", NsfwLevel = level };

    private static (Model Model, ModelVersion Version) Lora(
        bool? userRatedNsfw = null, bool creatorFlag = false, int? versionCivitaiId = 700,
        DateTimeOffset? imagesCheckedAt = null, params ModelImage[] images)
    {
        var model = new Model
        {
            Id = 1, Name = "lora", IsNsfw = creatorFlag, UserNsfwRating = userRatedNsfw,
            SyncState = new ModelSyncState { ModelId = 1, ImagesCheckedAt = imagesCheckedAt },
        };
        var version = new ModelVersion { Id = 10, CivitaiId = versionCivitaiId, Model = model };
        foreach (var image in images) version.Images.Add(image);
        model.Versions.Add(version);
        return (model, version);
    }

    [Fact]
    public void ASafePicture_IsNotHidden()
    {
        var (model, version) = Lora(images: Web(1, NsfwLevel.PG13));

        NsfwPolicy.Judge(model, version).Should().Be(new NsfwVerdict(NsfwHideReason.None));
    }

    [Fact]
    public void RatedSfwByTheUser_IsShown_WhateverCivitaiOrTheCreatorSays()
    {
        var (model, version) = Lora(userRatedNsfw: false, creatorFlag: true, images: Web(1, NsfwLevel.XXX));

        NsfwPolicy.Judge(model, version).IsHidden.Should().BeFalse();
    }

    [Fact]
    public void RatedNsfwByTheUser_IsHidden_EvenWithASafePicture()
    {
        var (model, version) = Lora(userRatedNsfw: true, images: Web(1, NsfwLevel.PG));

        var verdict = NsfwPolicy.Judge(model, version);
        verdict.Reason.Should().Be(NsfwHideReason.RatedNsfwByYou);
        verdict.IsUnrated.Should().BeFalse();
    }

    [Fact]
    public void FlaggedByTheCreator_IsHidden()
    {
        var (model, version) = Lora(creatorFlag: true, images: Web(1, NsfwLevel.PG));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.FlaggedByCreator);
    }

    [Fact]
    public void ARatedNsfwPicture_IsHiddenWithItsRating_AndIsNotUnrated()
    {
        // One other picture is unrated, but the shown one is rated XXX: the LoRA is rated.
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [Web(1, NsfwLevel.XXX), Web(2)]);

        var verdict = NsfwPolicy.Judge(model, version);
        verdict.Should().Be(new NsfwVerdict(NsfwHideReason.RatedOnCivitai, NsfwLevel.XXX));
        verdict.IsUnrated.Should().BeFalse();
    }

    [Fact]
    public void AnUnratedPicture_TheBulkRunWillFetch_IsNotRatedYet()
    {
        var (model, version) = Lora(versionCivitaiId: 700, imagesCheckedAt: null, images: Web(1));

        var verdict = NsfwPolicy.Judge(model, version);
        verdict.Reason.Should().Be(NsfwHideReason.NotRatedYet);
        verdict.IsUnrated.Should().BeTrue();
    }

    [Fact]
    public void AnUnratedPicture_CivitaiWasAlreadyAskedAbout_HasNoRating()
    {
        // Deleted from Civitai: the images step asked, got nothing, and stamped the model.
        var (model, version) = Lora(versionCivitaiId: 700, imagesCheckedAt: Checked, images: Web(1));

        var verdict = NsfwPolicy.Judge(model, version);
        verdict.Reason.Should().Be(NsfwHideReason.NoRating);
        verdict.IsUnrated.Should().BeTrue();
    }

    [Fact]
    public void AnUnratedPicture_OfAVersionWithoutACivitaiId_HasNoRating()
    {
        var (model, version) = Lora(versionCivitaiId: null, images: Web(1));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.NoRating);
    }

    [Fact]
    public void ALocalPreviewBesideAnNsfwGallery_IsHiddenByTheGallerysMildestRating()
    {
        var local = new ModelImage { Id = 1, Url = "file:///E:/l/a.preview.png", MediaType = "image" };
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [local, Web(2, NsfwLevel.XXX), Web(3, NsfwLevel.X)]);

        NsfwPolicy.Judge(model, version).Should().Be(new NsfwVerdict(NsfwHideReason.RatedOnCivitai, NsfwLevel.X));
    }

    /// <summary>
    /// Review: the gallery fallback must not name a safe rating for a hidden LoRA. A PG video
    /// beside an XXX still: the local preview is judged by the NSFW ratings (XXX), not by the
    /// video's PG — "hidden, rated PG" contradicts itself.
    /// </summary>
    [Fact]
    public void ALocalPreviewFallback_IgnoresSafeRatings()
    {
        var local = new ModelImage { Id = 1, Url = "file:///E:/l/a.preview.png", MediaType = "image" };
        var video = Web(2, NsfwLevel.PG);
        video.MediaType = "video";
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [local, video, Web(3, NsfwLevel.XXX)]);

        NsfwPolicy.Judge(model, version).Should().Be(new NsfwVerdict(NsfwHideReason.RatedOnCivitai, NsfwLevel.XXX));
    }

    /// <summary>
    /// A local preview beside Civitai pictures that are all rated safe but are videos: nothing NSFW
    /// is rated, so the hidden preview has no rating — not "rated PG".
    /// </summary>
    [Fact]
    public void ALocalPreviewBesideOnlySafeVideos_HasNoRating()
    {
        var local = new ModelImage { Id = 1, Url = "file:///E:/l/a.preview.png", MediaType = "image" };
        var video = Web(2, NsfwLevel.PG);
        video.MediaType = "video";
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [local, video]);

        var verdict = NsfwPolicy.Judge(model, version);
        verdict.Reason.Should().Be(NsfwHideReason.NoRating);
        verdict.IsHidden.Should().BeTrue();
    }

    /// <summary>
    /// Review round 3: in a grouped tile the shown version can belong to another model row than
    /// the tile's primary. Whether Civitai was already asked is that row's stamp, not the primary's.
    /// </summary>
    [Fact]
    public void TheShownVersionsOwnRow_DecidesWhetherCivitaiWasAsked()
    {
        var (primary, _) = Lora(imagesCheckedAt: null);
        var (otherRow, otherVersion) = Lora(versionCivitaiId: 700, imagesCheckedAt: Checked, images: Web(1));

        NsfwPolicy.Judge(primary, otherVersion).Reason.Should().Be(NsfwHideReason.NoRating,
            "the shown version's row was already asked and Civitai had nothing");
        otherRow.SyncState!.ImagesCheckedAt.Should().Be(Checked);
    }

    [Fact]
    public void ALoraWithoutPictures_IsNotHidden()
    {
        var (model, version) = Lora();

        NsfwPolicy.Judge(model, version).IsHidden.Should().BeFalse();
        NsfwPolicy.Judge(model, shownVersion: null).IsHidden.Should().BeFalse();
    }

    /// <summary>
    /// What Civitai says about the LoRA, whatever the user rated it — the "original rating" kept
    /// when the user changes it.
    /// </summary>
    [Fact]
    public void CivitaiRatingLabel_DescribesCivitaisVerdict_IgnoringTheUsersRating()
    {
        NsfwPolicy.CivitaiRatingLabel(Lora(userRatedNsfw: false, images: Web(1, NsfwLevel.XXX)).Model,
            Lora(userRatedNsfw: false, images: Web(1, NsfwLevel.XXX)).Version).Should().Be("XXX");

        var (pgModel, pgVersion) = Lora(userRatedNsfw: true, images: Web(1, NsfwLevel.PG13));
        NsfwPolicy.CivitaiRatingLabel(pgModel, pgVersion).Should().Be("PG-13");

        var (flagged, flaggedVersion) = Lora(creatorFlag: true, images: Web(1, NsfwLevel.PG));
        NsfwPolicy.CivitaiRatingLabel(flagged, flaggedVersion).Should().Be("NSFW (creator's flag)");

        var (unrated, unratedVersion) = Lora(imagesCheckedAt: Checked, images: Web(1));
        NsfwPolicy.CivitaiRatingLabel(unrated, unratedVersion).Should().Be("unrated");

        var (none, noneVersion) = Lora();
        NsfwPolicy.CivitaiRatingLabel(none, noneVersion).Should().Be("unrated");
    }
}
