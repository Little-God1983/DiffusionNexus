using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// Feature readiness looks workloads up by hardcoded catalog ids (FeatureRegistry). An id the
/// catalog no longer has must read as "not installed", never throw.
/// </summary>
public class WorkloadInstallationCheckerAdapterCatalogTests
{
    [Fact]
    public async Task A_workload_missing_from_the_catalog_is_not_installed()
    {
        var id = Guid.Parse("701DA214-2B25-44B4-A904-E4B036621564");
        var catalog = new Mock<ICatalog>();
        catalog.Setup(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((InstallationConfiguration?)null);
        var services = new ServiceCollection();
        services.AddSingleton(catalog.Object);
        using var provider = services.BuildServiceProvider();
        var adapter = new WorkloadInstallationCheckerAdapter(provider, new Mock<IConfigurationCheckerService>().Object);

        var summary = await adapter.CheckAsync(id);

        summary.IsFullyInstalled.Should().BeFalse();
        summary.WorkloadId.Should().Be(id);
        summary.MissingItems.Should().ContainSingle().Which.Should().Contain("workload catalog");
        catalog.Verify(c => c.GetWorkloadAsync(id, It.IsAny<CancellationToken>()), Times.Once);
    }
}
