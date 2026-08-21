using System.Text.Json;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Step 3 — fetches the Civitai image records for versions identified on Civitai but holding no
/// images locally (#521 WP2).
/// </summary>
/// <remarks>
/// The images twin of <see cref="FetchTagsStep"/>, and the same fix: a version Civitai has no
/// images for must be recorded as asked-and-answered or it is re-fetched forever. The one
/// structural difference is that the work is per <i>version</i> while <c>ImagesCheckedAt</c> lives
/// on the <i>model</i>, so one <see cref="SyncItem"/> carries all of a model's versions and the
/// stamp lands once, after the last of them. If any version faults, the model is left unstamped
/// and the whole group is retried — cheaper to repeat than to record a half-truth.
/// </remarks>
public sealed class FetchImagesStep : ISyncStep
{
    private const string LogSource = "LibrarySync";

    private readonly IServiceScopeFactory _scopes;
    private readonly CivitaiMetadataApplier _civitai;
    private readonly IUnifiedLogger? _logger;

    public FetchImagesStep(IServiceScopeFactory scopes, CivitaiMetadataApplier civitai, IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _civitai = civitai ?? throw new ArgumentNullException(nameof(civitai));
        _logger = logger;
    }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.FetchImages;

    /// <inheritdoc />
    public string Description => "Fetch image records";

    /// <summary>One version call plus the client's pacing, per version of the model.</summary>
    public TimeSpan EstimatedPerItem => TimeSpan.FromSeconds(1.6);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
    {
        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        // "The library" is what the enabled LoRA sources hold, so the scope alone is not enough:
        // without the roots the selection also picks up rows left behind by sources the user has
        // since disabled or removed (#521 WP2 acceptance, R2). Resolved inside the scope because
        // IAppSettingsService is transient over a scoped unit of work.
        var settings = dbScope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        var enabledRoots = await settings.GetEnabledLoraSourcesAsync(ct).ConfigureAwait(false);

        var candidates = await uow.SyncStates.SelectImageCandidatesAsync(scope, enabledRoots, ct).ConfigureAwait(false);

        // Grouped by model because that is the granularity of the stamp: two versions of one model
        // share a single ImagesCheckedAt, so they must succeed or fail together.
        var items = new List<SyncItem>();
        foreach (var group in candidates
                     .Where(c => options.Policy.IsFetchDue(c.ImagesCheckedAt, options.ForceImages))
                     .GroupBy(c => c.ModelId))
        {
            ct.ThrowIfCancellationRequested();

            var versions = group.ToList();
            items.Add(new SyncItem(group.Key, versions[0].Name, versions));
        }

        _logger?.Debug(LogCategory.General, LogSource,
            $"Images: {items.Count} model(s) / {candidates.Count} version(s) due");
        return items;
    }

    /// <inheritdoc />
    public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
    {
        var candidates = item.Payload as IReadOnlyList<ImageCandidate>
            ?? throw new ArgumentException($"Payload must be a list of {nameof(ImageCandidate)}.", nameof(item));

        if (candidates.Count == 0) return SyncItemResult.Skip;

        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = DateTimeOffset.UtcNow;
        var inFlight = candidates[0];

        try
        {
            // "Answered" is any non-null result: Civitai listed the version, whether or not it had
            // images for it (a 0 also covers the local version row vanishing mid-run, which the
            // applier cannot distinguish and which needs no separate handling here).
            var versionsAnswered = 0;
            var versionsGone = 0;
            var imagesAdded = 0;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                // Which version the refusal handler names in its one warning line.
                inFlight = candidate;

                var added = await _civitai
                    .ApplyImagesAsync(uow, candidate.ModelId, candidate.VersionId, candidate.CivitaiVersionId, apiKey, ct)
                    .ConfigureAwait(false);

                if (added is null)
                {
                    versionsGone++;
                    continue;
                }

                versionsAnswered++;
                imagesAdded += added.Value;
            }

            // See FetchTagsStep: the model may have been deleted mid-run, and a state row whose
            // PK/FK points at nothing is worse than no state row at all.
            if (await uow.Models.GetByIdAsync(item.ModelId, ct).ConfigureAwait(false) is null)
            {
                _logger?.Debug(LogCategory.General, LogSource,
                    $"Skipped '{item.Name}': model {item.ModelId} was deleted during the run");
                return SyncItemResult.Skip;
            }

            // One stamp for the model, after its last version — including when every version came
            // back empty, which is the whole point of the step.
            await StampAsync(uow, item.ModelId, now, ct).ConfigureAwait(false);

            // One line for the whole item, and only now: logged from inside the loop it would both
            // repeat per version and claim a stamp that a later version's fault could still cancel.
            if (versionsGone > 0)
            {
                _logger?.Warn(LogCategory.Network, LogSource,
                    $"{item.Name}: Civitai returned no version for {versionsGone} of {candidates.Count} version id(s); marked images as checked");
            }

            // Nothing on Civitai answered at all: stamped (final), but not a success either.
            if (versionsAnswered == 0) return SyncItemResult.Skip;

            _logger?.Debug(LogCategory.Network, LogSource,
                $"Images for '{item.Name}': {imagesAdded} added across {versionsAnswered} version(s)");
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unstamped: a cancelled item is work not done, not work that failed.
            throw;
        }
        catch (HttpRequestException ex) when (SyncFaults.IsFinalRefusal(ex))
        {
            return await RecordRefusalAsync(uow, item, inFlight, ex, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (SyncFaults.IsItemFault(ex))
        {
            // No stamp for the whole model — the versions that did land keep their images (they
            // were saved as they went), and the next run re-asks the group. Re-asking a version
            // that already has images is free: the repository no longer selects it. The tracker is
            // dropped first: the applier interleaves reads with mutations, and after a rejected save
            // the context is still holding exactly the rows the database refused.
            uow.ClearChangeTracker();

            _logger?.Warn(LogCategory.Network, LogSource, $"Image fetch failed for '{item.Name}': {ex.Message}");
            _logger?.Debug(LogCategory.Network, LogSource, $"Image fetch failure detail for '{item.Name}'", ex.ToString());
            return SyncItemResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Records "Civitai will not show us this version" as a final answer for the whole model:
    /// stamped, warned about once, skipped. A refusal is not a hiccup — an early-access or
    /// restricted page answers the same way tomorrow — and leaving it unstamped means re-asking on
    /// every run forever, which is the bug this step exists to fix. It covers the model rather than
    /// the one version because <c>ImagesCheckedAt</c> is per model; the remaining versions of a
    /// model Civitai is refusing us are not going to answer differently.
    /// </summary>
    private async Task<SyncItemResult> RecordRefusalAsync(
        IUnitOfWork uow, SyncItem item, ImageCandidate inFlight, HttpRequestException refusal,
        DateTimeOffset now, CancellationToken ct)
    {
        uow.ClearChangeTracker();

        // As on the success path: stamping a model deleted mid-run would Add a state row whose
        // PK/FK points at nothing.
        if (await uow.Models.GetByIdAsync(item.ModelId, ct).ConfigureAwait(false) is null)
        {
            _logger?.Debug(LogCategory.General, LogSource,
                $"Skipped '{item.Name}': model {item.ModelId} was deleted during the run");
            return SyncItemResult.Skip;
        }

        try
        {
            await StampAsync(uow, item.ModelId, now, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && SyncFaults.IsItemFault(ex))
        {
            // Recording the answer failed; the answer stands. Report the item as failed so the run
            // carries on rather than taking a database fault out to the top of the sync.
            _logger?.Error(LogCategory.General, LogSource,
                $"Could not mark '{item.Name}' as checked: {ex.Message}", ex);
            return SyncItemResult.Failure(refusal.Message);
        }

        _logger?.Warn(LogCategory.Network, LogSource,
            $"{item.Name}: Civitai refused ({SyncFaults.RefusalCode(refusal)}) for id {inFlight.CivitaiVersionId}; marked as checked");
        return SyncItemResult.Skip;
    }

    private static async Task StampAsync(IUnitOfWork uow, int modelId, DateTimeOffset now, CancellationToken ct)
    {
        var state = await uow.SyncStates.GetOrCreateAsync(modelId, ct).ConfigureAwait(false);

        state.ImagesCheckedAt = now;
        state.UpdatedAt = now;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
