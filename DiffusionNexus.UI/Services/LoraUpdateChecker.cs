using System.Net;
using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services;

/// <inheritdoc />
public sealed class LoraUpdateChecker : ILoraUpdateChecker
{
    private const int MaxConcurrency = 4;

    /// <summary>How long to pause new checks after a 429 from Civitai.</summary>
    private static readonly TimeSpan RateLimitBackoff = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ICivitaiClient _civitaiClient;
    private readonly IAppSettingsService _settingsService;
    private readonly IUnifiedLogger? _logger;

    /// <summary>
    /// UTC time before which new requests should be skipped because Civitai
    /// recently returned 429. Shared across pagination events so a rate-limit
    /// during one batch cools down subsequent batches too.
    /// </summary>
    private DateTime _rateLimitedUntilUtc;
    private readonly object _rateLimitLock = new();

    public LoraUpdateChecker(
        IServiceScopeFactory scopeFactory,
        ICivitaiClient civitaiClient,
        IAppSettingsService settingsService,
        IUnifiedLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(civitaiClient);
        ArgumentNullException.ThrowIfNull(settingsService);
        _scopeFactory = scopeFactory;
        _civitaiClient = civitaiClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task CheckVisibleAsync(
        IEnumerable<ModelTileViewModel> tiles,
        TimeSpan staleness,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiles);

        if (staleness <= TimeSpan.Zero)
        {
            return;
        }

        if (IsRateLimited())
        {
            _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                "Skipping update-check batch — backing off after recent 429.");
            return;
        }

        var nowUtc = DateTime.UtcNow;
        var cutoffUtc = nowUtc - staleness;

        // Snapshot eligible tiles, deduplicated by the Civitai model id so we
        // don't issue redundant calls for grouped tiles.
        var pending = tiles
            .Where(t => t is not null)
            .Where(t => TryGetCivitaiModelId(t!) is not null)
            .Where(t => IsStale(t!, cutoffUtc))
            .GroupBy(t => TryGetCivitaiModelId(t!)!.Value)
            .Select(g => (CivitaiModelId: g.Key, Tile: g.First()))
            .ToList();

        if (pending.Count == 0)
        {
            return;
        }

        string? apiKey = null;
        try
        {
            apiKey = await _settingsService.GetCivitaiApiKeyAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Missing API key is non-fatal; many models are publicly accessible.
            _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                $"Could not read Civitai API key: {ex.Message}");
        }

        _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
            $"Checking {pending.Count} visible LoRA(s) for new versions (staleness {staleness.TotalDays:0.##}d)");

        using var semaphore = new SemaphoreSlim(MaxConcurrency);
        var tasks = pending.Select(p => CheckOneAsync(p.CivitaiModelId, p.Tile, apiKey, semaphore, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task CheckOneAsync(
        int civitaiModelId,
        ModelTileViewModel tile,
        string? apiKey,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Honour a backoff that another in-flight call may have triggered.
            if (IsRateLimited())
            {
                return;
            }

            var remote = await _civitaiClient
                .GetModelAsync(civitaiModelId, apiKey, cancellationToken)
                .ConfigureAwait(false);

            if (remote is null)
            {
                // 404 — model was removed or made private. Leave the tile alone.
                return;
            }

            var totalVersions = remote.ModelVersions?.Count ?? 0;
            var checkedAtUtc = DateTime.UtcNow;

            var modelId = tile.ModelEntity?.Id;
            if (modelId is null || modelId.Value <= 0)
            {
                return;
            }

            await PersistAsync(modelId.Value, totalVersions, checkedAtUtc, cancellationToken)
                .ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
                tile.UpdateRemoteVersionCount(totalVersions, checkedAtUtc));
        }
        catch (OperationCanceledException)
        {
            // User paginated or filter changed — drop quietly.
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TriggerBackoff();
            _logger?.Warn(LogCategory.Network, "LoraUpdateChecker",
                $"Civitai rate-limited update check for model {civitaiModelId}; pausing for {RateLimitBackoff.TotalSeconds:0}s");
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already handled above when GetModelAsync returns null, but kept for safety.
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Network, "LoraUpdateChecker",
                $"Update check failed for model {civitaiModelId}: {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task PersistAsync(int modelId, int totalVersions, DateTime checkedAtUtc, CancellationToken cancellationToken)
    {
        // Fresh scope so the DbContext is not shared with any UI-thread work.
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.Models
            .UpdateUpdateCheckMetadataAsync(modelId, totalVersions, checkedAtUtc, cancellationToken)
            .ConfigureAwait(false);
    }

    private static int? TryGetCivitaiModelId(ModelTileViewModel tile)
    {
        var model = tile.ModelEntity;
        if (model is null) return null;
        // Prefer the page id (groups multiple local models under one Civitai page);
        // fall back to CivitaiId for legacy rows that have not been backfilled.
        return model.CivitaiModelPageId ?? model.CivitaiId;
    }

    private static bool IsStale(ModelTileViewModel tile, DateTime cutoffUtc)
    {
        var last = tile.ModelEntity?.LastCheckedForUpdatesUtc;
        return last is null || last.Value < cutoffUtc;
    }

    private bool IsRateLimited()
    {
        lock (_rateLimitLock)
        {
            return DateTime.UtcNow < _rateLimitedUntilUtc;
        }
    }

    private void TriggerBackoff()
    {
        lock (_rateLimitLock)
        {
            var newDeadline = DateTime.UtcNow + RateLimitBackoff;
            if (newDeadline > _rateLimitedUntilUtc)
            {
                _rateLimitedUntilUtc = newDeadline;
            }
        }
    }
}
