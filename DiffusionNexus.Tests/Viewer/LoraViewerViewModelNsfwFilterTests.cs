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
            NewModel("unrated", flaggedNsfw: false, Image("u", NsfwLevel.None)),
            NewModel("local preview", flaggedNsfw: false, local),
            NewModel("no images", flaggedNsfw: false));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEquivalentTo("local preview", "no images");
        vm.HiddenCount.Should().Be(1);
    }

    /// <summary>
    /// #647: the hint's number is exactly what "Show hidden" lists — whatever the reason a LoRA is
    /// hidden — and the hint promises nothing about Download Metadata.
    /// </summary>
    [Fact]
    public void TheHintCount_IsExactlyWhatShowHiddenLists()
    {
        var vm = ViewModelWith(
            NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)),
            NewModel("creator flag", flaggedNsfw: true, Image("pg", NsfwLevel.PG)),
            NewModel("unrated", flaggedNsfw: false, Image("u", NsfwLevel.None)),
            NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG13)));

        vm.ShowNsfw = false;
        vm.HiddenCount.Should().Be(3);
        vm.HasHiddenInView.Should().BeTrue();
        vm.HiddenHint.Should().Be("3 LoRAs in this view are hidden by Show NSFW.");

        vm.ShowHiddenCommand.Execute(null);

        vm.ShowHiddenOnly.Should().BeTrue();
        Visible(vm).Should().BeEquivalentTo("rated xxx", "creator flag", "unrated");
        vm.FilteredTiles.Should().OnlyContain(t => t.IsInHiddenView && t.IsPreviewHidden,
            "every picture stays covered in the hidden list");
        vm.FilteredTiles.Select(t => t.HiddenReasonText).Should().BeEquivalentTo(
            "Rated XXX on Civitai", "Flagged NSFW by its creator", "No rating from Civitai");

        vm.BackToAllCommand.Execute(null);
        Visible(vm).Should().BeEquivalentTo("safe");
        vm.FilteredTiles.Should().OnlyContain(t => !t.IsInHiddenView);
    }

    /// <summary>
    /// #647: the user's choice per LoRA. Safe to show brings it back (with its usual picture),
    /// NSFW hides it with that reason, Automatic returns to Civitai's verdict.
    /// </summary>
    [Fact]
    public async Task MarkingALora_SafeNsfwOrAutomatic_ChangesWhatIsHidden()
    {
        var vm = ViewModelWith(
            NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)),
            NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG)));
        vm.ShowNsfw = false;
        var xxx = vm.AllTiles.Single(t => t.DisplayName == "rated xxx");
        var safe = vm.AllTiles.Single(t => t.DisplayName == "safe");

        await vm.SetNsfwOverrideAsync(xxx, false);
        Visible(vm).Should().BeEquivalentTo("rated xxx", "safe");
        xxx.IsPreviewHidden.Should().BeFalse("the user decided it is safe to show");

        await vm.SetNsfwOverrideAsync(safe, true);
        Visible(vm).Should().BeEquivalentTo("rated xxx");
        vm.ShowHiddenCommand.Execute(null);
        vm.FilteredTiles.Should().ContainSingle().Which.HiddenReasonText.Should().Be("Marked NSFW by you");

        await vm.SetNsfwOverrideAsync(safe, null);
        await vm.SetNsfwOverrideAsync(xxx, null);
        vm.FilteredTiles.Should().ContainSingle().Which.DisplayName.Should().Be("rated xxx");
    }

    /// <summary>
    /// The hint counts models, like the toolbar's "N of M models": a tile that groups two model
    /// rows of the same LoRA counts twice, so shown + hidden adds up to what the view holds.
    /// </summary>
    [Fact]
    public void TheHintCounts_Models_LikeTheToolbar()
    {
        var vm = new LoraViewerViewModel();
        vm.AllTiles.Clear();
        vm.AllTiles.Add(ModelTileViewModel.FromModelGroup(
        [
            NewModel("same lora", flaggedNsfw: false, Image("x1", NsfwLevel.XXX)),
            NewModel("same lora", flaggedNsfw: false, Image("x2", NsfwLevel.XXX)),
        ]));
        vm.AllTiles.Add(ModelTileViewModel.FromModel(NewModel("safe", flaggedNsfw: false, Image("s", NsfwLevel.PG))));

        vm.ShowNsfw = false;

        vm.FilteredModelCount.Should().Be(1);
        vm.HiddenCount.Should().Be(2);
        vm.ShowHiddenButtonText.Should().Be("Show hidden (2)");

        vm.ShowHiddenCommand.Execute(null);
        vm.FilteredModelCount.Should().Be(2, "the list shows exactly what the hint counted");
    }

    /// <summary>The tile's buttons and menu ask the Installed tab to record the choice.</summary>
    [Fact]
    public void TheTilesMarkCommands_RequestTheChoice()
    {
        var tile = ModelTileViewModel.FromModel(NewModel("t", flaggedNsfw: false, Image("x", NsfwLevel.XXX)));
        var requested = new List<bool?>();
        tile.NsfwOverrideRequested += (_, choice) => requested.Add(choice);

        tile.MarkSafeToShowCommand.Execute(null);
        tile.MarkNsfwCommand.Execute(null);
        tile.MarkNsfwAutomaticCommand.Execute(null);

        requested.Should().Equal(false, true, null);
    }

    [Fact]
    public void TurningShowNsfwOn_LeavesTheHiddenList()
    {
        var vm = ViewModelWith(NewModel("rated xxx", flaggedNsfw: false, Image("x", NsfwLevel.XXX)));
        vm.ShowNsfw = false;
        vm.ShowHiddenCommand.Execute(null);

        vm.ShowNsfw = true;

        vm.ShowHiddenOnly.Should().BeFalse();
        vm.HiddenCount.Should().Be(0);
        Visible(vm).Should().BeEquivalentTo("rated xxx");
    }

    /// <summary>
    /// When everything in view is hidden the tab says so — not "add LoRA source folders" as if the
    /// library were gone.
    /// </summary>
    [Fact]
    public void EmptyState_PointsToShowHidden_WhenEverythingInViewIsHidden()
    {
        var vm = ViewModelWith(
            NewModel("one", flaggedNsfw: false, Image("u1", NsfwLevel.None)),
            NewModel("two", flaggedNsfw: false, Image("x2", NsfwLevel.X)));

        vm.ShowNsfw = false;

        vm.EmptyStateMessage.Should().Be("All 2 LoRAs in this view are hidden by Show NSFW. Click Show hidden to see them, or turn Show NSFW on.");
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
    public async Task HiddenCount_OnlyCountsWhatTheOtherFiltersLetThrough()
    {
        var vm = ViewModelWith(
            NewModel("alpha", flaggedNsfw: false, Image("u", NsfwLevel.None)),
            NewModel("beta", flaggedNsfw: false, Image("pg", NsfwLevel.PG)));
        vm.SearchDebounceInterval = TimeSpan.FromMilliseconds(10);
        vm.ShowNsfw = false;
        vm.HiddenCount.Should().Be(1);

        vm.SearchText = "zzz";
        await vm.SearchDebounceTask!;

        vm.HiddenCount.Should().Be(0);
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

        vm.AllTiles.Add(ModelTileViewModel.FromModel(NewModel("unrated", flaggedNsfw: false, Image("u", NsfwLevel.None))));
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
        vm.HiddenCount.Should().Be(0);
    }
}
