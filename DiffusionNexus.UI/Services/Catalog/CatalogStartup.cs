using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Serilog;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// The catalog's startup work, all of it in one background task (<see cref="RunAsync"/>): pick the
/// channel the installer saved, report where the catalog came from, its versions and every diagnostic
/// to the Unified Console, then check that channel for updates.
/// <para>
/// Nothing here gates the startup overlay. <see cref="ICatalog.Source"/>, <see cref="ICatalog.State"/>
/// and <see cref="ICatalog.Diagnostics"/> block on the first load, which can unpack the embedded seed
/// or wait up to 30 s on the installer's apply lock; the UI reads the catalog through the async
/// members, which await that same load. Never throws; startup never fails because of the catalog.
/// </para>
/// </summary>
public static class CatalogStartup
{
    public const string LogSource = "Installer SDK";

    public static async Task RunAsync(
        ICatalog catalog,
        IUserSettingsRepository settings,
        CatalogStartupUpdater updater,
        string? environmentChannel,
        IActivityLogService? activityLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(updater);

        var (channel, source) = await ResolveChannelAsync(settings, environmentChannel, activityLog, ct).ConfigureAwait(false);
        Report(catalog, channel, source, activityLog);
        // The channel goes in as an argument, never through CatalogOptions.Channel: the updater must
        // not depend on an earlier step having mutated a shared singleton.
        await updater.RunAsync(channel, source, ct).ConfigureAwait(false);
    }

    /// <summary>Environment, then the installer's saved setting, then Stable as a <see cref="CatalogChannelSource.Default"/>.</summary>
    public static async Task<(CatalogChannel Channel, CatalogChannelSource Source)> ResolveChannelAsync(
        IUserSettingsRepository settings,
        string? environmentChannel,
        IActivityLogService? activityLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // Read-only lookup: GetOrCreateForCurrentUserAsync would write user_settings.json, which
        // the installer owns. The main app never writes the channel.
        // The SDK's JSON repository reads a locked or corrupt file as empty instead of throwing, so the
        // result is then Default; CatalogStartupUpdater refuses to apply a Default channel over a
        // catalog on another channel. The catch covers other repository implementations.
        string? savedChannel = null;
        try
        {
            savedChannel = (await settings.GetByUserNameAsync(Environment.UserName, ct).ConfigureAwait(false))?.CatalogChannel;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "CatalogStartup: could not read the saved catalog channel");
            activityLog?.LogWarning(LogSource, "Could not read the saved catalog channel; using the environment or Stable.", ex.Message);
        }

        var (channel, source) = CatalogChannelResolver.Resolve(environmentChannel, savedChannel);
        Log.Information("CatalogStartup: following the {Channel} catalog channel ({Source})", channel, source);
        return (channel, source);
    }

    /// <summary>Blocks on the first catalog load; call it off the UI thread.</summary>
    public static void Report(ICatalog catalog, CatalogChannel channel, CatalogChannelSource source, IActivityLogService? activityLog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        try
        {
            var catalogSource = catalog.Source;
            var seeded = catalogSource.SeededFromEmbedded ? " (seeded from embedded)" : "";
            activityLog?.LogInfo(LogSource, $"Catalog: {catalogSource.Kind} at {catalogSource.Path}{seeded}");

            // State is always the installed catalog's (FileCatalog reads it from InstalledCatalogPath),
            // so under an override it does not describe what the app is reading.
            var state = catalog.State;
            var versions = $"workloads {Version(state.Workloads)}, workflows {Version(state.Workflows)}, channel {channel}";
            var label = catalogSource.Kind == CatalogSourceKind.Override
                ? "Installed catalog (not in use while the override is active)"
                : "Catalog version";
            activityLog?.LogInfo(LogSource, $"{label}: {versions}", $"Channel source: {source}");

            foreach (var diagnostic in catalog.Diagnostics)
            {
                var message = $"{diagnostic.Code}: {diagnostic.Message}";
                if (diagnostic.IsError)
                    activityLog?.LogError(LogSource, message, diagnostic.Path);
                else
                    activityLog?.LogWarning(LogSource, message, diagnostic.Path);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "CatalogStartup: failed to load the workload catalog");
            activityLog?.LogError(LogSource, "Failed to load the workload catalog", ex);
        }
    }

    private static string Version(SectionState? section) => section is null ? "n/a" : $"v{section.CatalogVersion}";
}
