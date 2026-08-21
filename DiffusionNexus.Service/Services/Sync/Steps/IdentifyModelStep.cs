using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync.Steps;

/// <summary>
/// Step 1 — establishes what a local file actually <i>is</i>: stored/computed SHA256 →
/// Civitai hash lookup → local sidecar fallback. Replaces the ViewModel's Phase 1 / 1b and
/// its per-tile metadata copy (#521 WP2).
/// </summary>
/// <remarks>
/// Every path stamps <c>ModelSyncState</c>, which is the whole point of the overhaul: a run that
/// found nothing must be distinguishable from a run that never looked, or the next run repeats
/// the same 2 500 fruitless requests. Successful outcomes (<see cref="SyncOutcome.Matched"/>,
/// <see cref="SyncOutcome.Sidecar"/>, <see cref="SyncOutcome.NotIdentified"/>) reset the attempt
/// counter; only <see cref="SyncOutcome.Error"/> increments it, which is what bounds retries.
/// </remarks>
public sealed class IdentifyModelStep : ISyncStep
{
    private const string LogSource = "LibrarySync";
    private const int MaxErrorLength = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly ICivitaiClient _client;
    private readonly CivitaiMetadataApplier _civitai;
    private readonly SidecarMetadataApplier _sidecar;
    private readonly IUnifiedLogger? _logger;

    public IdentifyModelStep(
        IServiceScopeFactory scopes,
        ICivitaiClient client,
        CivitaiMetadataApplier civitai,
        SidecarMetadataApplier sidecar,
        IUnifiedLogger? logger = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _civitai = civitai ?? throw new ArgumentNullException(nameof(civitai));
        _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
        _logger = logger;
    }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.IdentifyModel;

    /// <inheritdoc />
    public string Description => "Identify models on Civitai";

    /// <summary>Hash (large files) + one API call + the 1.5 s pacing the client applies.</summary>
    public TimeSpan EstimatedPerItem => TimeSpan.FromSeconds(3);

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

        var candidates = await uow.SyncStates.SelectIdentifyCandidatesAsync(scope, enabledRoots, ct).ConfigureAwait(false);

        var items = new List<SyncItem>(candidates.Count);
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            // A file that is not on disk cannot be hashed, and no network call can substitute for
            // that — leave it to the verify/discover steps rather than burning an attempt on it.
            if (!File.Exists(candidate.LocalPath)) continue;

            if (IsDue(candidate, options, now)) items.Add(new SyncItem(candidate.ModelId, candidate.Name, candidate));
        }

        _logger?.Debug(LogCategory.General, LogSource,
            $"Identify: {items.Count} of {candidates.Count} candidate(s) due");
        return items;
    }

    private static bool IsDue(IdentifyCandidate candidate, SyncOptions options, DateTimeOffset now)
    {
        if (options.Policy.IsIdentifyDue(candidate.Outcome, candidate.CheckedAt, candidate.Attempts, now, options.ForceIdentify))
            return true;

        // A sidecar that appeared or changed since the last look is new evidence, so it beats the
        // retry window: the user just dropped a .civitai.info next to the file and expects it read.
        if (candidate.Outcome is not (SyncOutcome.Sidecar or SyncOutcome.NotIdentified)) return false;

        var signature = SidecarMetadataApplier.Find(candidate.LocalPath).Signature;
        return !string.Equals(signature, candidate.SidecarSignature ?? string.Empty, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
    {
        var candidate = item.Payload as IdentifyCandidate
            ?? throw new ArgumentException($"Payload must be an {nameof(IdentifyCandidate)}.", nameof(item));

        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var now = DateTimeOffset.UtcNow;

        // Before the hash and before the request: the model may have been deleted in the UI between
        // selection and now, and every path below this point stamps ModelSyncState — including the
        // 404/sidecar branch, whose GetOrCreateAsync would Add a row with a dangling FK and blow up
        // in SaveChanges with a DbUpdateException that no catch filter here covers. Hashing a
        // multi-gigabyte file for a model that no longer exists is wasted work on top of that.
        if (await uow.Models.GetByIdAsync(candidate.ModelId, ct).ConfigureAwait(false) is null)
        {
            _logger?.Debug(LogCategory.General, LogSource,
                $"Skipped '{candidate.Name}': model {candidate.ModelId} was deleted before the run reached it");
            return SyncItemResult.Skip;
        }

        try
        {
            var hash = await ResolveHashAsync(uow, candidate, ct).ConfigureAwait(false);

            var version = await _client.GetModelVersionByHashAsync(hash, apiKey, ct).ConfigureAwait(false);
            if (version is not null)
            {
                var applied = await _civitai.ApplyAsync(uow, candidate.ModelId, candidate.FileId, version, apiKey, ct).ConfigureAwait(false);
                return await RecordMatchAsync(uow, candidate, version, applied, now, ct).ConfigureAwait(false);
            }

            // 404 — not on Civitai. Fall back to whatever the user has next to the file.
            var sidecar = await _sidecar.ApplyAsync(uow, candidate.ModelId, candidate.LocalPath, ct).ConfigureAwait(false);
            var outcome = sidecar.Applied ? SyncOutcome.Sidecar : SyncOutcome.NotIdentified;
            await StampAsync(uow, candidate.ModelId, outcome, now, sidecar.Signature, error: null, ct).ConfigureAwait(false);

            _logger?.Debug(LogCategory.Network, LogSource,
                $"'{candidate.Name}' not on Civitai → {outcome}", sidecar.SidecarPath);
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unstamped: a cancelled item is work not done, not work that failed.
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            // TaskCanceledException with our token intact is HttpClient's own timeout, and a
            // JsonException is Civitai changing its response shape — both are faults of the run,
            // not of the code, so they are stamped rather than thrown.
            //
            // CivitaiMetadataApplier interleaves DB reads (IsCivitaiIdTakenAsync,
            // FindCreatorByUsernameAsync, GetAllTagsLookupAsync) *after* it starts mutating the
            // graph, so a mid-flight fault can leave half a Civitai apply tracked. Drop all of it
            // before stamping: the error path must persist the state row and nothing else. The
            // computed hash survives because ResolveHashAsync already committed it.
            uow.ClearChangeTracker();
            await StampAsync(uow, candidate.ModelId, SyncOutcome.Error, now, signature: null, ex.Message, ct).ConfigureAwait(false);

            _logger?.Warn(LogCategory.Network, LogSource, $"Identify failed for '{candidate.Name}': {ex.Message}");
            _logger?.Debug(LogCategory.Network, LogSource, $"Identify failure detail for '{candidate.Name}'", ex.ToString());
            return SyncItemResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Turns the outcome of a Civitai apply into a stamped result. A hash match is only a
    /// <see cref="SyncOutcome.Matched"/> when model-level data actually landed — see the two
    /// escape hatches below, both of which would otherwise become permanent dead ends, because
    /// <c>Matched</c> is terminal for the retry policy while a model with no <c>CivitaiId</c>
    /// is invisible to the tags and images steps.
    /// </summary>
    private async Task<SyncItemResult> RecordMatchAsync(
        IUnitOfWork uow, IdentifyCandidate candidate, CivitaiModelVersion version, bool applied,
        DateTimeOffset now, CancellationToken ct)
    {
        // The applier returns false for exactly one reason: the model row is gone (deleted between
        // select and execute). Stamping it would Add a state row whose PK/FK points at nothing.
        var dbModel = await uow.Models.GetByIdAsync(candidate.ModelId, ct).ConfigureAwait(false);
        if (dbModel is null)
        {
            _logger?.Debug(LogCategory.General, LogSource,
                $"Skipped '{candidate.Name}': model {candidate.ModelId} was deleted during the run");
            return SyncItemResult.Skip;
        }

        // A version whose ModelId is absent (<= 0) never reaches the applier's model-level branch,
        // so neither id was written. CivitaiModelPageId is checked alongside CivitaiId on purpose:
        // when a duplicate local copy already owns the CivitaiId the applier deliberately leaves it
        // null but still sets the page id, and that is a real match, not a dead end.
        var modelLevelDataMissing = dbModel.CivitaiId is null && dbModel.CivitaiModelPageId is null;

        if (!applied || modelLevelDataMissing)
        {
            var reason = $"Civitai match applied nothing (model {candidate.ModelId}, version {version.Id})";
            await StampAsync(uow, candidate.ModelId, SyncOutcome.Error, now, signature: null, reason, ct).ConfigureAwait(false);

            _logger?.Warn(LogCategory.Network, LogSource, $"Identify failed for '{candidate.Name}': {reason}");
            return SyncItemResult.Failure(reason);
        }

        await StampAsync(uow, candidate.ModelId, SyncOutcome.Matched, now, signature: null, error: null, ct).ConfigureAwait(false);

        _logger?.Debug(LogCategory.Network, LogSource, $"Identified '{candidate.Name}' on Civitai", $"versionId={version.Id}");
        return SyncItemResult.Success;
    }

    /// <summary>
    /// The candidate's stored hash when it is a usable SHA256, otherwise a freshly computed one —
    /// which is then written back onto the file row so the next run (and the duplicate finder) reuses it.
    /// </summary>
    private static async Task<string> ResolveHashAsync(IUnitOfWork uow, IdentifyCandidate candidate, CancellationToken ct)
    {
        if (FileHasher.IsSha256(candidate.Sha256)) return candidate.Sha256!.ToUpperInvariant();

        var hash = await FileHasher.Sha256UpperAsync(candidate.LocalPath, ct).ConfigureAwait(false);

        // Set before the Civitai applier runs: it fills hashes with `??=`, so the digest of the bytes
        // we actually have must already be in place to win over the response's (possibly stale) one.
        var file = await uow.ModelFiles.GetByIdAsync(candidate.FileId, ct).ConfigureAwait(false);
        if (file is not null)
        {
            file.HashSHA256 = hash;

            // Committed before the network call, on its own: hashing a multi-gigabyte file is the
            // expensive half of this step, and neither a cancellation nor a fault later in the item
            // may throw that work away. It also puts the digest of the bytes we actually hold in
            // place before CivitaiMetadataApplier's `??=` gets a chance to fill it from the response.
            await uow.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return hash;
    }

    private static async Task StampAsync(
        IUnitOfWork uow, int modelId, SyncOutcome outcome, DateTimeOffset now,
        string? signature, string? error, CancellationToken ct)
    {
        var state = await uow.SyncStates.GetOrCreateAsync(modelId, ct).ConfigureAwait(false);

        state.MetadataOutcome = outcome;
        state.MetadataCheckedAt = now;
        state.UpdatedAt = now;

        if (outcome == SyncOutcome.Error)
        {
            state.MetadataAttempts++;
            state.LastError = error is { Length: > MaxErrorLength } ? error[..MaxErrorLength] : error;
        }
        else
        {
            state.MetadataAttempts = 0;
            state.LastError = null;
        }

        // Only the sidecar paths know the signature; Matched leaves the stored one alone so a later
        // fallback still notices whether that sidecar has changed.
        if (signature is not null) state.SidecarSignature = signature;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
