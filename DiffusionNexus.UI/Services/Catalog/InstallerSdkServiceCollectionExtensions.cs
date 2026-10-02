using System.Reflection;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using DiffusionNexus.Installer.SDK.Shared.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// The Installer SDK 2.x registrations that replaced 1.x's <c>AddDiffusionNexusDataAccess</c>: the
/// JSON workload catalog and its startup updater, the user settings file and the dismissed-banner
/// store beside it. In one
/// method so <c>CatalogRegistrationTests</c> exercises exactly what App.ConfigureServices registers.
/// </summary>
public static class InstallerSdkServiceCollectionExtensions
{
    /// <summary>A catalog checkout to read instead of the installed catalog. Same name as the installer's.</summary>
    public const string CatalogPathEnvironmentVariable = "DIFFUSIONNEXUS_CATALOG_PATH";

    /// <summary>The <c>LogicalName</c>s of the embedded seed in DiffusionNexus.UI.csproj; the two must match.</summary>
    public const string SeedArchiveResource = "catalog.zip";
    public const string SeedManifestResource = "manifest.json";

    /// <param name="seedAssembly">The assembly embedding <c>catalog.zip</c> and <c>manifest.json</c>.</param>
    /// <param name="userSettingsPath">Defaults to <see cref="UserSettingsPaths.Default"/>, the file the installer uses.</param>
    /// <param name="configure">Runs after the defaults; tests point the catalog at a temp folder.</param>
    public static IServiceCollection AddInstallerSdkCatalog(
        this IServiceCollection services,
        Assembly seedAssembly,
        string? userSettingsPath = null,
        Action<CatalogOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(seedAssembly);

        var settingsPath = userSettingsPath ?? UserSettingsPaths.Default;
        services.AddDiffusionNexusUserSettings(settingsPath);

        // The default InstalledCatalogPath (%LocalAppData%\DiffusionNexus\catalog) is shared with
        // the 3.x installer on purpose: both apps see the same catalog. CatalogOptions.Channel stays
        // at its default: CatalogStartup passes the installer's channel to the update check directly.
        services.AddDiffusionNexusCatalog(o =>
        {
            o.EmbeddedArchive = () => OpenSeed(seedAssembly, SeedArchiveResource);
            o.EmbeddedManifest = () => OpenSeed(seedAssembly, SeedManifestResource);
            // A missing path warns and falls back rather than failing to start.
            o.LocalOverridePath = Environment.GetEnvironmentVariable(CatalogPathEnvironmentVariable);
            configure?.Invoke(o);
        });

        services.AddSingleton(sp => new CatalogStartupUpdater(
            sp.GetRequiredService<ICatalogUpdateService>(),
            sp.GetService<IActivityLogService>()));

        // Beside user_settings.json, as in 1.x, so banners dismissed before the upgrade stay dismissed.
        services.AddSingleton(_ =>
        {
            var dir = Path.GetDirectoryName(settingsPath) ?? AppContext.BaseDirectory;
            return new DismissedMessageStore(Path.Combine(dir, "dismissed_messages.json"));
        });

        return services;
    }

    // A null stream would surface from the SDK as a bare ArgumentNullException with no hint at the cause.
    private static Stream OpenSeed(Assembly assembly, string name)
        => assembly.GetManifestResourceStream(name)
           ?? throw new InvalidOperationException(
               $"The embedded catalog seed '{name}' is missing from {assembly.GetName().Name}. Check its EmbeddedResource LogicalName in the csproj.");
}
