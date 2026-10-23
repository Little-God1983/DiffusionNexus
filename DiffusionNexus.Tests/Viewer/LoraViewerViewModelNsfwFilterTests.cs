using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// #631: with "Show NSFW" off the Installed tab shows nothing NSFW. A tile is hidden when the
/// model is flagged NSFW or when the picture the tile shows is not rated safe (PG/PG-13); a tile
/// that stays shows a safe picture. Before, only the creator's model flag was checked, so an
/// SFW-flagged model with an XXX preview stayed on screen.
/// <para>
/// #647: a LoRA is SFW or NSFW. Unrated LoRAs — no rating from Civitai or the user — are hidden,
/// counted in a hint and listed by "Show unrated" so the user can rate them. A rating the user
/// gives is the LoRA's rating from then on, marked as changed by them with the original kept.
/// </para>
/// </summary>
public class LoraViewerViewModelNsfwFilterTests
{
    private static ModelImage Image(string name, NsfwLevel level, int sortOrder = 0) => new()
    {
        Url = $"https://image.civitai.com/x/{name}.jpeg",
        MediaType = "image",
        NsfwLevel = level,
        SortOrder = sortOrder,
    };

    private static Model NewModel(string name, bool flaggedNsfw, params ModelImage[] images)
    {
        var model = new Model { Name = name, Type = ModelType.LORA, IsNsfw = flaggedNsfw };
        var version = new ModelVersion { Name = $"{name} v1", BaseModelRaw = "SDXL 1.0", Model = model };
        version.Files.Add(new ModelFile { FileName = $"{name}.safetensors", IsPrimary = true, ModelVersion = version });
        foreach (var image in images)
        {
            image.ModelVersion = version;
            version.Images.Add(image);
        }
        model.Versions.Add(version);
        return model;
    }

    /// <summary>An unrated LoRA the bulk Download Metadata run would still fetch.</summary>
    private static Model NotRatedYet(string name)
    {
        var model = NewModel(name, flaggedNsfw: false, Image(name, NsfwLevel.None));
        model.Versions.First().CivitaiId = 700;
        return model;
    }

    /// <summary>An unrated LoRA Civitai has no rating for (no version id: it cannot be asked).</summary>
    private static Model NoRating(string name) => NewModel(name, flaggedNsfw: false, Image(name, NsfwLevel.None));

    private static LoraViewerViewModel ViewModelWith(params Model[] models)
    {
        var vm = new LoraViewerViewModel();
        vm.AllTiles.Clear();
        foreach (var model in models) vm.AllTiles.Add(ModelTileViewModel.FromModel(model));
        return vm;
    }

    private static IEnumerable<string> Visible(LoraViewerViewModel vm) => vm.FilteredTiles.Select(t => t.DisplayName);

    [Fact]
    public void SfwFlaggedModelWithOnlyNsfwImages_IsHidden()
    {
        var vm = ViewModelWith(NewModel("explicit gallery", flaggedNsfw: false, Image("a", NsfwLevel.XXX), Image("b", NsfwLevel.R, 1)));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEmpty();
    }

    [Fact]
    public void SfwFlaggedModelWithOneSafeImage_StaysAndShowsThatImage()
    {
        var safe = Image("safe", NsfwLevel.PG13, sortOrder: 1);
        var vm = ViewModelWith(NewModel("mixed", flaggedNsfw: false, Image("xxx", NsfwLevel.XXX), safe));

        vm.ShowNsfw = false;

        var tile = vm.FilteredTiles.Should().ContainSingle().Subject;
        tile.SelectedVersion!.PrimaryImage.Should().BeSameAs(safe);
    }

    /// <summary>
    /// #631 review: most downloaded LoRAs carry a local preview file (saved from the Civitai
    /// gallery, often the X cover) as their first image row. It must not slip through as "safe"
    /// beside Civitai pictures rated X; beside a PG picture the tile shows the PG one.
    /// </summary>
    [Fact]
    public void LocalPreviewBesideCivitaiImages_IsJudgedByThem()
    {
        ModelImage Local() => new() { Url = "file:///E:/Loras/preview.png", MediaType = "image" };
        var pg = Image("pg", NsfwLevel.PG, sortOrder: 1);
        var vm = ViewModelWith(
            NewModel("explicit cover", flaggedNsfw: false, Local(), Image("x", NsfwLevel.X, sortOrder: 1)),
            NewModel("safe gallery", flaggedNsfw: false, Local(), pg));

        vm.ShowNsfw = false;

        var tile = vm.FilteredTiles.Should().ContainSingle().Subject;
        tile.DisplayName.Should().Be("safe gallery");
        tile.SelectedVersion!.PrimaryImage.Should().BeSameAs(pg);
    }

    [Fact]
    public void NsfwFlaggedModel_IsHiddenEvenWithSafeImages()
    {
        var vm = ViewModelWith(NewModel("mature model", flaggedNsfw: true, Image("pg", NsfwLevel.PG)));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEmpty();
    }

    [Fact]
    public void UnratedCivitaiImages_AreHidden_LocalPreviewsAndNoImagesStay()
    {
        var local = new ModelImage { Url = "file:///E:/Loras/local.preview.png", MediaType = "image" };
        var vm = ViewModelWith(
            NoRating("unrated"),
            NewModel("local preview", flaggedNsfw: false, local),
            NewModel("no images", flaggedNsfw: false));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEquivalentTo("local preview", "no images");
        vm.UnratedCount.Should().Be(1);
    }

    /// <summary>
    /// #647: the hint and its list are about unrated LoRAs only — not ones Civitai or the creator
    /// rated NSFW — and the number is exactly what the list shows.
    /// </summary>
    [Fact]
    public void TheHintAndTheList_CoverOnlyUnratedLoras()
    {
        var vm = ViewModelWith(
            NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)),
            NewModel("creator flag", flaggedNsfw: true, Image("pg", NsfwLevel.PG)),
            NoRating("deleted from civitai"),
            NotRatedYet("not fetched yet"),
            NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG13)));

        vm.ShowNsfw = false;
        vm.UnratedCount.Should().Be(2);
        vm.ShowUnratedHint.Should().BeTrue();
        vm.UnratedHint.Should().Be("2 LoRAs in this view have no NSFW rating and are hidden.");
        vm.ShowUnratedButtonText.Should().Be("Show unrated (2)");

        vm.ShowUnratedCommand.Execute(null);

        vm.ShowUnratedOnly.Should().BeTrue();
        vm.ShowUnratedHint.Should().BeFalse();
        Visible(vm).Should().BeEquivalentTo("deleted from civitai", "not fetched yet");
        vm.FilteredModelCount.Should().Be(2, "the list shows exactly what the hint counted");
        vm.FilteredTiles.Should().OnlyContain(t => t.IsInUnratedView && t.IsPreviewHidden && t.IsUnrated,
            "pictures stay covered until the user rates the LoRA");

        vm.BackToAllCommand.Execute(null);
        Visible(vm).Should().BeEquivalentTo("safe");
        vm.FilteredTiles.Should().OnlyContain(t => !t.IsInUnratedView);
    }

    /// <summary>Each unrated tile says why it has no rating, in its tooltip.</summary>
    [Fact]
    public void AnUnratedTile_SaysWhyItHasNoRating()
    {
        var vm = ViewModelWith(NoRating("deleted"), NotRatedYet("pending"));

        var deleted = vm.AllTiles.Single(t => t.DisplayName == "deleted");
        var pending = vm.AllTiles.Single(t => t.DisplayName == "pending");

        deleted.IsUnrated.Should().BeTrue();
        deleted.UnratedTooltip.Should().Contain("Civitai has no NSFW rating").And.Contain("Rate SFW");
        pending.UnratedTooltip.Should().Contain("Not rated yet").And.Contain("Download Metadata");
    }

    /// <summary>
    /// #647: a LoRA is SFW or NSFW — the user's rating replaces Civitai's, both ways, and there is
    /// no third state. The tile shows that the rating was changed by the user, and from what.
    /// </summary>
    [Fact]
    public async Task RatingALora_ReplacesCivitaisRating_AndShowsTheOriginal()
    {
        var vm = ViewModelWith(
            NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)),
            NoRating("unrated"));
        vm.ShowNsfw = false;
        var xxx = vm.AllTiles.Single(t => t.DisplayName == "rated xxx");
        var unrated = vm.AllTiles.Single(t => t.DisplayName == "unrated");

        await vm.SetUserNsfwRatingAsync(xxx, isNsfw: false);
        await vm.SetUserNsfwRatingAsync(unrated, isNsfw: false);

        Visible(vm).Should().BeEquivalentTo("rated xxx", "unrated");
        vm.UnratedCount.Should().Be(0);
        xxx.IsPreviewHidden.Should().BeFalse("the user rated it SFW");
        xxx.IsRatedByUser.Should().BeTrue();
        xxx.UserRatingBadge.Should().Be("SFW · by you");
        xxx.UserRatingTooltip.Should().Be("Rated SFW by you. Civitai: XXX.");
        unrated.UserRatingTooltip.Should().Be("Rated SFW by you. Civitai: unrated.");

        await vm.SetUserNsfwRatingAsync(xxx, isNsfw: true);

        Visible(vm).Should().BeEquivalentTo("unrated");
        xxx.UserRatingBadge.Should().Be("NSFW · by you");
        xxx.UserRatingTooltip.Should().Be("Rated NSFW by you. Civitai: XXX.", "the original stays what Civitai said first");
    }

    /// <summary>
    /// Review: a tile groups several model rows of one LoRA, and the database gets the rating for
    /// all of them. The ones in memory must too — otherwise the tile loses the rating as soon as a
    /// refresh makes another grouped row its primary.
    /// </summary>
    [Fact]
    public async Task RatingAGroupedTile_RatesEveryGroupedModelInMemory()
    {
        var first = NoRating("same lora");
        var second = NoRating("same lora");
        var vm = new LoraViewerViewModel();
        vm.AllTiles.Clear();
        var tile = ModelTileViewModel.FromModelGroup([first, second]);
        vm.AllTiles.Add(tile);

        await vm.SetUserNsfwRatingAsync(tile, isNsfw: false);

        new[] { first, second }.Should().OnlyContain(m => m.UserNsfwRating == false && m.OriginalNsfwRating == "unrated");
    }

    /// <summary>
    /// Review: rating the last unrated LoRA empties the list — the tab goes back to all LoRAs
    /// instead of leaving the user in an empty "Showing the 0 unrated LoRAs" view.
    /// </summary>
    [Fact]
    public async Task RatingTheLastUnratedLora_LeavesTheEmptyList()
    {
        var vm = ViewModelWith(NoRating("only unrated"), NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG)));
        vm.ShowNsfw = false;
        vm.ShowUnratedCommand.Execute(null);

        await vm.SetUserNsfwRatingAsync(vm.FilteredTiles.Single(), isNsfw: false);

        vm.ShowUnratedOnly.Should().BeFalse();
        Visible(vm).Should().BeEquivalentTo("only unrated", "safe");
    }

    /// <summary>
    /// Review: the creator's red NSFW badge contradicts a LoRA the user rated SFW ("SFW · by you"
    /// beside "NSFW"). The user's rating is the LoRA's rating, so the creator badge steps aside.
    /// </summary>
    [Fact]
    public async Task TheCreatorsNsfwBadge_StepsAside_OnceTheUserRated()
    {
        var vm = ViewModelWith(NewModel("flagged", flaggedNsfw: true, Image("pg", NsfwLevel.PG)));
        var tile = vm.AllTiles.Single();
        tile.ShowCreatorNsfwBadge.Should().BeTrue();

        await vm.SetUserNsfwRatingAsync(tile, isNsfw: false);

        tile.ShowCreatorNsfwBadge.Should().BeFalse();
        tile.UserRatingTooltip.Should().Be("Rated SFW by you. Civitai: NSFW (creator's flag).");
    }

    /// <summary>The tile's buttons and menu ask the Installed tab to record the rating.</summary>
    [Fact]
    public void TheTilesRateCommands_RequestTheRating()
    {
        var tile = ModelTileViewModel.FromModel(NoRating("t"));
        var requested = new List<bool>();
        tile.NsfwRatingRequested += (_, isNsfw) => requested.Add(isNsfw);

        tile.RateSfwCommand.Execute(null);
        tile.RateNsfwCommand.Execute(null);

        requested.Should().Equal(false, true);
    }

    /// <summary>The detail view's line: what the LoRA is rated, and by whom.</summary>
    [Fact]
    public async Task TheRatingSummary_SaysWhatTheLoraIsRatedAndByWhom()
    {
        var vm = ViewModelWith(
            NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)),
            NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG)),
            NoRating("unrated"));
        var xxx = vm.AllTiles.Single(t => t.DisplayName == "rated xxx");

        xxx.NsfwRatingSummary.Should().Be("NSFW (Civitai: XXX)");
        vm.AllTiles.Single(t => t.DisplayName == "safe").NsfwRatingSummary.Should().Be("SFW (Civitai: PG)");
        vm.AllTiles.Single(t => t.DisplayName == "unrated").NsfwRatingSummary.Should().Be("Unrated");

        await vm.SetUserNsfwRatingAsync(xxx, isNsfw: false);
        xxx.NsfwRatingSummary.Should().Be("SFW — changed by you (Civitai: XXX)");
    }

    [Fact]
    public void TurningShowNsfwOn_LeavesTheUnratedList()
    {
        var vm = ViewModelWith(NoRating("unrated"));
        vm.ShowNsfw = false;
        vm.ShowUnratedCommand.Execute(null);

        vm.ShowNsfw = true;

        vm.ShowUnratedOnly.Should().BeFalse();
        vm.UnratedCount.Should().Be(0);
        Visible(vm).Should().BeEquivalentTo("unrated");
    }

    /// <summary>
    /// The hint counts models, like the toolbar's "N of M models": a tile that groups two model
    /// rows of the same LoRA counts twice.
    /// </summary>
    [Fact]
    public void TheHintCounts_Models_LikeTheToolbar()
    {
        var vm = new LoraViewerViewModel();
        vm.AllTiles.Clear();
        vm.AllTiles.Add(ModelTileViewModel.FromModelGroup([NoRating("same lora"), NoRating("same lora")]));
        vm.AllTiles.Add(ModelTileViewModel.FromModel(NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG))));

        vm.ShowNsfw = false;

        vm.FilteredModelCount.Should().Be(1);
        vm.UnratedCount.Should().Be(2);
        vm.ShowUnratedButtonText.Should().Be("Show unrated (2)");

        vm.ShowUnratedCommand.Execute(null);
        vm.FilteredModelCount.Should().Be(2);
    }

    /// <summary>
    /// When everything in view is hidden the tab says so — not "add LoRA source folders" as if the
    /// library were gone.
    /// </summary>
    [Fact]
    public void EmptyState_SaysEverythingInViewIsHidden()
    {
        var vm = ViewModelWith(
            NoRating("one"),
            NewModel("two", flaggedNsfw: false, Image("x2", NsfwLevel.X)));

        vm.ShowNsfw = false;

        vm.EmptyStateMessage.Should().Be("All 2 LoRAs in this view are hidden by Show NSFW. Turn Show NSFW on to see them.");
    }

    [Fact]
    public void EmptyState_AsksForSourceFolders_OnlyWhenThereAreNoLoras()
    {
        var vm = ViewModelWith();

        vm.ShowNsfw = false;

        vm.EmptyStateMessage.Should().Be("Add LoRA source folders in Settings, then click Refresh.");
    }

    /// <summary>
    /// The count and the empty-state reason are about the LoRAs the other filters let through: a
    /// search that matches nothing is "no match", even while LoRAs elsewhere are hidden.
    /// </summary>
    [Fact]
    public async Task UnratedCount_OnlyCountsWhatTheOtherFiltersLetThrough()
    {
        var vm = ViewModelWith(
            NoRating("alpha"),
            NewModel("beta", flaggedNsfw: false, Image("pg", NsfwLevel.PG)));
        vm.SearchDebounceInterval = TimeSpan.FromMilliseconds(10);
        vm.ShowNsfw = false;
        vm.UnratedCount.Should().Be(1);

        vm.SearchText = "zzz";
        await vm.SearchDebounceTask!;

        vm.UnratedCount.Should().Be(0);
        vm.EmptyStateMessage.Should().Be("No LoRAs match the current filters.");
    }

    /// <summary>
    /// The empty-state text is re-announced even when the grid stays empty: an empty library
    /// that loads only hidden LoRAs must stop telling the user to add source folders.
    /// </summary>
    [Fact]
    public async Task EmptyStateMessage_IsReannounced_WhenTheGridStaysEmpty()
    {
        var vm = ViewModelWith();
        vm.SearchDebounceInterval = TimeSpan.FromMilliseconds(10);
        vm.ShowNsfw = false;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.AllTiles.Add(ModelTileViewModel.FromModel(NoRating("unrated")));
        vm.SearchText = "unrated";
        await vm.SearchDebounceTask!;

        vm.FilteredTiles.Should().BeEmpty();
        raised.Should().Contain(nameof(LoraViewerViewModel.EmptyStateMessage));
        vm.EmptyStateMessage.Should().NotContain("source folders");
    }

    /// <summary>
    /// A tile stays on screen when its picture is safe, but its version chips can switch to a
    /// version whose picture is not. With Show NSFW off that picture must be covered, not shown.
    /// </summary>
    [Fact]
    public void SwitchingATileToAVersionWithAnNsfwPicture_CoversThePicture()
    {
        var model = new Model { Name = "two versions", Type = ModelType.LORA };
        ModelVersion AddVersion(string name, DateTimeOffset created, NsfwLevel level)
        {
            var v = new ModelVersion { Name = name, BaseModelRaw = "SDXL 1.0", Model = model, CreatedAt = created };
            v.Files.Add(new ModelFile { FileName = $"{name}.safetensors", IsPrimary = true, ModelVersion = v });
            var image = Image(name, level);
            image.ModelVersion = v;
            v.Images.Add(image);
            model.Versions.Add(v);
            return v;
        }
        var safe = AddVersion("safe", DateTimeOffset.UtcNow, NsfwLevel.PG);
        var explicitVersion = AddVersion("explicit", DateTimeOffset.UtcNow.AddDays(-1), NsfwLevel.XXX);
        var vm = ViewModelWith(model);

        vm.ShowNsfw = false;
        var tile = vm.FilteredTiles.Should().ContainSingle().Subject;
        tile.SelectedVersion.Should().BeSameAs(safe);
        tile.IsPreviewHidden.Should().BeFalse();

        var raised = new List<string?>();
        tile.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        tile.SelectedVersion = explicitVersion;

        tile.IsPreviewHidden.Should().BeTrue();
        raised.Should().Contain(nameof(ModelTileViewModel.IsPreviewHidden));

        vm.ShowNsfw = true;
        tile.IsPreviewHidden.Should().BeFalse("with Show NSFW on every picture is shown");
    }

    [Fact]
    public void ShowNsfwOn_ShowsEverything()
    {
        var vm = ViewModelWith(
            NewModel("explicit gallery", flaggedNsfw: false, Image("a", NsfwLevel.XXX)),
            NewModel("mature model", flaggedNsfw: true, Image("pg", NsfwLevel.PG)));

        vm.ShowNsfw = false;
        vm.ShowNsfw = true;

        Visible(vm).Should().HaveCount(2);
        vm.UnratedCount.Should().Be(0);
    }
}
