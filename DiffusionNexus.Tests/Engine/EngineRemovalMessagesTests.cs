using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public class EngineRemovalMessagesTests
{
    [Theory]
    [InlineData(0, false, RemovalChoice.Cancel)]
    [InlineData(1, false, RemovalChoice.EngineOnly)]
    [InlineData(-1, false, RemovalChoice.Cancel)]
    [InlineData(0, true, RemovalChoice.Cancel)]
    [InlineData(1, true, RemovalChoice.Everything)]
    [InlineData(2, true, RemovalChoice.EngineOnly)]
    [InlineData(-1, true, RemovalChoice.Cancel)]
    public void ToChoice_MapsTheButtonIndex(int index, bool hasOutside, RemovalChoice expected) =>
        EngineRemovalMessages.ToChoice(index, hasOutside).Should().Be(expected);

    [Fact]
    public void FeatureOptions_WithOutsideModels_PutTheSafeChoiceLast()
    {
        EngineRemovalMessages.FeatureOptions(hasOutside: true).Should().Equal(
            "Cancel", "Delete all models of this feature, wherever they are", "Delete only what's in the Diffusion Nexus Engine");
        EngineRemovalMessages.FeatureOptions(hasOutside: false).Should().Equal("Cancel", "Remove");
    }

    [Fact]
    public void UninstallOptions_WithOutsideModels_PutTheSafeChoiceLast()
    {
        EngineRemovalMessages.UninstallOptions(hasOutside: true).Should().Equal(
            "Cancel", "Delete these models too", "Delete only what's in the Diffusion Nexus Engine");
        EngineRemovalMessages.UninstallOptions(hasOutside: false).Should().Equal("Cancel", "Uninstall");
    }

    [Fact]
    public void FeatureRemoval_NamesCountsSizeKeptItemsAndOutsideFiles()
    {
        var plan = new EngineRemovalPlan(
            [@"C:\E\custom_nodes\ComfyUI-GGUF"],
            [new PlannedFile(@"C:\E\models\unet\a.gguf", 2L * 1024 * 1024 * 1024)],
            [new PlannedFile(@"D:\Models\vae\ae.safetensors", 300L * 1024 * 1024)],
            [new KeptItem("qwen_image_vae.safetensors", @"C:\E\models\vae\qwen_image_vae.safetensors", "Outpaint Vision")],
            []);

        var text = EngineRemovalMessages.FeatureRemoval("Inpaint & Outpaint", plan);

        text.Should().StartWith("Remove Inpaint & Outpaint?");
        text.Should().Contain("1 custom node pack and 1 model (2.0 GB) from the Diffusion Nexus Engine");
        text.Should().Contain("qwen_image_vae.safetensors (used by Outpaint Vision)");
        text.Should().Contain("Other programs (ComfyUI, A1111, …) may use them too");
        text.Should().Contain(@"D:\Models\vae\ae.safetensors (300 MB)");
        text.Should().Contain("Python packages");
    }

    // Minor: nothing of the feature in the Engine (only the user's own copies): no "0 packs and 0 models" sentence.
    [Fact]
    public void FeatureRemoval_NothingInTheEngine_SaysSoInsteadOfCountingZeros()
    {
        var plan = new EngineRemovalPlan([], [], [new PlannedFile(@"D:\Models\vae\ae.safetensors", 300L * 1024 * 1024)], [], []);

        var text = EngineRemovalMessages.FeatureRemoval("Batch Upscale", plan);

        text.Should().NotContain("0 custom node packs");
        text.Should().Contain("Nothing of it is in the Diffusion Nexus Engine's own folder.");
        text.Should().Contain(@"D:\Models\vae\ae.safetensors (300 MB)");
    }

    // C1: a feature present only through this feature's files goes with it; the question says so.
    [Fact]
    public void FeatureRemoval_NamesTheFeaturesThatGoWithIt()
    {
        var plan = new EngineRemovalPlan([@"C:\E\custom_nodes\ComfyUI-GGUF"], [], [], [], ["Inpaint & Outpaint"]);

        EngineRemovalMessages.FeatureRemoval("Outpaint Vision", plan).Should().Contain(
            "Inpaint & Outpaint uses only files of this feature, so it is removed too.");
    }

    // A feature that loses this feature's files but keeps some another feature needs is not "removed too".
    [Fact]
    public void FeatureRemoval_NamesTheFeaturesLeftPartlyInstalled()
    {
        var plan = new EngineRemovalPlan([@"C:\E\custom_nodes\ComfyUI-GGUF"], [], [], [], []) { LeftPartial = ["Inpaint & Outpaint"] };

        var text = EngineRemovalMessages.FeatureRemoval("Outpaint Vision", plan);

        text.Should().Contain("Inpaint & Outpaint loses its files except those another feature keeps; it will show as Partial.");
        text.Should().NotContain("removed too");
    }

    [Fact]
    public void Uninstall_WithoutOutsideModels_SaysTheUsersFoldersAreNotTouched()
    {
        var text = EngineRemovalMessages.Uninstall(@"C:\E", 6L * 1024 * 1024 * 1024, []);

        text.Should().StartWith("Uninstall the Diffusion Nexus Engine?");
        text.Should().Contain(@"This deletes C:\E (6.0 GB)");
        text.Should().Contain("Your own model folders are not touched.");
    }

    // C2: model files no feature declares (a Core download that landed in the Engine) are named as going too.
    [Fact]
    public void Uninstall_WithModelsNoFeatureUses_SaysTheyAreDeletedWithTheEngine()
    {
        var text = EngineRemovalMessages.Uninstall(@"C:\E", 6L * 1024 * 1024 * 1024, [], new ModelFileTotal(3, 2L * 1024 * 1024 * 1024));

        text.Should().Contain("It also holds 3 other model files (2.0 GB) that no Engine feature uses, " +
                              "for example ones another part of the app downloaded there. They are deleted with the Engine.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Uninstall_WithoutModelsNoFeatureUses_SaysNothingAboutThem(bool unknown)
    {
        var text = EngineRemovalMessages.Uninstall(@"C:\E", 1, [], unknown ? null : new ModelFileTotal(0, 0));

        text.Should().NotContain("no Engine feature uses");
    }

    // Minor: an Engine folder that is itself a link loses only the link; the result names where the files stay.
    [Theory]
    [InlineData(@"D:\Real\Engine", @"D:\Real\Engine")]
    [InlineData(null, "the folder it pointed to")]
    public void UninstalledLinkOnly_SaysTheFilesBehindTheLinkStay(string? target, string expected)
    {
        var text = EngineRemovalMessages.UninstalledLinkOnly(@"C:\E", target);

        text.Should().Contain(@"C:\E");
        text.Should().Contain("only the link was removed");
        text.Should().Contain($"the files behind it stay in {expected}");
    }

    [Fact]
    public void UninstallUnsafeFolder_SaysNothingOnDiskIsTouched()
    {
        var text = EngineRemovalMessages.UninstallUnsafeFolder(@"D:\");

        text.Should().StartWith("Uninstall the Diffusion Nexus Engine?");
        text.Should().Contain("\"D:\\\" is not a folder the app can safely delete");
        text.Should().Contain("nothing on disk is touched");
    }
}
