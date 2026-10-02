using System.Reflection;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// Without an embedded seed a fresh or offline machine has an empty catalog: the Installer Manager
/// lists no workloads and every feature readiness check reports "not installed". This also catches
/// a seed refresh (Scripts/Update-CatalogSeed.ps1) that drops a workload the app hardcodes.
/// </summary>
public class CatalogSeedTests
{
    private static readonly Assembly Ui = typeof(DiffusionNexus.UI.App).Assembly;

    /// <summary>Read from the code that hardcodes them, so a new feature's workload is covered without editing this test.</summary>
    private static IReadOnlyList<Guid> Ids() => FeatureRegistry.GetAll()
        .Select(r => r.WorkloadConfigurationId)
        .OfType<Guid>()
        .Concat(EngineWorkloadCatalog.WorkloadIds)
        .Distinct()
        .ToList();

    public static TheoryData<Guid> HardcodedWorkloadIds() => new(Ids());

    [Fact]
    public void Every_hardcoded_source_contributes_ids()
    {
        // Guards the member data itself: an empty registry would turn the theory into a silent no-op.
        var ids = Ids();
        ids.Should().Contain(EngineWorkloadCatalog.Krea2Turbo);
        ids.Should().HaveCountGreaterThan(EngineWorkloadCatalog.WorkloadIds.Count);
    }

    [Theory]
    [MemberData(nameof(HardcodedWorkloadIds))]
    public async Task Embedded_seed_contains_every_hardcoded_workload(Guid id)
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var services = new ServiceCollection();
            services.AddDiffusionNexusCatalog(o =>
            {
                o.InstalledCatalogPath = dir;
                o.EmbeddedArchive = () => Ui.GetManifestResourceStream("catalog.zip")!;
                o.EmbeddedManifest = () => Ui.GetManifestResourceStream("manifest.json")!;
            });
            using var provider = services.BuildServiceProvider();
            var catalog = provider.GetRequiredService<ICatalog>();

            (await catalog.GetWorkloadAsync(id)).Should().NotBeNull();
            catalog.Source.SeededFromEmbedded.Should().BeTrue();
            catalog.Diagnostics.Should().NotContain(d => d.Severity == CatalogDiagnosticSeverity.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
