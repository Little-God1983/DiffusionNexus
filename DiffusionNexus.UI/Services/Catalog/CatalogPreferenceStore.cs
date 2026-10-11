using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.Installer.SDK.Services.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>The app's catalog channel (null only when nothing could be stored) and "apply automatically".</summary>
public sealed record CatalogPreferences(string? Channel, bool ApplyAutomatically);

/// <summary>Where the app keeps its catalog channel and "apply automatically" (#618): two AppSettings columns.</summary>
public interface ICatalogPreferenceStore
{
    /// <summary>
    /// The stored preferences. On the first start with the columns the channel takes the installer's
    /// saved channel once (Stable when it has none) and stores it, so a Preview user stays on Preview;
    /// after that the two apps choose independently. Database failures propagate.
    /// </summary>
    Task<CatalogPreferences> LoadAsync(CancellationToken ct = default);

    Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default);

    Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default);
}

/// <summary>
/// A fresh DI scope per call: <see cref="IAppSettingsService"/> is transient over a scoped unit of work,
/// and this store is held by a singleton (same rule as App.axaml.cs's other settings readers).
/// </summary>
public sealed class CatalogPreferenceStore(IServiceScopeFactory scopes, IUserSettingsRepository installerSettings, IUnifiedLogger? logger = null)
    : ICatalogPreferenceStore
{
    private const string LogSource = "Catalog";

    public async Task<CatalogPreferences> LoadAsync(CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var stored = await settings.GetCatalogUpdateSettingsAsync(ct).ConfigureAwait(false);
        if (stored.Channel is not null) return new CatalogPreferences(stored.Channel, stored.ApplyAutomatically);

        // Unreadable: nothing is known, so nothing is stored; the copy is tried again on the next start (PR #619 round 1).
        if (await InstallerChannelAsync(ct).ConfigureAwait(false) is not { } copied) return new CatalogPreferences(null, stored.ApplyAutomatically);
        try
        {
            // Never creates the settings row: on a fresh install the UI creates it at the same time (PR #619 round 2).
            if (!await settings.SetCatalogChannelIfSettingsExistAsync(copied.ToString(), ct).ConfigureAwait(false))
                logger?.Info(LogCategory.General, LogSource, $"Following {copied} for this run; it is saved on the next start (no settings yet).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The installer's channel was read: follow it this run; the copy is tried again on the next start.
            logger?.Warn(LogCategory.General, LogSource, $"The {copied} catalog channel could not be saved; following it for this run.", ex.Message);
        }
        return new CatalogPreferences(copied.ToString(), stored.ApplyAutomatically);
    }

    public async Task SaveChannelAsync(CatalogChannel channel, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsService>().SetCatalogChannelAsync(channel.ToString(), ct).ConfigureAwait(false);
    }

    public async Task SaveApplyAutomaticallyAsync(bool on, CancellationToken ct = default)
    {
        using var scope = scopes.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppSettingsService>().SetApplyCatalogUpdatesAutomaticallyAsync(on, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The installer's saved channel, read-only (GetOrCreateForCurrentUserAsync would write the installer's
    /// file). Nothing usable saved: Stable. Unreadable: null (nothing is known).
    /// </summary>
    private async Task<CatalogChannel?> InstallerChannelAsync(CancellationToken ct)
    {
        string? saved;
        try
        {
            saved = (await installerSettings.GetByUserNameAsync(Environment.UserName, ct).ConfigureAwait(false))?.CatalogChannel;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger?.Warn(LogCategory.General, LogSource, "The installer's settings could not be read; following Stable for now and copying its channel on the next start.", ex.Message);
            return null;
        }

        if (CatalogChannelResolver.TryParse(saved, out var channel))
        {
            logger?.Info(LogCategory.General, LogSource, $"First start with the catalog setting: following the installer's {channel} channel from now on.",
                "Change it in Settings → Catalog; the installer keeps its own choice.");
            return channel;
        }

        logger?.Info(LogCategory.General, LogSource, "First start with the catalog setting: the installer saved no channel; following Stable.");
        return CatalogChannel.Stable;
    }
}
