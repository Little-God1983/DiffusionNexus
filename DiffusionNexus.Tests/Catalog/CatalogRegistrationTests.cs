using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared.Services;
using DiffusionNexus.UI.Services.Catalog;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>
/// The same registrations App.ConfigureServices makes, against temp paths: every SDK service the
/// app resolves must be there, and the embedded seed must be wired into the catalog options.
/// </summary>
public class CatalogRegistrationTests
{
    [Fact]
    public async Task Registrations_resolve_and_seed_the_catalog_from_the_app_assembly()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var services = new ServiceCollection();
            services.AddInstallerSdkCatalog(
                typeof(DiffusionNexus.UI.App).Assembly,
                userSettingsPath: Path.Combine(dir, "user_settings.json"),
                configure: o =>
                {
                    o.InstalledCatalogPath = Path.Combine(dir, "catalog");
                    // A developer machine may have DIFFUSIONNEXUS_CATALOG_PATH set.
                    o.LocalOverridePath = null;
                });
            using var provider = services.BuildServiceProvider();

            provider.GetRequiredService<ICatalogUpdateService>().Should().NotBeNull();
            provider.GetRequiredService<IUserSettingsRepository>().Should().NotBeNull();
            provider.GetRequiredService<DismissedMessageStore>().Should().NotBeNull();
            provider.GetRequiredService<CatalogStartupUpdater>().Should().NotBeNull();
            var catalog = provider.GetRequiredService<ICatalog>();

            (await catalog.GetWorkloadsAsync()).Should().NotBeEmpty();
            catalog.Source.Kind.Should().Be(CatalogSourceKind.Installed);
            catalog.Source.SeededFromEmbedded.Should().BeTrue();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Catalog_override_comes_from_the_environment_variable_the_installer_uses()
        => InstallerSdkServiceCollectionExtensions.CatalogPathEnvironmentVariable
            .Should().Be("DIFFUSIONNEXUS_CATALOG_PATH");
}
