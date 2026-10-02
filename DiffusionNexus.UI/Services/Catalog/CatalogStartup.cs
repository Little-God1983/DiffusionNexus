using DiffusionNexus.Domain.Services;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Serilog;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>
/// The first catalog load at startup: picks the channel the installer saved, then reports where the
/// catalog came from, its versions and every diagnostic to the Unified Console.
/// <para>
/// Call it off the UI thread: <see cref="ICatalog.Source"/>, <see cref="ICatalog.State"/> and
/// <see cref="ICatalog.Diagnostics"/> block, and the first read can unpack the embedded seed.
/// Never throws; startup never fails because of the catalog.
/// </para>
/// </summary>
public static class CatalogStartup
{
    public const string LogSource = "Installer SDK";

    /// <returns>The channel now set on <paramref name="options"/>.</returns>
    public static async Task<CatalogChannel> InitializeAsync(
        ICatalog catalog,
        CatalogOptions options,
        IUserSettingsRepository settings,
        string? environmentChannel,
        IActivityLogService? activityLog,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(settings);

        // Read-only lookup: GetOrCreateForCurrentUserAsync would write user_settings.json, which
        // the installer owns. The main app never writes the channel.
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
        options.Channel = channel;
        Log.Information("CatalogStartup: following the {Channel} catalog channel ({Source})", channel, source);

        try
        {
            var catalogSource = catalog.Source;
            var seeded = catalogSource.SeededFromEmbedded ? " (seeded from embedded)" : "";
            activityLog?.LogInfo(LogSource, $"Catalog: {catalogSource.Kind} at {catalogSource.Path}{seeded}");

            var state = catalog.State;
            activityLog?.LogInfo(LogSource,
                $"Catalog version: workloads v{state.Workloads?.CatalogVersion}, workflows v{state.Workflows?.CatalogVersion}, channel {channel}",
                $"Channel source: {source}");

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

        return channel;
    }
}
