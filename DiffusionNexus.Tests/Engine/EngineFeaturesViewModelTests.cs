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
}
