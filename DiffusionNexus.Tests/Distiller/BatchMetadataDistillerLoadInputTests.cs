using System.IO;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services.Lora;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.ViewModels.Pipelines;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.Distiller;

/// <summary>
/// <see cref="BatchMetadataDistillerViewModel.LoadInputImages"/> is the "Send to → Workflows" entry
/// point. The distiller writes PNG only, so non-PNG hand-offs are skipped up front and reported
/// instead of failing one by one after the user clicks Distill.
/// </summary>
public sealed class BatchMetadataDistillerLoadInputTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"dn_distill_{Guid.NewGuid():N}");

    public BatchMetadataDistillerLoadInputTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, [0x00]);
        return path;
    }

    private static BatchMetadataDistillerViewModel MakeVm() =>
        new(new PipelineManifest { Id = "batch-metadata-distiller" },
            Mock.Of<ILoraCatalog>(),
            Mock.Of<IPipelineAssetInstaller>());

    [Fact]
    public void LoadInputImages_KeepsOnlyPngAndReportsSkipped()
    {
        var png = Touch("a.png");
        var pngUpper = Touch("b.PNG");
        var jpg = Touch("c.jpg");
        var webp = Touch("d.webp");
        var vm = MakeVm();

        vm.LoadInputImages([png, jpg, pngUpper, webp]);

        vm.ImagePaths.Should().Equal(png, pngUpper);
        vm.StatusText.Should().Contain("2").And.ContainEquivalentOf("skipped");
    }

    [Fact]
    public void LoadInputImages_AllPng_LeavesStatusUntouched()
    {
        var png = Touch("a.png");
        var vm = MakeVm();

        vm.LoadInputImages([png]);

        vm.ImagePaths.Should().Equal(png);
        vm.StatusText.Should().BeEmpty();
    }
}
