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
    public void VideoPreviewsAndCivitaiThumbCompanionsAreFound()
    {
        // Review 4.4: SidecarExtensions was a hand-copy of StaticFileTypes.GeneralExtensions
        // missing every video extension plus .civitai and .thumb, so a LoRA with a video
        // preview had its weights moved and MyLora.mp4 left behind, permanently detached —
        // and ModelDiscoveryService still considered that file part of the model.
        var model = Write("mylora.safetensors");
        var expected = new[] { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".flv", ".m4v", ".civitai", ".thumb" }
            .Select(e => Write("mylora" + e)).ToArray();

        SidecarLocator.FindSidecars(model).Should().Contain(expected);
    }

    [Fact]
    public void SidecarExtensionsCoverTheCanonicalNonModelList()
    {
        // The canonical list lives in DiffusionNexus.Service (internal), so it cannot be
        // referenced here — this asserts the copy stays a superset of its non-model entries.
        string[] canonicalNonModel =
        [
            ".thumb.jpg", ".preview.png", ".preview.webp", ".metadata.json", ".webp", ".mp4",
            ".mov", ".webm", ".avi", ".mkv", ".wmv", ".flv", ".m4v", ".png", ".preview.jpeg",
            ".preview.jpg", ".cm-info.json", ".civitai.info", ".civitai", ".thumb", ".json", ".yaml"
        ];

        SidecarLocator.SidecarExtensions.Should().Contain(canonicalNonModel);
        SidecarLocator.SidecarExtensions.Should().NotContain([".safetensors", ".ckpt", ".pt", ".pth"]);
    }

    [Fact]
    public void SidecarNameThatBreaksTheStemConventionFallsBackInsteadOfThrowing()
    {
        // The slice sidecarName[sourceStem.Length..] was unguarded: a name shorter than the
        // stem threw ArgumentOutOfRangeException out of the executor's transfer loop, past
        // its IOException/UnauthorizedAccessException filter.
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\ab.preview.png",
            modelFilePath: @"E:\src\a-much-longer-stem.safetensors",
            targetModelFilePath: @"E:\dst\Unknown\renamed_42.safetensors");

        mapped.Should().Be(@"E:\dst\Unknown\renamed_42.preview.png");
    }

    [Fact]
    public void UnknownSidecarExtensionFallsBackToThePlainExtension()
    {
        var mapped = SidecarLocator.DeriveSidecarTargetPath(
            sidecarPath: @"E:\src\somethingelse.bin",
            modelFilePath: @"E:\src\V1.safetensors",
            targetModelFilePath: @"E:\dst\V1_2.safetensors");

        mapped.Should().Be(@"E:\dst\V1_2.bin");
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
