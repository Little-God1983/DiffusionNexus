using System.Reflection;
using DiffusionNexus.Installer.SDK.Catalog;
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

    [Theory]
    [InlineData("701DA214-2B25-44B4-A904-E4B036621564")] // Captioning   (FeatureRegistry)
    [InlineData("4C486765-A4C1-4E94-ACC2-BBAC0E405B6A")] // Inpainting   (FeatureRegistry)
    [InlineData("137929E4-5C05-4304-80D4-5D785D45FD3F")] // Outpaint     (FeatureRegistry)
    [InlineData("B853EB7C-0A0E-48A6-985E-E32B2F8848F5")] // BatchUpscale (FeatureRegistry)
    [InlineData("E79C079A-2FD7-4FE7-8086-23731092555D")] // Engine base  (EngineWorkloadCatalog / ManagedEngineInstaller)
    public async Task Embedded_seed_contains_every_hardcoded_workload(string id)
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

            (await catalog.GetWorkloadAsync(Guid.Parse(id))).Should().NotBeNull();
            catalog.Source.SeededFromEmbedded.Should().BeTrue();
            catalog.Diagnostics.Should().NotContain(d => d.Severity == CatalogDiagnosticSeverity.Error);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
