using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using Serilog;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// The catalog's startup work, all of it in one background task (<see cref="RunAsync"/>): resolve the
/// channel this app follows (Settings → Catalog, #618), report where the catalog came from, its versions and
/// every diagnostic to the Unified Console, then the coordinator's startup check.
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

    /// <summary>
    /// The catalog's startup work in one background task: resolve the channel this app follows (before anything
    /// reads the catalog, so the first read takes that channel's folder), report the catalog to the Unified
    /// Console, then the coordinator's startup check. Never throws.
    /// </summary>
    public static async Task RunAsync(ICatalog catalog, ICatalogUpdateCoordinator coordinator, IActivityLogService? activityLog, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);

        var channel = await coordinator.ResolveChannelAsync(ct).ConfigureAwait(false);
        Report(catalog, channel, coordinator.ChannelSource, activityLog);
        await coordinator.CheckAtStartupAsync(ct).ConfigureAwait(false);
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
            // The channel shown is the installed catalog's own, which can differ from the one followed
            // (an embedded seed in a channel folder records the seed's). An SDK 2.0.0 state records no
            // section channel, only the file-level stamp.
            var state = catalog.State;
            var installedChannel = state.Workloads?.Channel ?? state.Workflows?.Channel ?? state.Channel;
            var versions = $"workloads {Version(state.Workloads)}, workflows {Version(state.Workflows)}, channel {installedChannel}";
            var label = catalogSource.Kind == CatalogSourceKind.Override
                ? "Installed catalog (not in use while the override is active)"
                : "Catalog version";
            activityLog?.LogInfo(LogSource, $"{label}: {versions}", $"Following {channel} ({source})");

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
