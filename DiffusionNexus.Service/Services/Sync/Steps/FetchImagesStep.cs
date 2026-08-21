using System.Text.Json;
using DiffusionNexus.DataAccess.UnitOfWork;
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

        var candidates = await uow.SyncStates.SelectImageCandidatesAsync(scope, ct).ConfigureAwait(false);

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

        try
        {
            var answered = 0;
            var added = 0;

            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();

                var imagesAdded = await _civitai
                    .ApplyImagesAsync(uow, candidate.ModelId, candidate.VersionId, candidate.CivitaiVersionId, apiKey, ct)
                    .ConfigureAwait(false);

                if (imagesAdded is null)
                {
                    _logger?.Warn(LogCategory.Network, LogSource,
                        $"{item.Name}: Civitai returned no version for id {candidate.CivitaiVersionId}; marked images as checked");
                    continue;
                }

                answered++;
                added += imagesAdded.Value;
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
            await StampAsync(uow, item.ModelId, ct).ConfigureAwait(false);

            // Nothing on Civitai answered at all: stamped (final), but not a success either.
            if (answered == 0) return SyncItemResult.Skip;

            _logger?.Debug(LogCategory.Network, LogSource,
                $"Images for '{item.Name}': {added} added across {answered} version(s)");
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unstamped: a cancelled item is work not done, not work that failed.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            // No stamp for the whole model — the versions that did land keep their images (they
            // were saved as they went), and the next run re-asks the group. Re-asking a version
            // that already has images is free: the repository no longer selects it.
            uow.ClearChangeTracker();

            _logger?.Warn(LogCategory.Network, LogSource, $"Image fetch failed for '{item.Name}': {ex.Message}");
            _logger?.Debug(LogCategory.Network, LogSource, $"Image fetch failure detail for '{item.Name}'", ex.ToString());
            return SyncItemResult.Failure(ex.Message);
        }
    }

    private static async Task StampAsync(IUnitOfWork uow, int modelId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var state = await uow.SyncStates.GetOrCreateAsync(modelId, ct).ConfigureAwait(false);

        state.ImagesCheckedAt = now;
        state.UpdatedAt = now;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
