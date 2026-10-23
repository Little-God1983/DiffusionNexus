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

    [Fact]
    public void NsfwFlaggedModel_IsHiddenEvenWithSafeImages()
    {
        var vm = ViewModelWith(NewModel("mature model", flaggedNsfw: true, Image("pg", NsfwLevel.PG)));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEmpty();
    }

    [Fact]
    public void UnratedCivitaiImages_AreHiddenUntilRated_LocalPreviewsAndNoImagesStay()
    {
        var local = new ModelImage { Url = "file:///E:/Loras/local.preview.png", MediaType = "image" };
        var vm = ViewModelWith(
            NewModel("unrated", flaggedNsfw: false, Image("u", NsfwLevel.None)),
            NewModel("local preview", flaggedNsfw: false, local),
            NewModel("no images", flaggedNsfw: false));

        vm.ShowNsfw = false;

        Visible(vm).Should().BeEquivalentTo("local preview", "no images");
        vm.HiddenUnratedCount.Should().Be(1, "the user is told how many wait for Download Metadata");
    }

    /// <summary>
    /// Until the first Download Metadata run after the upgrade, nearly every Civitai picture is
    /// unrated and "Show NSFW" off empties the grid. The tab must say why and what to do, not
    /// "add LoRA source folders" as if the library were gone.
    /// </summary>
    [Fact]
    public void HiddenUnratedTiles_AreExplained_WithTheWayToRateThem()
    {
        var vm = ViewModelWith(
            NewModel("unrated one", flaggedNsfw: false, Image("u1", NsfwLevel.None)),
            NewModel("unrated two", flaggedNsfw: false, Image("u2", NsfwLevel.None)));

        vm.ShowNsfw = false;

        vm.HasHiddenUnrated.Should().BeTrue();
        vm.HiddenUnratedMessage.Should().Contain("2 LoRAs").And.Contain("Download Metadata");
        vm.EmptyStateMessage.Should().Contain("Download Metadata").And.NotContain("source folders");
    }

    [Fact]
    public void EmptyState_SaysNoMatch_WhenFiltersHideARatedLibrary()
    {
        var vm = ViewModelWith(NewModel("explicit gallery", flaggedNsfw: false, Image("a", NsfwLevel.XXX)));

        vm.ShowNsfw = false;

        vm.HasHiddenUnrated.Should().BeFalse();
        vm.EmptyStateMessage.Should().Be("No LoRAs match the current filters.");
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
    /// search that matches nothing is "no match", not "previews have no rating", even while
    /// unrated LoRAs elsewhere in the library are hidden.
    /// </summary>
    [Fact]
    public async Task HiddenUnratedCount_OnlyCountsWhatTheOtherFiltersLetThrough()
    {
        var vm = ViewModelWith(
            NewModel("alpha", flaggedNsfw: false, Image("u", NsfwLevel.None)),
            NewModel("beta", flaggedNsfw: false, Image("pg", NsfwLevel.PG)));
        vm.SearchDebounceInterval = TimeSpan.FromMilliseconds(10);
        vm.ShowNsfw = false;
        vm.HiddenUnratedCount.Should().Be(1);

        vm.SearchText = "zzz";
        await vm.SearchDebounceTask!;

        vm.HiddenUnratedCount.Should().Be(0);
        vm.EmptyStateMessage.Should().Be("No LoRAs match the current filters.");
    }

    /// <summary>
    /// The empty-state text is re-announced even when the grid stays empty: an empty library
    /// that loads only unrated LoRAs must stop telling the user to add source folders.
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
        vm.HiddenUnratedCount.Should().Be(0);
    }
}
