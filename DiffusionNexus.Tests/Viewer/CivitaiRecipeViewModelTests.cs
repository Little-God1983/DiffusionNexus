using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// #628: the recipe view — which resources are ticked, what each row says, the SFW/NSFW rating
/// from Civitai, and what "Add N to queue" hands to the queue.
/// </summary>
public sealed class CivitaiRecipeViewModelTests
{
    private static CivitaiModel Model(int modelId, int versionId, string name, CivitaiModelType type = CivitaiModelType.LORA, double sizeKb = 1024) => new()
    {
        Id = modelId,
        Name = name,
        Type = type,
        ModelVersions =
        [
            new CivitaiModelVersion
            {
                Id = versionId, ModelId = modelId, Name = "v1.0", BaseModel = "Krea 2",
                Files = [new CivitaiModelFile { Name = $"{name}.safetensors", Primary = true, SizeKB = sizeKb }],
            },
        ],
    };

    private static CivitaiRecipeResource Resource(CivitaiModel model, double? weight = null) => new(
        model.ModelVersions[0].Id, model.ModelVersions[0], model,
        weight is null ? null : new RecipeLoraUse(model.Name, null, weight, null), null);

    private static CivitaiImageRecipe Recipe(IReadOnlyList<CivitaiRecipeResource> resources, int? rating = 1,
        ImageRecipeData? data = null, IReadOnlyList<RecipeLoraUse>? unlinked = null) => new(
        143992601,
        new CivitaiModelImage { Id = 143992601, Width = 1256, Height = 1672, Username = "purplelady", BaseModel = "Krea 2 Turbo" },
        rating, null,
        data ?? new ImageRecipeData { Source = RecipeDataSource.SettingsText, SourceNote = "Read from the original image file (settings text)", Steps = 8, Cfg = 1, Seed = 832350830334358, Sampler = "exponential/ddim_beta57" },
        resources, unlinked ?? []);

    private static CivitaiRecipeViewModel Vm(CivitaiImageRecipe recipe, CivitaiInstalledIndex? installed = null, bool showNsfw = true) =>
        new(recipe, installed ?? CivitaiInstalledIndex.Empty, showNsfw, m => new CivitaiResultViewModel(m, true));

    private static CivitaiImageRecipe ExampleShape() => Recipe(
    [
        Resource(Model(1, 3064297, "Krea 2 Turbo", CivitaiModelType.Checkpoint, 12833721)),
        Resource(Model(2, 3072964, "Ass and Thighs"), 3),
        Resource(Model(3, 3110766, "Purple Grainy"), 0.5),
        Resource(Model(4, 3113770, "Purple Graphics"), 1),
        Resource(Model(5, 3343424, "AG Aim"), 0.65),
    ]);

    [Fact]
    public void ExampleShape_TicksMissingLoras_NotCheckpointNorInstalled()
    {
        var vm = Vm(ExampleShape(), new CivitaiInstalledIndex([3343424], []));

        vm.Rows.Where(r => r.IsSelected).Select(r => r.Name).Should().Equal("Ass and Thighs", "Purple Grainy", "Purple Graphics");
        var checkpoint = vm.Rows.Single(r => r.TypeBadge == "CHECKPOINT");
        checkpoint.CanTick.Should().BeFalse();
        checkpoint.WeightDisplay.Should().Be("—");
        vm.Rows.Single(r => r.Name == "AG Aim").IsInstalled.Should().BeTrue();
        vm.Rows.Skip(1).Select(r => r.WeightDisplay).Should().Equal("3", "0.5", "1", "0.65");
        vm.Rows[1].VersionLabel.Should().Be("v1.0 · LoRA");
        vm.AddToQueueText.Should().Be("Add 3 to queue");
        vm.CanSendToComfyUi.Should().BeFalse();
        vm.SendToComfyUiTip.Should().Be("Coming in a later version");
        vm.Title.Should().Be("Image 143992601 by purplelady");
        vm.Details.Should().Be("1256 × 1672 · Krea 2 Turbo");
        vm.StepsCfgSeedLine.Should().Be("8 · 1 · 832350830334358");
        vm.SamplerLine.Should().Be("exponential/ddim_beta57");
    }

    [Fact]
    public void LoraWithoutUse_ShowsWeightUnknown()
    {
        var vm = Vm(Recipe([Resource(Model(2, 20, "Foo"))]));

        vm.Rows.Single().WeightDisplay.Should().Be("weight unknown");
    }

    [Fact]
    public void UnlinkedLora_IsAnUntickableRowWithNote()
    {
        var vm = Vm(Recipe([], unlinked: [new RecipeLoraUse("private_lora", @"x\private_lora.safetensors", 0.8, null)]));

        var row = vm.Rows.Single();
        row.Name.Should().Be("private_lora");
        row.Note.Should().Be("not linked on Civitai");
        row.WeightDisplay.Should().Be("0.8");
        row.CanTick.Should().BeFalse();
        row.IsSelected.Should().BeFalse();
    }

    [Fact]
    public void FailedResource_IsAnUntickableRowWithItsError()
    {
        var vm = Vm(Recipe([new CivitaiRecipeResource(100, null, null, null, "429 Too Many Requests")]));

        var row = vm.Rows.Single();
        row.Name.Should().Be("Version 100");
        row.Note.Should().Be("429 Too Many Requests");
        row.CanTick.Should().BeFalse();
    }

    [Fact]
    public void NsfwRecipe_HidesPreviewUntilShowNsfw()
    {
        var vm = Vm(Recipe([], rating: 8), showNsfw: false);

        vm.IsSafe.Should().BeFalse();
        vm.RatingBadge.Should().Be("NSFW · X");
        vm.IsPreviewHidden.Should().BeTrue();

        vm.ApplyNsfwPreference(true);

        vm.IsPreviewHidden.Should().BeFalse();
    }

    [Fact]
    public void SfwRecipe_BadgeAndPreviewShown()
    {
        var vm = Vm(Recipe([], rating: 1), showNsfw: false);

        vm.IsSafe.Should().BeTrue();
        vm.RatingBadge.Should().Be("SFW · PG");
        vm.IsPreviewHidden.Should().BeFalse();
    }

    [Fact]
    public void UnratedRecipe_CountsAsNsfw()
    {
        Vm(Recipe([], rating: null)).RatingBadge.Should().Be("NSFW · unrated");
    }

    [Fact]
    public void NoData_ShowsWarningAndUnknownLines()
    {
        var vm = Vm(Recipe([Resource(Model(2, 20, "Foo"))], data: ImageRecipeData.Empty(RecipeFileFormat.Png)));

        vm.HasGenerationData.Should().BeFalse();
        vm.NoDataWarning.Should().Be("This image's file carries no generation data, and Civitai does not publish it either. " +
            "The prompt, settings and LoRA weights are unknown. The list of resources below still comes from Civitai, so the LoRAs can be downloaded.");
        vm.StepsCfgSeedLine.Should().Be("unknown · unknown · unknown");
        vm.SamplerLine.Should().Be("unknown");
    }

    [Fact]
    public void NoResources_SaysCivitaiListsNone_AndTheWarningDoesNotPromiseAList()
    {
        // Image 146878246 (2026-10-28): the public API gives modelVersionIds = [] although the web page
        // lists six resources — Civitai serves that list only to its own site (image.getResources is 401).
        var vm = Vm(Recipe([], data: ImageRecipeData.Empty(RecipeFileFormat.Png)));

        vm.HasResources.Should().BeFalse();
        vm.NoResourcesText.Should().Be("Civitai's public API lists no resources for this image. The image page on Civitai may still " +
            "show some: Civitai gives that list only to its own website. Open the image on Civitai to see it.");
        vm.NoDataWarning.Should().Be("This image's file carries no generation data, and Civitai does not publish it either. " +
            "The prompt, settings and LoRA weights are unknown.");
    }

    [Fact]
    public void NoData_NamesTheProgramThatLastSavedTheFile()
    {
        var vm = Vm(Recipe([Resource(Model(2, 20, "Foo"))],
            data: ImageRecipeData.Empty(RecipeFileFormat.Png) with { SavedBy = "Topaz Gigapixel 1.3.0 (Macintosh)" }));

        vm.NoDataWarning.Should().Contain("The file was last saved by Topaz Gigapixel 1.3.0 (Macintosh), which may have removed it.");
    }

    [Fact]
    public void AddToQueue_PassesSelectedPairs()
    {
        var vm = Vm(ExampleShape());
        List<(CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick)>? sent = null;
        vm.EnqueueHandler = pairs => sent = pairs;
        vm.Rows.Single(r => r.Name == "Purple Grainy").IsSelected = false;

        vm.AddToQueueCommand.Execute(null);

        sent!.Select(p => p.Pick.Version.Id).Should().Equal(3072964, 3113770, 3343424);
        sent!.Select(p => p.Result.Model!.Id).Should().Equal(2, 4, 5);
    }

    [Fact]
    public void InstalledSnapshotArrivingLater_UnticksUntouchedRows_KeepsUserTicks()
    {
        var untouched = Vm(ExampleShape());
        untouched.ApplyInstalledIndex(new CivitaiInstalledIndex([3072964], []));
        untouched.Rows.Single(r => r.Name == "Ass and Thighs").IsSelected.Should().BeFalse();
        untouched.Rows.Single(r => r.Name == "Ass and Thighs").IsInstalled.Should().BeTrue();

        var touched = Vm(ExampleShape());
        touched.Rows.Single(r => r.Name == "Purple Grainy").IsSelected = false;
        touched.ApplyInstalledIndex(new CivitaiInstalledIndex([3072964], []));
        touched.Rows.Single(r => r.Name == "Ass and Thighs").IsSelected.Should().BeTrue("the user changed the ticks, so they stay");
        touched.Rows.Single(r => r.Name == "Ass and Thighs").IsInstalled.Should().BeTrue();
    }

    [Fact]
    public void LaterSnapshot_ReachesTheCardPicks()
    {
        // The preflight's "already installed" warning reads Pick.IsInstalled; a snapshot that lands after
        // the recipe was built must reach it, or a ticked installed LoRA is queued without a word.
        var vm = Vm(ExampleShape());
        vm.Rows.Single(r => r.Name == "Purple Grainy").IsSelected = false; // the user touched the ticks

        vm.ApplyInstalledIndex(new CivitaiInstalledIndex([3072964], []));

        var row = vm.Rows.Single(r => r.Name == "Ass and Thighs");
        row.IsSelected.Should().BeTrue();
        row.Pick!.IsInstalled.Should().BeTrue();
    }

    [Fact]
    public void LoraUsedTwiceWithTwoWeights_ShowsBoth()
    {
        var model = Model(2, 20, "Lightx2v");
        var use = new RecipeLoraUse("Lightx2v", null, 1, null, null, [1, 0.8]);
        var vm = Vm(Recipe([new CivitaiRecipeResource(20, model.ModelVersions[0], model, use, null)]));

        vm.Rows.Single().WeightDisplay.Should().Be("1 / 0.8");
    }

    [Fact]
    public void PromptMissingFromATrace_IsUnknown_NotNoneGiven()
    {
        var graphOnly = Vm(Recipe([], data: new ImageRecipeData { Source = RecipeDataSource.ComfyUiWorkflow, SourceNote = "x" }));
        graphOnly.PromptText.Should().Be("unknown");
        graphOnly.NegativePromptText.Should().Be("unknown");

        var text = Vm(Recipe([], data: new ImageRecipeData { Source = RecipeDataSource.SettingsText, SourceNote = "x", Prompt = "a cat" }));
        text.NegativePromptText.Should().Be("none given", "a parameters text without a Negative prompt line has none");
    }

    [Fact]
    public void FooterCountsAndSize()
    {
        var vm = Vm(Recipe([Resource(Model(2, 20, "A", sizeKb: 1024)), Resource(Model(3, 30, "B", sizeKb: 2048))]));

        vm.SelectedCount.Should().Be(2);
        vm.FooterText.Should().Be($"2 selected · {CivitaiVersionPickItemViewModel.FormatSize(3L << 20)}"); // same culture as the cards

        vm.Rows[0].IsSelected = false;

        vm.FooterText.Should().Be($"1 selected · {CivitaiVersionPickItemViewModel.FormatSize(2L << 20)}");
        vm.AddToQueueText.Should().Be("Add 1 to queue");
    }
}
