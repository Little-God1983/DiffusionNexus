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

    /// <summary>
    /// Workloads the code already names that are on catalog <c>main</c> (Preview) but not yet in a Stable
    /// release, which is all the seed takes. Until that release they read as "not installed" on a machine
    /// that has only the seed. Remove an id when the seed has it; <see cref="Workloads_awaiting_a_stable_release_are_not_in_the_seed_yet"/>
    /// fails as a reminder.
    /// </summary>
    private static readonly Guid[] AwaitingStableRelease =
    [
        Guid.Parse("FE2E7606-AC36-470E-A7C5-7F8CC23ECC98"), // Upscaling-Z-Image-Turbo Vision (#608) — catalog v6
    ];

    /// <summary>Read from the code that hardcodes them, so a new feature's workload is covered without editing this test.</summary>
    private static IReadOnlyList<Guid> Ids() => FeatureRegistry.GetAll()
        .Select(r => r.WorkloadConfigurationId)
        .OfType<Guid>()
        .Concat(EngineFeatureCatalog.AllWorkloadIds)
        .Distinct()
        .Except(AwaitingStableRelease)
        .ToList();

    public static TheoryData<Guid> AwaitingIds() => new(AwaitingStableRelease);

    [Theory]
    [MemberData(nameof(AwaitingIds))]
    public async Task Workloads_awaiting_a_stable_release_are_not_in_the_seed_yet(Guid id)
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

            (await catalog.GetWorkloadAsync(id)).Should().BeNull(
                "the seed now carries this workload: remove it from AwaitingStableRelease so the main seed test covers it");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    public static TheoryData<Guid> HardcodedWorkloadIds() => new(Ids());

    [Fact]
    public void Every_hardcoded_source_contributes_ids()
    {
        // Guards the member data itself: an empty registry would turn the theory into a silent no-op.
        var ids = Ids();
        ids.Should().Contain(EngineFeatureCatalog.Krea2Turbo);
        ids.Should().HaveCountGreaterThan(EngineFeatureCatalog.AllWorkloadIds.Count - 1);
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
