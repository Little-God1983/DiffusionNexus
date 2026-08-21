using System.Text.Json;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Step 2 — fetches the Civitai tag list for models identified on Civitai but holding no tags
/// locally (#521 WP2).
/// </summary>
/// <remarks>
/// This is the direct fix for the reported bug: 68 models that genuinely have no tags on Civitai
/// were re-fetched on every run forever, because the repository can only ask "does this model have
/// tags?" and the answer stayed "no" no matter how often we looked. The cure is to record that we
/// looked — <c>TagsCheckedAt</c> — which is why every non-throwing outcome stamps, including an
/// empty tag list and a model Civitai no longer has. Only a transient fault leaves no stamp, so
/// the item returns on the next run.
/// </remarks>
public sealed class FetchTagsStep : ISyncStep
{
    private const string LogSource = "LibrarySync";

    private readonly IServiceScopeFactory _scopes;
    private readonly CivitaiMetadataApplier _civitai;
    private readonly IUnifiedLogger? _logger;

    public FetchTagsStep(IServiceScopeFactory scopes, CivitaiMetadataApplier civitai, IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _civitai = civitai ?? throw new ArgumentNullException(nameof(civitai));
        _logger = logger;
    }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.FetchTags;

    /// <inheritdoc />
    public string Description => "Fetch tags";

    /// <summary>One model-page call plus the client's pacing.</summary>
    public TimeSpan EstimatedPerItem => TimeSpan.FromSeconds(1.6);

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
    {
        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var candidates = await uow.SyncStates.SelectTagCandidatesAsync(scope, ct).ConfigureAwait(false);

        var items = new List<SyncItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // Asked once, answered once: without ForceTags a stamped model is never asked again,
            // however empty the answer was.
            if (options.Policy.IsFetchDue(candidate.TagsCheckedAt, options.ForceTags))
                items.Add(new SyncItem(candidate.ModelId, candidate.Name, candidate));
        }

        _logger?.Debug(LogCategory.General, LogSource, $"Tags: {items.Count} of {candidates.Count} candidate(s) due");
        return items;
    }

    /// <inheritdoc />
    public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
    {
        var candidate = item.Payload as TagCandidate
            ?? throw new ArgumentException($"Payload must be a {nameof(TagCandidate)}.", nameof(item));

        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = DateTimeOffset.UtcNow;

        try
        {
            var tagCount = await _civitai
                .ApplyTagsAsync(uow, candidate.ModelId, candidate.CivitaiModelId, apiKey, ct)
                .ConfigureAwait(false);

            // The model may have been deleted in the UI while the run was in flight; stamping it
            // would Add a state row whose PK/FK points at nothing. FindAsync resolves against the
            // identity map, so on the normal path the applier's own load already paid for this.
            if (await uow.Models.GetByIdAsync(candidate.ModelId, ct).ConfigureAwait(false) is null)
            {
                _logger?.Debug(LogCategory.General, LogSource,
                    $"Skipped '{candidate.Name}': model {candidate.ModelId} was deleted during the run");
                return SyncItemResult.Skip;
            }

            await StampAsync(uow, candidate.ModelId, now, ct).ConfigureAwait(false);

            if (tagCount is null)
            {
                // Deliberately still stamped: a model that is gone from Civitai will be gone on the
                // next run too, and re-asking every run is the bug this step exists to fix.
                _logger?.Warn(LogCategory.Network, LogSource,
                    $"{candidate.Name}: Civitai returned no model for id {candidate.CivitaiModelId}; marked tags as checked");
                return SyncItemResult.Skip;
            }

            _logger?.Debug(LogCategory.Network, LogSource,
                $"Tags for '{candidate.Name}': {tagCount} tag(s)", $"civitaiModelId={candidate.CivitaiModelId}");
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unstamped: a cancelled item is work not done, not work that failed.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            // No stamp: a transient fault is not an answer about tags, so the item comes back on
            // the next run — bounded by the user, not by an attempt counter, because a tag fetch is
            // one cheap call. Discard the half-written graph first, as the applier interleaves DB
            // reads with mutations and may have left tag edits tracked.
            uow.ClearChangeTracker();

            _logger?.Warn(LogCategory.Network, LogSource, $"Tag fetch failed for '{candidate.Name}': {ex.Message}");
            _logger?.Debug(LogCategory.Network, LogSource, $"Tag fetch failure detail for '{candidate.Name}'", ex.ToString());
            return SyncItemResult.Failure(ex.Message);
        }
    }

    private static async Task StampAsync(IUnitOfWork uow, int modelId, DateTimeOffset now, CancellationToken ct)
    {
        var state = await uow.SyncStates.GetOrCreateAsync(modelId, ct).ConfigureAwait(false);

        state.TagsCheckedAt = now;
        state.UpdatedAt = now;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
