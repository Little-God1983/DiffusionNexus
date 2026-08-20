using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SidecarLocatorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sidecar-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string Write(string name)
    {
        var path = Path.Combine(_root.FullName, name);
        File.WriteAllText(path, "x");
        return path;
    }

    [Fact]
    public void FindsExistingSidecarsAndSkipsMissingOnes()
    {
        var model = Write("mylora.safetensors");
        var info = Write("mylora.civitai.info");
        var preview = Write("mylora.preview.png");
        Write("otherlora.civitai.info"); // different stem — must not match

        var sidecars = SidecarLocator.FindSidecars(model);

        sidecars.Should().BeEquivalentTo(new[] { info, preview });
    }

    [Fact]
    public void ModelFileItselfIsNeverReturnedEvenWhenItsExtensionIsASidecarExtension()
    {
        // ".json" IS in SidecarExtensions: without the self-exclusion guard,
        // FindSidecars("mylora.json") would return the input file itself.
        var model = Write("mylora.json");
        var info = Write("mylora.civitai.info");

        var sidecars = SidecarLocator.FindSidecars(model);

        sidecars.Should().BeEquivalentTo(new[] { info });
    }

    [Fact]
    public void ModelWithNoSidecarsYieldsEmpty()
    {
        var model = Write("mylora.safetensors");
        SidecarLocator.FindSidecars(model).Should().BeEmpty();
    }

    [Fact]
    public void SidecarTargetFollowsRenamedModelStem()
    {
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\V1.civitai.info",
            modelFilePath: @"E:\src\V1.safetensors",
            targetModelFilePath: @"E:\dst\SDXL 1.0\Character\V1_3204603.safetensors");

        mapped.Should().Be(@"E:\dst\SDXL 1.0\Character\V1_3204603.civitai.info");
    }

    [Fact]
    public void MultiDotSidecarExtensionIsPreserved()
    {
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\V1.preview.png",
            modelFilePath: @"E:\src\V1.safetensors",
            targetModelFilePath: @"E:\dst\Unknown\V1_2.safetensors");

        mapped.Should().Be(@"E:\dst\Unknown\V1_2.preview.png");
    }
}
