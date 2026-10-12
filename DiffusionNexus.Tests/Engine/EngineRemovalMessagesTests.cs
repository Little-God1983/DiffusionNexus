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

    // C1: a feature present only through this feature's files goes with it; the question says so.
    [Fact]
    public void FeatureRemoval_NamesTheFeaturesThatGoWithIt()
    {
        var plan = new EngineRemovalPlan([@"C:\E\custom_nodes\ComfyUI-GGUF"], [], [], [], ["Inpaint & Outpaint"]);

        EngineRemovalMessages.FeatureRemoval("Outpaint Vision", plan).Should().Contain(
            "Inpaint & Outpaint uses only files of this feature, so it is removed too.");
    }

    [Fact]
    public void Uninstall_WithoutOutsideModels_SaysTheUsersFoldersAreNotTouched()
    {
        var text = EngineRemovalMessages.Uninstall(@"C:\E", 6L * 1024 * 1024 * 1024, []);

        text.Should().StartWith("Uninstall the Diffusion Nexus Engine?");
        text.Should().Contain(@"This deletes C:\E (6.0 GB)");
        text.Should().Contain("Your own model folders are not touched.");
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
