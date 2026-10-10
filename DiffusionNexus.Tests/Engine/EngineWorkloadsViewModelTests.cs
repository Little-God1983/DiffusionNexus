using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineWorkloadsViewModelTests
{
    private static InstallationConfiguration Config(Guid id, string name)
    {
        var config = new InstallationConfiguration { Id = id, Name = name };
        config.Repository.Type = RepositoryType.ComfyUI;
        config.Vram.VramProfiles = "8,12,16,24,32";
        return config;
    }

    [Fact]
    public async Task WithAllowList_OnlyTheAllowedWorkloadsAreListed()
    {
        var configs = new List<InstallationConfiguration>
        {
            Config(EngineFeatureCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<ICatalog>();
        repo.Setup(r => r.GetWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\Engine\ComfyUI",
            allowedConfigurationIds: [EngineFeatureCatalog.Krea2Turbo]);

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads)
            .Select(w => w.Name)
            .Should().BeEquivalentTo(["Krea-2-Turbo"]);
    }

    [Fact]
    public async Task WithoutAllowList_EveryComfyUiWorkloadIsListed()
    {
        var configs = new List<InstallationConfiguration>
        {
            Config(EngineFeatureCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<ICatalog>();
        repo.Setup(r => r.GetWorkloadsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads).Should().HaveCount(2,
            "the ordinary Workloads dialog must keep showing everything");
    }

    [Fact]
    public async Task EveryLoad_RereadsTheCatalog_SoABackgroundApplyShowsUp()
    {
        // The startup catalog update applies in the background and invalidates ICatalog; the
        // dialog must show the new content on its next load, not a list cached from before.
        var catalog = new Mock<ICatalog>();
        catalog.SetupSequence(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([Config(Guid.NewGuid(), "Before the apply")])
            .ReturnsAsync([Config(Guid.NewGuid(), "After the apply")]);

        var vm = new WorkloadsViewModel(
            catalog.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);
        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        catalog.Verify(c => c.GetWorkloadsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads)
            .Select(w => w.Name)
            .Should().BeEquivalentTo(["After the apply"]);
    }

    // --- VRAM-tier suggestion -------------------------------------------------------------
    //
    // This suggestion is not engine-exclusive: ShowWorkloadsDialogAsync forwards the resource
    // monitor on both the engine and the ordinary (non-engine) branches, so opening the
    // Workloads dialog for a user-managed ComfyUI install now also preselects a tier. Pinning
    // both halves of that: a matching monitor resolves to the expected tier, and a missing
    // monitor (the pre-existing behaviour for every caller before this task) computes no
    // suggestion at all.
    //
    // ShowDetailsAsync itself constructs a real WorkloadDetailsDialog (an Avalonia Window)
    // immediately after computing the suggestion, so it cannot be exercised directly without
    // initializing Avalonia. WorkloadsViewModel.ComputeSuggestedVramGbAsync is the same
    // computation extracted into an internal, Avalonia-free method for exactly this reason.

    [Fact]
    public async Task ComputeSuggestedVramGb_WithResourceMonitor_ResolvesTheMatchingConfiguredTier()
    {
        var resourceMonitor = new Mock<IResourceMonitorService>();
        resourceMonitor.Setup(m => m.GetSnapshotAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResourceSnapshot { VramTotalMB = 16384 }); // 16 GB card

        var vm = new WorkloadsViewModel(
            new Mock<ICatalog>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI",
            resourceMonitor: resourceMonitor.Object);

        var suggested = await vm.ComputeSuggestedVramGbAsync([8, 12, 16, 24, 32]);

        suggested.Should().Be(16, "16 GB of detected VRAM exactly matches a configured tier");
    }

    [Fact]
    public async Task ComputeSuggestedVramGb_WithoutResourceMonitor_ComputesNoSuggestion()
    {
        var vm = new WorkloadsViewModel(
            new Mock<ICatalog>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        var suggested = await vm.ComputeSuggestedVramGbAsync([8, 12, 16, 24, 32]);

        suggested.Should().BeNull(
            "no monitor means the dialog keeps its pre-existing default — the behaviour before this task");
    }

    // ── Review (#607): a user's own ComfyUI installing Outpainting-Qwen 2512 from Installer Manager got the
    // GGUF node pack, whose requirements make pip compile llama-cpp-python from source ──

    private static readonly LamaCppWheel Cu128Wheel = new()
    {
        Id = Guid.NewGuid(), Name = "JamePeng cu128", IsGPU = true, PythonVersion = "3.12", CudaVersion = "12.8",
        Url = "https://example/llama_cpp_python-0.3.20-cp312-cp312-win_amd64.whl"
    };

    private static readonly CustomNodeCheckResult NodePack = new() { Id = Guid.NewGuid(), Name = "ComfyUI_Simple_Qwen3-VL-gguf", Url = "https://github.com/KLL535/ComfyUI_Simple_Qwen3-VL-gguf", ExpectedPath = "", IsInstalled = false };
    private static readonly ModelCheckResult Model = new() { Id = Guid.NewGuid(), Name = "Qwen3-VL-8B-Abliterated-Caption-it", IsInstalled = false, SearchedPaths = [] };

    private static (WorkloadsViewModel Vm, Mock<IWorkloadInstallService> Installer, List<string> Order,
        List<IReadOnlyList<CustomNodeCheckResult>> NodesPassed) WheelSut(LlamaCppWheelOutcome outcome)
    {
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Cu128Wheel]);
        var order = new List<string>();
        var nodesPassed = new List<IReadOnlyList<CustomNodeCheckResult>>();
        var installer = new Mock<IWorkloadInstallService>();
        installer.Setup(i => i.EnsureLlamaCppWheelAsync(@"C:\ComfyUI", It.IsAny<IReadOnlyList<LamaCppWheel>>(),
                It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wheel")).ReturnsAsync(outcome);
        installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), @"C:\ComfyUI",
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> n, IReadOnlyList<ModelCheckResult> _,
                int _, IProgress<WorkloadInstallProgress>? _, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            { order.Add("nodes"); nodesPassed.Add(n); })
            .ReturnsAsync("done");
        var vm = new WorkloadsViewModel(catalog.Object, new Mock<IConfigurationCheckerService>().Object,
            installer.Object, @"C:\ComfyUI");
        return (vm, installer, order, nodesPassed);
    }

    private static InstallationConfiguration WithWheel()
    {
        var config = Config(Guid.NewGuid(), "Outpainting-Qwen 2512");
        config.InstallLamaCpp = true;
        config.SelectedLamaCppWheelId = Cu128Wheel.Id;
        return config;
    }

    private static Task<string> Install(WorkloadsViewModel vm, InstallationConfiguration config,
        IReadOnlyList<CustomNodeCheckResult> nodes, IReadOnlyList<ModelCheckResult> models) =>
        vm.InstallItemsAsync(config, nodes, models, 16, new Progress<WorkloadInstallProgress>(),
            new Progress<DownloadProgress>(), () => CancellationToken.None, CancellationToken.None);

    [Fact]
    public async Task Install_WorkloadWithAWheel_InstallsItBeforeTheNodePacks()
    {
        var (vm, installer, order, _) = WheelSut(LlamaCppWheelOutcome.Installed);

        await Install(vm, WithWheel(), [NodePack], [Model]);

        order.Should().Equal(new List<string> { "wheel", "nodes" }, "pip must find llama-cpp-python satisfied before the node pack's requirements");
        installer.Verify(i => i.EnsureLlamaCppWheelAsync(@"C:\ComfyUI", It.Is<IReadOnlyList<LamaCppWheel>>(w => w.Contains(Cu128Wheel)),
            It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()), "the service picks from the catalog's wheels by the venv");
    }

    [Fact]
    public async Task Install_WheelFails_InstallsTheModelsButNotTheNodePacks()
    {
        var (vm, _, _, nodesPassed) = WheelSut(LlamaCppWheelOutcome.Failed);

        var summary = await Install(vm, WithWheel(), [NodePack], [Model]);

        nodesPassed.Should().ContainSingle().Which.Should().BeEmpty("the node pack waits for its wheel, so Install retries both");
        summary.Should().Contain("llama-cpp-python");
    }

    // Review round 3: the workload's cp312 wheel does not install into a Python 3.13 ComfyUI; leaving the
    // packs out there would block them forever.
    [Fact]
    public async Task Install_NoWheelForThisComfyUi_StillInstallsTheNodePacks()
    {
        var (vm, _, _, nodesPassed) = WheelSut(LlamaCppWheelOutcome.NoMatchingWheel);

        await Install(vm, WithWheel(), [NodePack], [Model]);

        nodesPassed.Should().ContainSingle().Which.Should().ContainSingle();
    }

    // Review round 5: with nothing left to install the dialog got "" instead of the installer's summary line.
    [Fact]
    public async Task Install_NothingSelected_ReturnsTheInstallersOwnSummary()
    {
        var (vm, _, _, _) = WheelSut(LlamaCppWheelOutcome.Installed);

        var summary = await Install(vm, WithWheel(), [], []);

        summary.Should().Be("done", "InstallSelectedAsync words the empty case itself (\"Nothing to install.\")");
    }

    [Fact]
    public async Task Install_ModelsOnly_OrNoWheel_DoesNotTouchTheWheel()
    {
        var (vm, installer, _, _) = WheelSut(LlamaCppWheelOutcome.Installed);

        await Install(vm, WithWheel(), [], [Model]);
        await Install(vm, Config(Guid.NewGuid(), "Inpainting"), [NodePack], []);

        installer.Verify(i => i.EnsureLlamaCppWheelAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<LamaCppWheel>>(),
            It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
