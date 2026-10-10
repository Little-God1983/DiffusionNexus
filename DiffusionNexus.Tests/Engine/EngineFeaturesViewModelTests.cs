using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Engine;

public class EngineFeaturesViewModelTests
{
    private const string Root = @"C:\Engine\ComfyUI";
    private readonly Mock<ICatalog> _catalog = new();
    private readonly Mock<IConfigurationCheckerService> _checker = new();
    private readonly Mock<IWorkloadInstallService> _installer = new();
    private readonly Dictionary<Guid, ConfigurationCheckResult> _state = new();

    public EngineFeaturesViewModelTests()
    {
        foreach (var id in EngineFeatureCatalog.AllWorkloadIds)
        {
            var config = new InstallationConfiguration { Id = id, Name = id.ToString() };
            config.Vram.VramProfiles = "8,16,24";
            _catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(config);
        }

        _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration c, string _, ConfigurationCheckOptions? _, CancellationToken _) => _state[c.Id]);

        // The Vision row is installed unless a test says otherwise.
        _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(0, 3, 0, 7);
    }

    private static ConfigurationCheckResult Result(int nodesMissing, int nodesPresent, int modelsMissing, int modelsPresent) => new()
    {
        OverallStatus = ConfigurationStatus.Partial,
        CustomNodesStatus = ConfigurationStatus.Partial,
        ModelsStatus = ConfigurationStatus.Partial,
        InstallationType = ComfyUIInstallationType.Manual,
        CustomNodeResults = Enumerable.Range(0, nodesMissing + nodesPresent).Select(i => new CustomNodeCheckResult
        {
            Id = Guid.NewGuid(), Name = $"node{i}", Url = "u", IsInstalled = i >= nodesMissing, ExpectedPath = "p"
        }).ToList(),
        ModelResults = Enumerable.Range(0, modelsMissing + modelsPresent).Select(i => new ModelCheckResult
        {
            Id = Guid.NewGuid(), Name = $"model{i}", IsInstalled = i >= modelsMissing, SearchedPaths = []
        }).ToList()
    };

    private EngineFeaturesViewModel Sut(EngineFeature? preselect = null) =>
        new(_catalog.Object, _checker.Object, _installer.Object, Root, preselect: preselect,
            freeSpaceProbe: _ => 412L * 1024 * 1024 * 1024);

    private EngineFeatureRowViewModel Row(EngineFeaturesViewModel vm, EngineFeature f) =>
        vm.Rows.Single(r => r.Definition.Feature == f);

    [Fact]
    public async Task Load_DerivesStatusPerRow()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 3, 2);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Partial);
        Row(vm, EngineFeature.InpaintOutpaint).StatusText.Should().Be("Partial · 1 node pack and 3 models missing");
        Row(vm, EngineFeature.InpaintOutpaint).NeedsText.Should().Be("1 node pack · 5 models");
        Row(vm, EngineFeature.Canvas).Status.Should().Be(EngineFeatureStatus.Installed);
        Row(vm, EngineFeature.Canvas).IsSelected.Should().BeTrue("installed rows are ticked");
        Row(vm, EngineFeature.Canvas).IsSelectable.Should().BeFalse("and locked");
    }

    [Theory]
    [InlineData(1, 1, 0, 5, "Partial · 1 node pack missing")]
    [InlineData(2, 0, 0, 5, "Partial · 2 node packs missing")]
    [InlineData(0, 1, 1, 4, "Partial · 1 model missing")]
    [InlineData(0, 1, 2, 3, "Partial · 2 models missing")]
    public async Task Partial_NamesWhatIsMissing(int nodesMissing, int nodesPresent, int modelsMissing, int modelsPresent, string expected)
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(nodesMissing, nodesPresent, modelsMissing, modelsPresent);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).StatusText.Should().Be(expected);
    }

    [Fact]
    public async Task NothingPresent_IsNotInstalled()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.NotInstalled);
        Row(vm, EngineFeature.InpaintOutpaint).StatusText.Should().Be("Not installed");
    }

    // #606 code review 3 (H8): an Installed row is ticked and locked; when a re-check finds it
    // incomplete it became selectable while still ticked, and slipped into the next install.
    [Fact]
    public async Task InstalledRow_ThatTurnsPartialOnARecheck_IsUnticked()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        Row(vm, EngineFeature.Canvas).IsSelected.Should().BeTrue();

        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 1, 2); // a model file was deleted
        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.Canvas).Status.Should().Be(EngineFeatureStatus.Partial);
        Row(vm, EngineFeature.Canvas).IsSelected.Should().BeFalse("the user re-ticks it deliberately");
        vm.InstallSelectedCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task PartialRow_TheUserTicked_StaysTicked_WhenARecheckIsStillPartial()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 3, 2);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut();
        await vm.LoadCommand.ExecuteAsync(null);
        Row(vm, EngineFeature.InpaintOutpaint).IsSelected = true;

        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 2, 3);
        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).IsSelected.Should().BeTrue();
    }

    [Fact]
    public async Task Preselect_TicksThatRow_AndTheFooterCountsIt()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);

        await vm.LoadCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).IsSelected.Should().BeTrue();
        vm.FooterText.Should().Be("Selected: 1 feature · 412 GB free on C:\\");
        vm.InstallSelectedCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Install_PassesOnlyTheMissingItems_FromAFreshCheck_AndSkipsInstalledRows()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 3, 2);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);

        // Between load and install, one model arrived (e.g. installed by another surface).
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 2, 3);

        IReadOnlyList<CustomNodeCheckResult>? nodes = null;
        IReadOnlyList<ModelCheckResult>? models = null;
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), Root,
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> n, IReadOnlyList<ModelCheckResult> m,
                int _, IProgress<WorkloadInstallProgress>? _, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            { nodes = n; models = m; })
            .ReturnsAsync("done");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        _installer.Verify(i => i.InstallSelectedAsync(
            It.Is<InstallationConfiguration>(c => c.Id == EngineFeatureCatalog.InpaintingQwen2512), Root,
            It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
            It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()), Times.Once);
        _installer.Verify(i => i.InstallSelectedAsync(
            It.Is<InstallationConfiguration>(c => c.Id == EngineFeatureCatalog.Krea2Turbo), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
            It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
            It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()), Times.Never, "installed rows are not reinstalled");
        nodes.Should().HaveCount(1).And.OnlyContain(n => !n.IsInstalled);
        models.Should().HaveCount(2, "the model that arrived after load must not be downloaded again")
            .And.OnlyContain(m => !m.IsInstalled);
        vm.DidInstall.Should().BeTrue();
    }

    [Fact]
    public async Task Install_RechecksEveryRowAfterwards()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5))
            .ReturnsAsync("done");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Installed);
        vm.IsInstalling.Should().BeFalse();
    }

    [Fact]
    public async Task InstallFailure_MarksTheRowAsError_AndEndsTheInstall()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("HF returned 503"));

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.IsInstalling.Should().BeFalse();
        vm.ProgressText.Should().Contain("HF returned 503");
        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.NotInstalled,
            "the re-check after a failed install shows what is really on disk");
        vm.DidInstall.Should().BeTrue(
            "an install that threw may have left files behind, so the caller must still re-sync and refresh");
    }

    private void InstallReturns(string summary, Action? onInstall = null) =>
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => onInstall?.Invoke())
            .ReturnsAsync(summary);

    [Fact]
    public async Task Install_WithMissingNodePacks_ReportsThatNodePacksWereInstalled()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 2, 3);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        InstallReturns("1 node(s) installed, 2 model(s) downloaded");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.DidInstall.Should().BeTrue();
        vm.DidInstallNodePacks.Should().BeTrue("a running Engine must restart to load new node packs");
    }

    [Fact]
    public async Task Install_ModelsOnly_DoesNotReportNodePacks()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 2, 3);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        InstallReturns("2 model(s) downloaded");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.DidInstall.Should().BeTrue();
        vm.DidInstallNodePacks.Should().BeFalse("a running ComfyUI picks up new model files without a restart");
    }

    [Fact]
    public async Task Install_ThatLeavesTheRowPartial_SaysFinishedWithProblems()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        InstallReturns("1 node(s) installed, 4 model(s) downloaded, 1 model(s) failed",
            () => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 1, 4));

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Row(vm, EngineFeature.InpaintOutpaint).Status.Should().Be(EngineFeatureStatus.Partial);
        vm.ProgressText.Should().Be(
            "Finished with problems: Inpaint & Outpaint: 1 node(s) installed, 4 model(s) downloaded, 1 model(s) failed. " +
            "See the Unified Console for details.");
    }

    [Fact]
    public async Task Install_ThatCompletesTheRow_SaysDone_WithTheSummary()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        InstallReturns("1 node(s) installed, 5 model(s) downloaded",
            () => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5));

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.ProgressText.Should().Be("Done. Inpaint & Outpaint: 1 node(s) installed, 5 model(s) downloaded.");
    }

    [Fact]
    public async Task InstallEnds_EvenWhenTheRecheckThrows()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => _checker.Setup(c => c.CheckConfigurationAsync(It.IsAny<InstallationConfiguration>(), Root,
                    It.IsAny<ConfigurationCheckOptions?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException()))
            .ReturnsAsync("done");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        vm.IsInstalling.Should().BeFalse("a failing re-check must not leave the dialog locked");
    }

    // ── #607: the Qwen3-VL GGUF node needs a prebuilt llama-cpp-python wheel, picked for the Engine's CUDA ──

    private static readonly LamaCppWheel Cu128 = new()
    {
        Id = Guid.Parse("F119F4D4-EF71-484F-8213-0ABC49F26900"), Name = "JamePeng cu128", IsGPU = true,
        PythonVersion = "3.12", CudaVersion = "12.8", Url = "https://example/llama_cpp_python-0.3.20-cp312-cp312-win_amd64.whl"
    };
    private static readonly LamaCppWheel Cu130 = new()
    {
        Id = Guid.NewGuid(), Name = "JamePeng cu130", IsGPU = true,
        PythonVersion = "3.12", CudaVersion = "13.0", Url = "https://example/llama_cpp_python-0.4.2+cu130-cp312-cp312-win_amd64.whl"
    };

    private async Task<EngineFeaturesViewModel> VisionNeedsNodePacksAsync()
    {
        _catalog.Setup(c => c.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Cu128, Cu130]);
        var engine = (await _catalog.Object.GetWorkloadAsync(EngineFeatureCatalog.Krea2Turbo))!;
        engine.Torch.CudaVersion = "13.0";
        engine.Python.PythonVersion = "3.12";
        var vision = (await _catalog.Object.GetWorkloadAsync(EngineFeatureCatalog.OutpaintingQwen2512))!;
        vision.SelectedLamaCppWheelId = Cu128.Id; // the workload's own (standalone) choice: a cu128 torch
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(2, 1, 0, 7);
        InstallReturns("2 node(s) installed", () => _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(0, 3, 0, 7));
        _installer.Setup(i => i.InstallLlamaCppWheelAsync(Root, It.IsAny<LamaCppWheel>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        var vm = Sut(EngineFeature.OutpaintVision);
        await vm.LoadCommand.ExecuteAsync(null);
        return vm;
    }

    [Fact]
    public async Task VisionInstall_InstallsTheWheelForTheEnginesCuda_BeforeTheNodePacks()
    {
        var vm = await VisionNeedsNodePacksAsync();
        var order = new List<string>();
        _installer.Setup(i => i.InstallLlamaCppWheelAsync(Root, Cu130, It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add("wheel")).ReturnsAsync(true);
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => { order.Add("nodes"); _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(0, 3, 0, 7); })
            .ReturnsAsync("2 node(s) installed");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        order.Should().Equal(new List<string> { "wheel", "nodes" }, "pip must find llama-cpp-python satisfied before the node pack's requirements");
        _installer.Verify(i => i.InstallLlamaCppWheelAsync(Root, Cu128, It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()),
            Times.Never, "the workload's cu128 wheel would run on the CPU in a cu130 Engine");
        vm.ProgressText.Should().StartWith("Done.");
    }

    // Review: a wheel that failed once was never retried — the node pack landed, the row read Installed,
    // and the next Install found nothing missing.
    [Fact]
    public async Task VisionInstall_WheelFails_LeavesTheNodePacksOut_SoTheNextInstallRetries()
    {
        var vm = await VisionNeedsNodePacksAsync();
        _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(2, 1, 1, 6);
        await vm.LoadCommand.ExecuteAsync(null);
        Row(vm, EngineFeature.OutpaintVision).IsSelected = true;
        _installer.Setup(i => i.InstallLlamaCppWheelAsync(Root, Cu130, It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var nodesPassed = new List<IReadOnlyList<CustomNodeCheckResult>>();
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> n, IReadOnlyList<ModelCheckResult> _,
                int _, IProgress<WorkloadInstallProgress>? _, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
                nodesPassed.Add(n))
            .ReturnsAsync("1 model(s) downloaded");

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        nodesPassed.Should().ContainSingle().Which.Should().BeEmpty("the node packs wait for their wheel; the model still comes");
        Row(vm, EngineFeature.OutpaintVision).Status.Should().NotBe(EngineFeatureStatus.Installed);
        vm.ProgressText.Should().StartWith("Finished with problems").And.Contain("llama-cpp-python");
    }

    [Fact]
    public async Task VisionInstall_ModelsOnly_DoesNotTouchTheWheel()
    {
        var vm = await VisionNeedsNodePacksAsync();
        _state[EngineFeatureCatalog.OutpaintingQwen2512] = Result(0, 3, 1, 6);
        await vm.LoadCommand.ExecuteAsync(null);
        Row(vm, EngineFeature.OutpaintVision).IsSelected = true;

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        _installer.Verify(i => i.InstallLlamaCppWheelAsync(It.IsAny<string>(), It.IsAny<LamaCppWheel>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task InpaintInstall_HasNoWheelToInstall()
    {
        _catalog.Setup(c => c.GetLamaCppWheelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([Cu130]);
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        InstallReturns("done", () => _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5));
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        _installer.Verify(i => i.InstallLlamaCppWheelAsync(It.IsAny<string>(), It.IsAny<LamaCppWheel>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void PickLlamaCppWheel_MatchesCudaAndPython_OrNothing()
    {
        EngineFeaturesViewModel.PickLlamaCppWheel([Cu128, Cu130], "13.0", "3.12").Should().BeSameAs(Cu130);
        EngineFeaturesViewModel.PickLlamaCppWheel([Cu128, Cu130], "12.8", "3.12").Should().BeSameAs(Cu128);
        EngineFeaturesViewModel.PickLlamaCppWheel([Cu128, Cu130], "12.4", "3.12").Should().BeNull("another CUDA's wheel loads but runs on the CPU");
        EngineFeaturesViewModel.PickLlamaCppWheel([Cu130], "13.0", "3.13").Should().BeNull();
    }

    // Smoke 2: "Done. …" was replaced 3 ms later by the last node pack's own progress line.
    [Fact]
    public async Task ProgressThatArrivesAfterTheInstallReturned_DoesNotOverwriteTheOutcome()
    {
        _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(1, 0, 5, 0);
        _state[EngineFeatureCatalog.Krea2Turbo] = Result(0, 2, 0, 3);
        IProgress<WorkloadInstallProgress>? captured = null;
        _installer.Setup(i => i.InstallSelectedAsync(It.IsAny<InstallationConfiguration>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<CustomNodeCheckResult>>(), It.IsAny<IReadOnlyList<ModelCheckResult>>(),
                It.IsAny<int>(), It.IsAny<IProgress<WorkloadInstallProgress>?>(), It.IsAny<IProgress<DownloadProgress>?>(),
                It.IsAny<Func<CancellationToken>?>(), It.IsAny<CancellationToken>()))
            .Callback((InstallationConfiguration _, string _, IReadOnlyList<CustomNodeCheckResult> _, IReadOnlyList<ModelCheckResult> _,
                int _, IProgress<WorkloadInstallProgress>? p, IProgress<DownloadProgress>? _, Func<CancellationToken>? _, CancellationToken _) =>
            { captured = p; _state[EngineFeatureCatalog.InpaintingQwen2512] = Result(0, 1, 0, 5); })
            .ReturnsAsync("1 node(s) installed, 5 model(s) downloaded");
        var vm = Sut(EngineFeature.InpaintOutpaint);
        await vm.LoadCommand.ExecuteAsync(null);
        await vm.InstallSelectedCommand.ExecuteAsync(null);
        vm.ProgressText.Should().StartWith("Done.");

        captured!.Report(new WorkloadInstallProgress { ItemName = "ComfyUI-GGUF", Message = "Installed ComfyUI-GGUF", IsSuccess = true });
        await Task.Delay(100); // Progress<T> posts the report; give it time to land

        vm.ProgressText.Should().StartWith("Done.");
    }
}
