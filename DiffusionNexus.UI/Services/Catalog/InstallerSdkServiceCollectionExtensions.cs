using System.Reflection;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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

        // SDK 2.3.0: installed catalogs live in %LocalAppData%\ITL-Shared\format-N\catalog-stable|preview\, shared
        // with the installer; the old %LocalAppData%\DiffusionNexus\catalog is copied over once by the SDK.
        // CatalogUpdateCoordinator is the one writer of CatalogOptions.Channel.
        services.AddDiffusionNexusCatalog(o =>
        {
            o.EmbeddedArchive = () => OpenSeed(seedAssembly, SeedArchiveResource);
            o.EmbeddedManifest = () => OpenSeed(seedAssembly, SeedManifestResource);
            // A missing path warns and falls back rather than failing to start.
            o.LocalOverridePath = Environment.GetEnvironmentVariable(CatalogPathEnvironmentVariable);
            configure?.Invoke(o);
        });

        // The app's own catalog channel and "apply automatically" (#618). The installer's user_settings.json is
        // read once, on the first start with the columns, to copy its channel.
        services.AddSingleton<ICatalogPreferenceStore>(sp => new CatalogPreferenceStore(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IUserSettingsRepository>(),
            sp.GetService<IUnifiedLogger>()));
        services.AddSingleton<CatalogUpdateCoordinator>(sp => new CatalogUpdateCoordinator(
            sp.GetRequiredService<ICatalogUpdateService>(),
            sp.GetRequiredService<ICatalog>(),
            sp.GetRequiredService<CatalogOptions>(),
            sp.GetRequiredService<ICatalogPreferenceStore>(),
            sp.GetRequiredService<IInstallActivity>(),
            () => Environment.GetEnvironmentVariable(CatalogChannelResolver.EnvironmentVariable),
            sp.GetService<IUnifiedLogger>()));
        services.AddSingleton<ICatalogUpdateCoordinator>(sp => sp.GetRequiredService<CatalogUpdateCoordinator>());

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
