using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Proves the <see cref="IUiScheduler"/> seam on <see cref="TrainingRunCardViewModel"/>:
/// <c>LoadBaseModelCatalogAsync</c> marshals the population of
/// <c>AvailableBaseModels</c> onto the UI thread via <c>InvokeAsync</c>. With
/// <see cref="ImmediateUiScheduler"/> that invoke runs inline, so the catalog is
/// loaded synchronously — a real Dispatcher would never pump it in a headless test.
/// </summary>
public class TrainingRunCardViewModelSchedulerTests
{
    private static TrainingRunCardViewModel CreateVm(
        ICivitaiBaseModelCatalog catalog,
        IUiScheduler scheduler,
        string? baseModel = null)
        => new(
            new TrainingRunInfo { Name = "Run", BaseModel = baseModel },
            runFolderPath: Path.Combine(Path.GetTempPath(), "dn-training-run-" + Guid.NewGuid().ToString("N")),
            eventAggregator: new DatasetEventAggregator(),
            baseModelCatalog: catalog,
            uiScheduler: scheduler);

    [Fact]
    public void WhenConstructedThenTheCatalogPopulatesAvailableBaseModelsSynchronouslyThroughTheScheduler()
    {
        var labels = new List<string> { "SDXL 1.0", "Pony" };
        var catalog = new Mock<ICivitaiBaseModelCatalog>();
        catalog
            .Setup(c => c.GetBaseModelsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(labels);

        var scheduler = new ImmediateUiScheduler();

        // The constructor fires LoadBaseModelCatalogAsync (fire-and-forget). Because
        // the InvokeAsync-marshalled population runs inline through the immediate
        // scheduler, it has completed synchronously by the time construction returns.
        var vm = CreateVm(catalog.Object, scheduler);

        scheduler.InvokeCount.Should().Be(1);
        vm.AvailableBaseModels.Should().BeEquivalentTo(labels);
    }

    [Fact]
    public async Task WhenReloadedThenAPersistedBaseModelStaysSelectableAtopTheCatalog()
    {
        var labels = new List<string> { "SDXL 1.0", "Pony" };
        var catalog = new Mock<ICivitaiBaseModelCatalog>();
        catalog
            .Setup(c => c.GetBaseModelsAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(labels);

        var scheduler = new ImmediateUiScheduler();
        // A legacy base model the catalog does not list.
        var vm = CreateVm(catalog.Object, scheduler, baseModel: "MyCustomBase");

        await vm.LoadBaseModelCatalogAsync();

        // The invoke-marshalled rebuild kept the persisted value selectable by
        // surfacing it at the top, then appended the catalog labels.
        vm.AvailableBaseModels.Should().Equal("MyCustomBase", "SDXL 1.0", "Pony");
    }
}
