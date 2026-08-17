using DiffusionNexus.Installer.SDK.DataAccess;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.ConfigurationChecker;
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
            Config(EngineWorkloadCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\Engine\ComfyUI",
            allowedConfigurationIds: EngineWorkloadCatalog.WorkloadIds);

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
            Config(EngineWorkloadCatalog.Krea2Turbo, "Krea-2-Turbo"),
            Config(Guid.NewGuid(), "Some other workload")
        };

        var repo = new Mock<IConfigurationRepository>();
        repo.Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(configs);

        var vm = new WorkloadsViewModel(
            repo.Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        await vm.LoadWorkloadsCommand.ExecuteAsync(null);

        vm.DiffusionNexusWorkloads.Concat(vm.InstallerWorkloads).Should().HaveCount(2,
            "the ordinary Workloads dialog must keep showing everything");
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
            new Mock<IConfigurationRepository>().Object,
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
            new Mock<IConfigurationRepository>().Object,
            new Mock<IConfigurationCheckerService>().Object,
            new Mock<IWorkloadInstallService>().Object,
            @"C:\ComfyUI");

        var suggested = await vm.ComputeSuggestedVramGbAsync([8, 12, 16, 24, 32]);

        suggested.Should().BeNull(
            "no monitor means the dialog keeps its pre-existing default — the behaviour before this task");
    }
}
