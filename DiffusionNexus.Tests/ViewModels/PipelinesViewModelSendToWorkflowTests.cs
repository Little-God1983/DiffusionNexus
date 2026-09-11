using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services.Pipelines;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Pipelines;
using FluentAssertions;
using Moq;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Covers <see cref="PipelinesViewModel.OpenWorkflowAsync"/> for the "Send to → Workflows" flow:
/// a second send into the workflow that is already open must feed the open run instead of disposing
/// it (which would cancel an in-flight distill and drop its curated batch).
/// </summary>
public sealed class PipelinesViewModelSendToWorkflowTests
{
    private sealed class FakeRun(string id) : IPipelineRun
    {
        public string Id { get; } = id;
        public string Title => Id;
        public event EventHandler? CloseRequested { add { } remove { } }
        public ResourceMonitorViewModel? ResourceMonitor { get; set; }
        public List<IReadOnlyList<string>> Loads { get; } = [];
        public bool Disposed { get; private set; }
        public void LoadInputImages(IReadOnlyList<string> paths) => Loads.Add(paths);
        public void Dispose() => Disposed = true;
    }

    private static PipelineManifest Utility(string id) =>
        new() { Id = id, Title = id, RequiresModels = false };

    private static (PipelinesViewModel Vm, List<FakeRun> Runs) MakeVm(params string[] ids)
    {
        var provider = new Mock<IPipelineManifestProvider>();
        provider.Setup(p => p.All()).Returns(ids.Select(Utility).ToList());
        var installer = new Mock<IPipelineAssetInstaller>();
        installer.Setup(i => i.ResolveModelsRootAsync(It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);

        var runs = new List<FakeRun>();
        var vm = new PipelinesViewModel(provider.Object, installer.Object, runFactory: tile =>
        {
            var run = new FakeRun(tile.Id);
            runs.Add(run);
            return run;
        });
        return (vm, runs);
    }

    [Fact]
    public async Task OpenWorkflow_WhenSameWorkflowIsActive_FeedsOpenRunInsteadOfReopening()
    {
        var (vm, runs) = MakeVm("batch-metadata-distiller");
        var first = new[] { @"C:\g\a.png" };
        var second = new[] { @"C:\g\b.png" };

        await vm.OpenWorkflowAsync("batch-metadata-distiller", first);
        await vm.OpenWorkflowAsync("batch-metadata-distiller", second);

        runs.Should().HaveCount(1, "the open run is reused");
        runs[0].Disposed.Should().BeFalse("reusing must not cancel the in-flight run");
        runs[0].Loads.Should().Equal([first, second]);
        vm.ActiveRun.Should().BeSameAs(runs[0]);
    }

    [Fact]
    public async Task OpenWorkflow_WhenDifferentWorkflowIsActive_ClosesItAndOpensTheNewOne()
    {
        var (vm, runs) = MakeVm("batch-metadata-distiller", "image-to-image");

        await vm.OpenWorkflowAsync("batch-metadata-distiller", [@"C:\g\a.png"]);
        await vm.OpenWorkflowAsync("image-to-image", [@"C:\g\b.png"]);

        runs.Should().HaveCount(2);
        runs[0].Disposed.Should().BeTrue();
        vm.ActiveRun.Should().BeSameAs(runs[1]);
    }

    [Fact]
    public async Task OpenWorkflow_AfterRunClosed_OpensAFreshRun()
    {
        var (vm, runs) = MakeVm("batch-metadata-distiller");

        await vm.OpenWorkflowAsync("batch-metadata-distiller", [@"C:\g\a.png"]);
        vm.CloseRunCommand.Execute(null);
        await vm.OpenWorkflowAsync("batch-metadata-distiller", [@"C:\g\b.png"]);

        runs.Should().HaveCount(2, "a closed run's id must not be treated as still active");
        vm.ActiveRun.Should().BeSameAs(runs[1]);
    }
}
