using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Thumbnails;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Step 5 — turns each version's primary image into stored thumbnail bytes, recording every attempt
/// on the image row itself (#521 Plan B).
/// </summary>
/// <remarks>
/// The other four steps stamp the <i>model</i>; this one does not stamp at all. Its record lives on
/// <c>ModelImage</c>, next to the bytes it is about, which is what makes the work per image rather
/// than per model: two versions of one model are two thumbnails, two requests and two independent
/// outcomes, so nothing here groups the way <see cref="FetchImagesStep"/> does. One item is exactly
/// one request, which is what lets the plan's item count double as its request count.
/// <para>
/// There is no <c>ICivitaiRequestPacer</c> either, deliberately. The pacer is the courtesy interval
/// for Civitai's <i>API</i>; thumbnails come from the image CDN, which is a static-asset host with
/// no such budget, and pacing a library's worth of 65 KB GETs at API speed would turn a minute into
/// an hour for nobody's benefit.
/// </para>
/// <para>
/// Failures are not thrown by the provider — it answers with a
/// <see cref="Domain.Entities.ThumbnailFailureReason"/> — so the catch ladder here is only for disk
/// and database faults. There is no refusal path: a CDN 404 is already one of those reasons, and
/// the retry policy knows which reasons are final.
/// </para>
/// </remarks>
public sealed class ThumbnailsStep : ISyncStep
{
    private const string LogSource = "LibrarySync";

    private readonly IServiceScopeFactory _scopes;
    private readonly IThumbnailProvider _provider;
    private readonly IUnifiedLogger? _logger;

    public ThumbnailsStep(IServiceScopeFactory scopes, IThumbnailProvider provider, IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _logger = logger;
    }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.Thumbnails;

    /// <inheritdoc />
    public string Description => "Fetch thumbnails";

    /// <summary>One CDN GET of a ~65 KB asset, plus the decode and re-encode, per image.</summary>
    public TimeSpan EstimatedPerItem => TimeSpan.FromSeconds(0.4);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
    {
        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // As in every other step: the scope alone is not "the library", because rows left behind by
        // a source the user has since disabled are still in the database. Resolved inside the scope
        // because IAppSettingsService is transient over a scoped unit of work.
        var settings = dbScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var enabledRoots = await settings.GetEnabledLoraSourcesAsync(ct).ConfigureAwait(false);

        var candidates = await uow.SyncStates.SelectThumbnailCandidatesAsync(scope, enabledRoots, ct).ConfigureAwait(false);

        // One item per image, never grouped: the record of the attempt lives on the image, so two
        // images have nothing to share and a failure on one must not re-run the other.
        var items = new List<SyncItem>();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!options.Policy.IsThumbnailDue(candidate.ThumbnailAttemptedAt, candidate.ThumbnailFailure, now, options.ForceThumbnails))
                continue;

            items.Add(new SyncItem(candidate.ModelId, candidate.Name, candidate));
        }

        _logger?.Debug(LogCategory.General, LogSource,
            $"Thumbnails: {items.Count} of {candidates.Count} image(s) due");
        return items;
    }

    /// <inheritdoc />
    public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
    {
        var candidate = item.Payload as ThumbnailCandidate
            ?? throw new ArgumentException($"Payload must be a {nameof(ThumbnailCandidate)}.", nameof(item));

        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = DateTimeOffset.UtcNow;

        try
        {
            // Selection ran in another scope and possibly minutes ago; the row is re-read here so
            // both of the ways it can have moved on are answered against the database, not a cache.
            var image = await uow.Models.GetImageByIdAsync(candidate.ImageId, ct).ConfigureAwait(false);

            if (image is null)
            {
                _logger?.Debug(LogCategory.General, LogSource,
                    $"Skipped '{item.Name}': image {candidate.ImageId} was deleted during the run");
                return SyncItemResult.Skip;
            }

            // Filled in since selection — by the per-tile button, or by the sidecar applier. The
            // entity is loaded fresh from the database, so the deferred-BLOB sentinel (a reference,
            // not a value) can never appear here and these bytes are always real.
            if (image.ThumbnailData is { Length: > 0 }) return SyncItemResult.Skip;

            // AllowVideoDownload is false and stays false: in bulk a video is worth one poster
            // request and nothing more. The FFmpeg fallback costs megabytes per model, so it is the
            // user's to grant on a model they asked about, never a library-wide default.
            var result = await _provider
                .ProduceAsync(new ThumbnailRequest(candidate.Url, candidate.MediaType, candidate.LocalPath, AllowVideoDownload: false), ct)
                .ConfigureAwait(false);

            if (result.Succeeded)
            {
                ThumbnailWriter.ApplySuccess(image, result.Payload!, now);
                await uow.SaveChangesAsync(ct).ConfigureAwait(false);
                return SyncItemResult.Success;
            }

            // Recorded rather than merely reported: an unrecorded failure is re-attempted on every
            // run forever, which is the bug this step exists to fix. The provider already logged
            // what went wrong, so there is no second warning here.
            ThumbnailWriter.ApplyFailure(image, result.Failure!, now);
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
            return SyncItemResult.Failure(result.Failure!);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unrecorded: a cancelled item is work not done, not work that failed, and
            // stamping it would write the row off as attempted when nobody ever asked the CDN.
            throw;
        }
        catch (Exception ex) when (SyncFaults.IsItemFault(ex))
        {
            // Nothing recorded, so the row stays due and the next run re-asks. The tracker is
            // dropped first: after a rejected save the context still holds exactly the row the
            // database refused. The reason is the exception's type rather than its message —
            // the message is a database or disk detail, and this string is shown per failed item.
            uow.ClearChangeTracker();

            _logger?.Warn(LogCategory.Network, LogSource, $"Thumbnail failed for '{item.Name}': {ex.Message}");
            _logger?.Debug(LogCategory.Network, LogSource, $"Thumbnail failure detail for '{item.Name}'", ex.ToString());
            return SyncItemResult.Failure(ex.GetType().Name);
        }
    }
}
