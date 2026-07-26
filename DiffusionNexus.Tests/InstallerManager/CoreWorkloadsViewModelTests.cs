using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.InstallerManager;

/// <summary>
/// Covers the download-target dropdown of the Diffusion Nexus Core Workloads window:
/// targets come from <see cref="IModelFolderCatalog"/>, the default is preselected,
/// and the LocalAppData fallback is the only entry when nothing is configured.
/// </summary>
public sealed class CoreWorkloadsViewModelTests
{
    private readonly Mock<IModelFolderCatalog> _catalog = new();

    private CoreWorkloadsViewModel CreateSut()
    {
        // Captioning tab VM is a hard ctor dependency; pipelines/installer stay null —
        // LoadAsync skips pipeline rows but must still populate the dropdown.
        var captioning = new CaptioningModelsDialogViewModel(
            new DiffusionNexus.Inference.Captioning.CaptioningModelManager(),
            new Mock<DiffusionNexus.Domain.Services.ICaptioningService>().Object,
            downloadCoordinator: null,
            optionsPicker: (_, _) => Task.FromResult<CaptioningDownloadChoice?>(null));
        return new CoreWorkloadsViewModel(captioning, manifestProvider: null, installer: null, _catalog.Object);
    }

    [Fact]
    public async Task Load_PopulatesDropdown_AndPreselectsTheDefault()
    {
        _catalog
            .Setup(c => c.GetDownloadTargetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new ModelFolderOption(@"D:\ModelsB", IsDefault: true, Exists: true),
                new ModelFolderOption(@"D:\ModelsA", IsDefault: false, Exists: true),
            ]);

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);

        sut.DownloadTargets.Select(t => t.Path).Should().Equal(@"D:\ModelsB", @"D:\ModelsA");
        sut.SelectedDownloadTarget.Should().NotBeNull();
        sut.SelectedDownloadTarget!.Path.Should().Be(@"D:\ModelsB");
    }

    [Fact]
    public async Task Load_ShowsFallbackAsOnlyEntry_WhenCatalogReturnsFallbackOnly()
    {
        _catalog
            .Setup(c => c.GetDownloadTargetsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ModelFolderOption(ModelFolderCatalog.FallbackRoot, IsDefault: true, Exists: false)]);

        var sut = CreateSut();
        await sut.LoadCommand.ExecuteAsync(null);

        var only = sut.DownloadTargets.Should().ContainSingle().Subject;
        only.Path.Should().Be(ModelFolderCatalog.FallbackRoot);
        sut.SelectedDownloadTarget.Should().Be(only);
    }
}
