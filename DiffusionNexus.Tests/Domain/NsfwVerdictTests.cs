using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.Domain;

/// <summary>
/// #647: whether "Show NSFW" off hides a LoRA, and why — one answer for the grid, the hint count
/// and the "Show hidden" list. The user's own choice wins over everything; then the creator's flag;
/// then the picture the tile shows.
/// </summary>
public sealed class NsfwVerdictTests
{
    private static readonly DateTimeOffset Checked = new(2026, 10, 23, 9, 0, 0, TimeSpan.Zero);

    private static ModelImage Web(int id, NsfwLevel level = NsfwLevel.None) =>
        new() { Id = id, Url = $"https://image.civitai.com/t/u{id}/original=true/{id}.jpeg", MediaType = "image", NsfwLevel = level };

    private static (Model Model, ModelVersion Version) Lora(
        bool? userChoice = null, bool creatorFlag = false, int? versionCivitaiId = 700,
        DateTimeOffset? imagesCheckedAt = null, params ModelImage[] images)
    {
        var model = new Model
        {
            Id = 1, Name = "lora", IsNsfw = creatorFlag, NsfwOverride = userChoice,
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
    public void MarkedSafeByTheUser_IsShown_WhateverCivitaiOrTheCreatorSays()
    {
        var (model, version) = Lora(userChoice: false, creatorFlag: true, images: Web(1, NsfwLevel.XXX));

        NsfwPolicy.Judge(model, version).IsHidden.Should().BeFalse();
    }

    [Fact]
    public void MarkedNsfwByTheUser_IsHidden_EvenWithASafePicture()
    {
        var (model, version) = Lora(userChoice: true, images: Web(1, NsfwLevel.PG));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.MarkedNsfwByYou);
    }

    [Fact]
    public void FlaggedByTheCreator_IsHidden()
    {
        var (model, version) = Lora(creatorFlag: true, images: Web(1, NsfwLevel.PG));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.FlaggedByCreator);
    }

    [Fact]
    public void ARatedNsfwPicture_IsHiddenWithItsRating()
    {
        // The case behind the bogus "no rating yet" count: one other picture is unrated, but the
        // shown one is rated XXX — that rating is the reason.
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [Web(1, NsfwLevel.XXX), Web(2)]);

        NsfwPolicy.Judge(model, version).Should().Be(new NsfwVerdict(NsfwHideReason.RatedOnCivitai, NsfwLevel.XXX));
    }

    [Fact]
    public void AnUnratedPicture_TheBulkRunWillFetch_IsNotRatedYet()
    {
        var (model, version) = Lora(versionCivitaiId: 700, imagesCheckedAt: null, images: Web(1));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.NotRatedYet);
    }

    [Fact]
    public void AnUnratedPicture_CivitaiWasAlreadyAskedAbout_HasNoRating()
    {
        // Deleted from Civitai: the images step asked, got nothing, and stamped the model.
        var (model, version) = Lora(versionCivitaiId: 700, imagesCheckedAt: Checked, images: Web(1));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.NoRating);
    }

    [Fact]
    public void AnUnratedPicture_OfAVersionWithoutACivitaiId_HasNoRating()
    {
        var (model, version) = Lora(versionCivitaiId: null, images: Web(1));

        NsfwPolicy.Judge(model, version).Reason.Should().Be(NsfwHideReason.NoRating);
    }

    [Fact]
    public void ALocalPreviewBesideAnNsfwGallery_IsHiddenByTheGallerysSafestRating()
    {
        var local = new ModelImage { Id = 1, Url = "file:///E:/l/a.preview.png", MediaType = "image" };
        var (model, version) = Lora(imagesCheckedAt: Checked, images: [local, Web(2, NsfwLevel.XXX), Web(3, NsfwLevel.X)]);

        NsfwPolicy.Judge(model, version).Should().Be(new NsfwVerdict(NsfwHideReason.RatedOnCivitai, NsfwLevel.X));
    }

    [Fact]
    public void ALoraWithoutPictures_IsNotHidden()
    {
        var (model, version) = Lora();

        NsfwPolicy.Judge(model, version).IsHidden.Should().BeFalse();
        NsfwPolicy.Judge(model, shownVersion: null).IsHidden.Should().BeFalse();
    }
}
