using System.Text.Json;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Identity;
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
    private readonly ICivitaiRequestPacer _pacer;
    private readonly IUnifiedLogger? _logger;

    /// <param name="pacer">
    /// Paces the one call this step makes itself — the hash lookup. The model-page call that
    /// follows it lives inside <see cref="CivitaiMetadataApplier"/> and is paced there, so the two
    /// requests of one item are spaced from each other and not merely from the previous item (R4).
    /// </param>
    public IdentifyModelStep(
        IServiceScopeFactory scopes,
        ICivitaiClient client,
        CivitaiMetadataApplier civitai,
        SidecarMetadataApplier sidecar,
        IUnifiedLogger? logger = null,
        ICivitaiRequestPacer? pacer = null)
    {
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _civitai = civitai ?? throw new ArgumentNullException(nameof(civitai));
        _sidecar = sidecar ?? throw new ArgumentNullException(nameof(sidecar));
        _logger = logger;
        _pacer = pacer ?? NoCivitaiRequestPacer.Instance;
    }

    /// <inheritdoc />
    public SyncStepKind Kind => SyncStepKind.IdentifyModel;

    /// <inheritdoc />
    public string Description => "Identify models (Civitai, sidecar, file header, filename)";

    /// <summary>Hash (large files) + two API calls (hash lookup, model page) with 1.5 s pacing between them.</summary>
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

        // A forced run is the user asking for another look at a model they can see, so a stored
        // Civitai id must not hide it: that is what made the per-tile button answer "no metadata
        // found on Civitai" for every already-matched model. An explicit id scope is the same
        // request by a different route. Due-ness is still the retry policy's call — and under
        // force it always says yes.
        var includeMatched = options.ForceIdentify || scope.Kind == SyncScopeKind.Models;

        var candidates = await uow.SyncStates
            .SelectIdentifyCandidatesAsync(scope, enabledRoots, includeMatched, ct)
            .ConfigureAwait(false);

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

    /// <summary>
    /// Whether this candidate is due, either because the retry policy says so or because its
    /// sidecar changed since the last look.
    /// </summary>
    /// <remarks>
    /// A stored signature of <c>null</c> means "never recorded", which is <i>not</i> the same as
    /// "recorded as absent" (<c>""</c>) and is never a change trigger (R5). Rows derived from a
    /// legacy library all carry null, so comparing them against a real sidecar's signature made
    /// every legacy model that happens to own a <c>.civitai.info</c> due on the very first run —
    /// the same herd <see cref="SyncStateDeriver"/> stamps <c>now</c> to avoid. Such a model is
    /// picked up at its next regular check (30 days, or immediately via ForceIdentify — the
    /// per-tile button), and that check records the signature, after which changes are tracked
    /// normally.
    /// <para>
    /// The null gate also bounds the disk work of planning: <see cref="SidecarMetadataApplier.Find"/>
    /// is probed only for rows that have been checked before, not for the whole library.
    /// </para>
    /// </remarks>
    private static bool IsDue(IdentifyCandidate candidate, SyncOptions options, DateTimeOffset now)
    {
        if (options.Policy.IsIdentifyDue(candidate.Outcome, candidate.CheckedAt, candidate.Attempts, now, options.ForceIdentify))
            return true;

        // A sidecar that appeared or changed since the last look is new evidence, so it beats the
        // retry window: the user just dropped a .civitai.info next to the file and expects it read.
        if (candidate.Outcome is not (SyncOutcome.Sidecar or SyncOutcome.Header or SyncOutcome.Heuristic or SyncOutcome.NotIdentified)) return false;
        if (candidate.SidecarSignature is null) return false;

        var signature = SidecarMetadataApplier.Find(candidate.LocalPath).Signature;
        return !string.Equals(signature, candidate.SidecarSignature, StringComparison.Ordinal);
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
        // 404/sidecar branch, whose GetOrCreateAsync would Add a row with a dangling FK and be
        // rejected by SaveChanges. That rejection is survivable now (see the catch below), but a
        // failure row for a model the user deliberately deleted is noise, and hashing a
        // multi-gigabyte file for a model that no longer exists is wasted work on top of it.
        if (await uow.Models.GetByIdAsync(candidate.ModelId, ct).ConfigureAwait(false) is null)
        {
            _logger?.Debug(LogCategory.General, LogSource,
                $"Skipped '{candidate.Name}': model {candidate.ModelId} was deleted before the run reached it");
            return SyncItemResult.Skip;
        }

        try
        {
            var hash = await ResolveHashAsync(uow, candidate, ct).ConfigureAwait(false);

            await _pacer.WaitAsync(ct).ConfigureAwait(false);
            var version = await _client.GetModelVersionByHashAsync(hash, apiKey, ct).ConfigureAwait(false);
            if (version is not null)
            {
                var applied = await _civitai.ApplyAsync(uow, candidate.ModelId, candidate.FileId, version, apiKey, ct).ConfigureAwait(false);
                return await RecordMatchAsync(uow, candidate, version, applied, now, ct).ConfigureAwait(false);
            }

            // 404 — not on Civitai. Fall back to whatever the user has next to the file.
            var sidecar = await _sidecar.ApplyAsync(uow, candidate.ModelId, candidate.LocalPath, ct).ConfigureAwait(false);

            // The applier answers a cancellation with "nothing applied", which is the same answer it
            // gives for "there is no sidecar" — so without asking the token here, a model the user
            // cancelled out of would be stamped NotIdentified and then left alone for 30 days.
            ct.ThrowIfCancellationRequested();

            if (sidecar.Applied)
            {
                await StampAsync(uow, candidate.ModelId, SyncOutcome.Sidecar, now, sidecar.Signature, error: null, ct).ConfigureAwait(false);
                return SyncItemResult.Success;
            }

            // No Civitai match, no sidecar — read the file's own header, then guess from its name.
            var header = SafetensorsHeaderReader.TryRead(candidate.LocalPath);
            ct.ThrowIfCancellationRequested();
            var headerCheckedAt = header is not null ? now : (DateTimeOffset?)null;
            var label = header is not null ? BaseModelHeaderMap.Map(header) : null;
            var outcome = label is not null ? SyncOutcome.Header : SyncOutcome.NotIdentified;
            if (label is null)
            {
                label = FilenameBaseModelHeuristic.Guess(Path.GetFileNameWithoutExtension(candidate.LocalPath));
                if (label is not null) outcome = SyncOutcome.Heuristic;
            }
            if (label is not null)
            {
                var dbVersion = await uow.Models.GetVersionByIdAsync(candidate.VersionId, ct).ConfigureAwait(false);
                if (dbVersion is not null && BaseModelWriter.CanFill(dbVersion)) BaseModelWriter.Write(dbVersion, label);
            }
            await StampAsync(uow, candidate.ModelId, outcome, now, sidecar.Signature, error: null, ct, headerCheckedAt).ConfigureAwait(false);

            _logger?.Debug(LogCategory.Network, LogSource,
                $"'{candidate.Name}' not on Civitai → {outcome}" + (label is not null ? $" ({label})" : string.Empty), sidecar.SidecarPath);
            return SyncItemResult.Success;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Deliberately unstamped: a cancelled item is work not done, not work that failed.
            throw;
        }
        catch (Exception ex) when (SyncFaults.IsItemFault(ex))
        {
            // TaskCanceledException with our token intact is HttpClient's own timeout, a
            // JsonException is Civitai changing its response shape, and a rejected save is one
            // model's graph the database will not take (see SyncFaults) — all faults of one item,
            // not of the run, so they are stamped rather than thrown.
            //
            // CivitaiMetadataApplier interleaves DB reads (IsCivitaiIdTakenAsync,
            // FindCreatorByUsernameAsync, GetAllTagsLookupAsync) *after* it starts mutating the
            // graph, so a mid-flight fault can leave half a Civitai apply tracked. Drop all of it
            // before stamping: the error path must persist the state row and nothing else. This is
            // also what makes the stamp possible at all after a rejected save — the context would
            // otherwise still be holding the rows the database just refused. The computed hash
            // survives because ResolveHashAsync already committed it.
            uow.ClearChangeTracker();

            try
            {
                await StampAsync(uow, candidate.ModelId, SyncOutcome.Error, now, signature: null, ex.Message, ct).ConfigureAwait(false);
            }
            catch (Exception stampEx) when (stampEx is not OperationCanceledException && SyncFaults.IsItemFault(stampEx))
            {
                // The stamp is a database write, and the fault we are recording may be the reason
                // database writes are failing. Losing the record of the attempt is bad; taking the
                // whole run down with it is worse, so the item is reported as failed and the run
                // goes on — with both exceptions logged, because this is not normal.
                _logger?.Error(LogCategory.General, LogSource,
                    $"Could not record the identify failure for '{candidate.Name}': {stampEx.Message}", stampEx);
                return SyncItemResult.Failure(ex.Message);
            }

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
        string? signature, string? error, CancellationToken ct, DateTimeOffset? headerCheckedAt = null)
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

        // Only the miss-branch ever reads the header, and only when the file itself could be
        // opened as one — a non-safetensors file or a 404 that never reaches this stamp leaves the
        // stored value alone rather than overwriting it with null.
        if (headerCheckedAt is not null) state.HeaderCheckedAt = headerCheckedAt;

        await uow.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
