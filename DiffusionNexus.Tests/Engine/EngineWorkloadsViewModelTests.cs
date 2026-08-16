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
}
