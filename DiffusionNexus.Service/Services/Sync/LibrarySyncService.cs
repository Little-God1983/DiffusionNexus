using System.Diagnostics;
using DiffusionNexus.Civitai;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Sync.Steps;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.Service.Services.Sync;

/// <summary>
/// Runs the library metadata sync (#521 WP2): turns the registered <see cref="ISyncStep"/>s into a
/// plan the user can agree to, then executes that plan under a process-wide single-flight gate.
/// </summary>
/// <remarks>
/// The service owns no <c>DbContext</c> — it is a singleton, and every step creates its own scope —
/// so the only state it holds is the gate. Step order is registration order: tags and images can
/// only run against models the identify step has already matched.
/// </remarks>
public sealed class LibrarySyncService : ILibrarySyncService
{
    private const string LogSource = "LibrarySync";

    /// <summary>
    /// Discovery walks every enabled source folder no matter how the run was scoped, so the plan
    /// dialog must not imply otherwise ("this folder only" would be a lie for this one step).
    /// </summary>
    private const string DiscoverDescription = "Discover new files in all LoRA sources";

    private readonly IReadOnlyList<ISyncStep> _steps;
    private readonly SyncStateInitializer _initializer;
    private readonly IServiceScopeFactory _scopes;
    private readonly IUnifiedLogger? _logger;
    private readonly ICivitaiApiCache? _apiCache;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // volatile: the UI thread polls this while the run itself lives on a thread-pool thread.
    private volatile bool _isRunning;

    /// <remarks>
    /// There is no pacing parameter: every Civitai call the steps make goes through
    /// <c>CivitaiApiGateway</c> (registered as the steps' <c>ICivitaiClient</c>), which paces,
    /// waits out a shared 429 cooldown and caches on its own. Pacing used to be applied per
    /// <i>request</i> at hand-picked call sites inside the steps, because one item is not one
    /// request (R4) — the images step calls once per version, the identify step twice (hash
    /// lookup, model page) — but that left every surface added since to discover the rate limit
    /// on its own; the gateway is the fix, so this service no longer owns any pacing decision.
    /// </remarks>
    public LibrarySyncService(
        IEnumerable<ISyncStep> steps,
        SyncStateInitializer initializer,
        IServiceScopeFactory scopes,
        IUnifiedLogger? logger = null,
        ICivitaiApiCache? apiCache = null)
    {
        _steps = (steps ?? throw new ArgumentNullException(nameof(steps))).ToList();
        _initializer = initializer ?? throw new ArgumentNullException(nameof(initializer));
        _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));
        _logger = logger;
        _apiCache = apiCache;
    }

    /// <inheritdoc />
    public bool IsRunning => _isRunning;

    /// <inheritdoc />
    public async Task<SyncPlan> PlanAsync(SyncScope scope, SyncOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(options);

        // Before anything is selected: a model with no state row looks "never checked" to every
        // step, so the backfill has to have happened or the first run re-does the whole library.
        await _initializer.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var planSteps = new List<SyncPlanStep>();

        foreach (var step in _steps)
        {
            // A requested kind with no registered step is simply absent from the plan rather than
            // an error — the option set is a wish, not a contract.
            if (!options.Steps.Contains(step.Kind)) continue;

            ct.ThrowIfCancellationRequested();

            var items = await step.SelectAsync(scope, options, now, ct).ConfigureAwait(false);
            planSteps.Add(BuildPlanStep(step, items));
        }

        var plan = new SyncPlan(scope, options, planSteps, now);

        _logger?.Info(LogCategory.Network, LogSource, $"Plan: {DescribePlan(plan)}");

        return plan;
    }

    /// <summary>
    /// Turns a step's selection into its planned row. Two kinds do not map one item to one unit of
    /// work: discovery does not know its size until it runs, and images calls Civitai once per
    /// <i>version</i> while grouping its items per model.
    /// </summary>
    private static SyncPlanStep BuildPlanStep(ISyncStep step, IReadOnlyList<SyncItem> items)
    {
        var perItem = step.EstimatedPerItem;

        return step.Kind switch
        {
            // Always 0: the pseudo-item exists only so the step has something to execute, and
            // showing "1" would read as "one new file found" before anything has been scanned.
            SyncStepKind.DiscoverFiles => new SyncPlanStep(step.Kind, 0, Scale(perItem, items.Count), DiscoverDescription),

            SyncStepKind.FetchImages => new SyncPlanStep(step.Kind, items.Count, Scale(perItem, CountVersions(items)), step.Description),

            _ => new SyncPlanStep(step.Kind, items.Count, Scale(perItem, items.Count), step.Description),
        };
    }

    /// <summary>Total versions across the items, which is the number of calls the images step makes.</summary>
    private static int CountVersions(IReadOnlyList<SyncItem> items)
        => items.Sum(i => i.Payload is IReadOnlyList<ImageCandidate> versions ? versions.Count : 1);

    private static TimeSpan Scale(TimeSpan perItem, int count) => TimeSpan.FromTicks(perItem.Ticks * count);

    /// <inheritdoc />
    public async Task<SyncReport> ExecuteAsync(SyncPlan plan, IProgress<LibrarySyncProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);

        // Wait(0), not WaitAsync: the caller must learn synchronously that a run is already going,
        // and a queued second run is never what the user meant by pressing Sync twice.
        //
        // Its own exception type, not a bare InvalidOperationException: a step's SelectAsync raises
        // that too (GetRequiredService, Single() over nothing), and a caller cannot tell "not now"
        // from "your DI graph is broken" by type. Still derives from it, so old callers are fine.
        if (!_gate.Wait(0)) throw new SyncAlreadyRunningException();

        _isRunning = true;
        var stopwatch = Stopwatch.StartNew();

        var tally = new RunTally();
        var cancelled = false;
        string? abortReason = null;

        try
        {
            try
            {
                // "Force" means the user has told us the local answer is wrong. A cached response
                // is a local answer, so it goes too — otherwise a forced re-sync would replay the
                // very page that produced the state they are trying to correct. This also covers
                // the LoRA Viewer's per-tile "Download Metadata", which forces identify and
                // thumbnails for one model — see InvalidateForcedScopeAsync for why that path drops
                // only that model's cache entries rather than the whole process-wide cache.
                //
                // Inside the INNER try, not the outer one it used to sit in: InvalidateForcedScopeAsync
                // now does awaited DB I/O of its own (a GetByIdWithIncludesAsync per targeted model,
                // honouring ct) to find the versions and file hashes to invalidate alongside each
                // model, so it can raise a DatabaseOperationException or an OperationCanceledException
                // the same as any step's SelectAsync can. Only the inner try turns that into
                // abortReason/cancelled on the report instead of escaping ExecuteAsync outright —
                // the outer try exists solely to guarantee the finally below always runs.
                var options = plan.Options;
                if (options.ForceIdentify || options.ForceTags || options.ForceImages || options.ForceThumbnails)
                {
                    await InvalidateForcedScopeAsync(plan.Scope, ct).ConfigureAwait(false);
                }

                var apiKey = await ResolveApiKeyAsync(ct).ConfigureAwait(false);

                foreach (var planStep in plan.Steps)
                {
                    var step = _steps.FirstOrDefault(s => s.Kind == planStep.Kind);
                    if (step is null) continue;

                    ct.ThrowIfCancellationRequested();

                    await RunStepAsync(step, planStep, plan, apiKey, progress, tally, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // A cancelled run still reports what it managed to do — the stamps are already
                // committed, so pretending nothing happened would only cause it to be redone.
                //
                // Filtered, like its item-level counterpart in ExecuteItemAsync: an OCE arriving
                // when nobody cancelled — an HttpClient timeout, a linked token — is a step
                // blowing up in a cancellation costume, and it falls through to the abort catch
                // below instead of posing as "the user cancelled" (#535's door, cancellation-shaped).
                cancelled = true;
                _logger?.Info(LogCategory.Network, LogSource, "Library sync cancelled by the user");
            }
            catch (Exception ex)
            {
                // R5, one level up (#535). ExecuteItemAsync already counts bugs inside items; what
                // lands here escaped OUTSIDE the item loop — a step's SelectAsync, or the API-key
                // read. The rule is the same: by now earlier steps' items are committed (one scope
                // each), so letting this throw destroy the tally would report a partially-synced
                // library as "nothing happened". The run aborts — the failing step and everything
                // after it never runs — but what it did is returned, with the reason on the report
                // for the caller to state out loud.
                abortReason = $"Unexpected {ex.GetType().Name}: {ex.Message}";
                _logger?.Error(LogCategory.Network, LogSource,
                    "Library sync aborted: an exception escaped outside the item loop", ex);
            }

            stopwatch.Stop();

            var syncReport = new SyncReport(
                plan, tally.Steps, tally.Failures, cancelled, stopwatch.Elapsed, tally.Discovered,
                tally.UnexpectedFailures, tally.FirstUnexpectedError, abortReason, tally.Repointed);
            _logger?.Info(LogCategory.Network, LogSource, $"Sync finished: {syncReport.Summary} in {Describe(stopwatch.Elapsed)}");

            return syncReport;
        }
        finally
        {
            _isRunning = false;
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the cached Civitai responses a forced run is about to make stale — scoped to the
    /// models the run actually targets, and everything the identify step could have cached an
    /// answer under for them, when the plan can name them; a full <c>Clear()</c> only for a
    /// genuinely library- or folder-wide forced re-sync.
    /// </summary>
    /// <remarks>
    /// A run scoped to specific models (<see cref="SyncScopeKind.Models"/>) is the LoRA Viewer's
    /// per-tile "Download Metadata" button — one model, not the library. Calling <c>Clear()</c>
    /// for it used to empty the whole process-wide cache and bump its generation, which stamps
    /// every fetch already in flight for every OTHER surface (the browser's current page, the
    /// detail panel, the update sweep) as stale and discards its result — so working down a list
    /// of tiles re-triggered that N times, exactly the repeat-request pattern the cache exists to
    /// absorb. <see cref="ICivitaiApiCache.InvalidateModel"/> is scoped to one Civitai model id and
    /// leaves everything else alone.
    /// <para>
    /// Invalidating the model page alone under-reaches: a forced identify (<c>ForceIdentify</c>)
    /// re-asks Civitai by file hash first (<c>GetModelVersionByHashAsync</c>, cached under
    /// <c>hash:{HASH}</c> for 60 minutes — the longest TTL of any entry) before it ever reaches the
    /// model page, so a stale hash answer alone can make a forced press look like it did nothing
    /// for up to an hour. Every version and file hash the targeted models own is invalidated too,
    /// via <see cref="ICivitaiApiCache.InvalidateVersion"/> / <see cref="ICivitaiApiCache.InvalidateHash"/>,
    /// so a model-scoped forced run genuinely cannot be answered from an entry that predates it.
    /// </para>
    /// <para>
    /// <c>CivitaiModelPageId</c> is preferred over <c>CivitaiId</c> for the model-page key: it is
    /// set on every local row that shares a Civitai page, including duplicates, whereas
    /// <c>CivitaiId</c> is unique and only the "owner" row of a page carries it. A row falls back to
    /// <c>CivitaiId</c> only when <c>CivitaiModelPageId</c> is null — a row written before the
    /// <c>AddCivitaiModelPageId</c> migration and never re-synced since — which matches the source
    /// <c>SyncStateRepository.SelectTagCandidatesAsync</c> and <c>SelectImageCandidatesAsync</c>
    /// already use for the same id (from <c>m.CivitaiId</c>): without the fallback, such a row
    /// would be invalidated under neither field.
    /// </para>
    /// <para>
    /// A <see cref="SyncScopeKind.Library"/> or <see cref="SyncScopeKind.SourceFolder"/> run still
    /// clears everything: at that scope "force" means the checkbox in the plan dialog ("Models not
    /// found on Civitai"), which is deliberately library-wide, and the plan does not name a target
    /// set narrow enough to invalidate piecemeal.
    /// </para>
    /// </remarks>
    private async Task InvalidateForcedScopeAsync(SyncScope scope, CancellationToken ct)
    {
        if (_apiCache is null) return;

        if (scope.Kind != SyncScopeKind.Models || scope.ModelIds is not { Count: > 0 } modelIds)
        {
            _apiCache.Clear();
            return;
        }

        using var dbScope = _scopes.CreateScope();
        var uow = dbScope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        foreach (var modelId in modelIds)
        {
            // A model in the scope may have been deleted since the plan was built (same reasoning
            // as RunStepAsync re-selecting rather than trusting the plan's snapshot) — answers null
            // rather than throwing, and there is simply nothing left to invalidate. Loaded with
            // includes (not GetByIdAsync) because the versions and file hashes below live on the
            // navigation graph, not the row itself.
            var model = await uow.Models.GetByIdWithIncludesAsync(modelId, ct).ConfigureAwait(false);
            if (model is null) continue;

            if ((model.CivitaiModelPageId ?? model.CivitaiId) is { } civitaiModelId)
            {
                _apiCache.InvalidateModel(civitaiModelId);
            }

            foreach (var version in model.Versions)
            {
                if (version.CivitaiId is { } civitaiVersionId)
                {
                    _apiCache.InvalidateVersion(civitaiVersionId);
                }

                foreach (var file in version.Files)
                {
                    if (!string.IsNullOrWhiteSpace(file.HashSHA256))
                    {
                        _apiCache.InvalidateHash(file.HashSHA256);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Runs one step to completion, recording its counts into <paramref name="tally"/>. Items are
    /// re-selected here rather than taken from the plan: the plan dialog may have been open for
    /// minutes, and acting on ids that old means touching models the user has since deleted or
    /// already fixed by hand.
    /// </summary>
    private async Task RunStepAsync(
        ISyncStep step,
        SyncPlanStep planStep,
        SyncPlan plan,
        string? apiKey,
        IProgress<LibrarySyncProgress>? progress,
        RunTally tally,
        CancellationToken ct)
    {
        var items = await step.SelectAsync(plan.Scope, plan.Options, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        _logger?.Info(LogCategory.Network, LogSource,
            $"{StepLabel(step.Kind)}: starting {items.Count} item(s) (planned {planStep.Count})");

        var processed = 0;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        var tallyLock = new object();

        void Record(ItemOutcome outcome, SyncItem item)
        {
            var result = outcome.Result;
            processed++;
            if (result.Succeeded) succeeded++;
            else if (result.Skipped) skipped++;
            else
            {
                failed++;
                tally.Failures.Add(new SyncFailure(step.Kind, item.ModelId, item.Name, result.FailureReason ?? "Unknown error"));

                if (outcome.Unexpected)
                {
                    tally.UnexpectedFailures++;
                    tally.FirstUnexpectedError ??= result.FailureReason;
                }
            }
        }

        var concurrency = Math.Clamp(plan.Options.ThumbnailConcurrency, 1, 8);

        try
        {
            if (step.Kind == SyncStepKind.Thumbnails && concurrency > 1 && items.Count > 1)
            {
                // CDN fetches only — deliberately unpaced (see ThumbnailsStep remarks), and every
                // step owns a fresh scope per ExecuteOneAsync, so items are independent. Only the
                // tally and the progress counter are shared, and both are synchronized here.
                var started = 0;
                var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct };

                await Parallel.ForEachAsync(items, parallelOptions, async (item, itemCt) =>
                {
                    // "Items started", not a stable position: under parallelism the status line
                    // shows churn, and a monotonic counter is the honest number to put in [i/n].
                    var index = Interlocked.Increment(ref started);
                    progress?.Report(new LibrarySyncProgress(step.Kind, index, items.Count, item.Name));

                    var outcome = await ExecuteItemAsync(step, item, apiKey, itemCt).ConfigureAwait(false);

                    lock (tallyLock) Record(outcome, item);
                }).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < items.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var item = items[i];
                    progress?.Report(new LibrarySyncProgress(step.Kind, i + 1, items.Count, item.Name));

                    var outcome = await ExecuteItemAsync(step, item, apiKey, ct).ConfigureAwait(false);
                    Record(outcome, item);
                }
            }
        }
        finally
        {
            // In the finally, not after the loop: a cancelled step still did work, and its counts
            // are the only record that those items must not be redone.
            tally.Steps.Add(new SyncStepReport(step.Kind, planStep.Count, processed, succeeded, skipped, failed));
            if (step is DiscoverFilesStep discoverStep)
            {
                tally.Discovered = discoverStep.DiscoveredCount;
                tally.Repointed = discoverStep.RepointedCount;
            }

            _logger?.Info(LogCategory.Network, LogSource,
                $"{StepLabel(step.Kind)}: {succeeded} succeeded · {skipped} skipped · {failed} failed of {processed} processed");
        }
    }

    /// <summary>Mutable running totals for one <see cref="ExecuteAsync"/>, so a cancelled step still reports.</summary>
    private sealed class RunTally
    {
        public List<SyncStepReport> Steps { get; } = [];
        public List<SyncFailure> Failures { get; } = [];
        public int Discovered { get; set; }

        /// <summary>Rows the scan re-pointed at moved files — changed, not added (#537).</summary>
        public int Repointed { get; set; }

        /// <summary>Items that failed with an exception no step claimed — bugs, counted rather than fatal (R5).</summary>
        public int UnexpectedFailures { get; set; }

        /// <summary>The first such message, for the status line.</summary>
        public string? FirstUnexpectedError { get; set; }
    }

    /// <summary>One item's result, plus whether it got there via an exception nobody expected.</summary>
    private readonly record struct ItemOutcome(SyncItemResult Result, bool Unexpected);

    /// <summary>
    /// Executes one item. Steps return failure results for the faults they expect (network, disk,
    /// a changed response shape); anything that escapes is a bug.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That bug is now counted rather than thrown (R5). Rethrowing meant one
    /// <c>NullReferenceException</c> on model 812 destroyed the tally of the 811 models already
    /// synced — the stamps were committed, but nothing recorded that they had been — and the user
    /// saw the raw exception message where the report should have been. The item is failed, the
    /// exception is logged at Error <i>with</i> the exception object, and
    /// <see cref="SyncReport.UnexpectedFailures"/> carries it to the UI so it is stated out loud
    /// rather than left in a log nobody opens.
    /// </para>
    /// <para>
    /// The one exception that still escapes is a real cancellation: an
    /// <see cref="OperationCanceledException"/> raised while <paramref name="ct"/> is cancelled.
    /// A <see cref="TaskCanceledException"/> carrying somebody else's token is not the user
    /// pressing Cancel — it is HttpClient's own timeout, or a bug — and is an ordinary failure.
    /// </para>
    /// <para>
    /// No change tracker is reset here: every step owns its own scope inside
    /// <c>ExecuteOneAsync</c> and disposes it on the way out, so there is no unit of work at this
    /// level to clear. A step that keeps a context alive across items would have to clear it
    /// itself, which is what the item-fault handlers inside the steps already do.
    /// </para>
    /// </remarks>
    private async Task<ItemOutcome> ExecuteItemAsync(ISyncStep step, SyncItem item, string? apiKey, CancellationToken ct)
    {
        try
        {
            return new ItemOutcome(await step.ExecuteOneAsync(item, apiKey, ct).ConfigureAwait(false), Unexpected: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.Network, LogSource,
                $"{StepLabel(step.Kind)} threw an unexpected exception on '{item.Name}' (model {item.ModelId})", ex);

            return new ItemOutcome(SyncItemResult.Failure($"Unexpected {ex.GetType().Name}: {ex.Message}"), Unexpected: true);
        }
    }

    /// <summary>Reads the Civitai key once per run, from its own scope (the settings service is scoped/transient).</summary>
    private async Task<string?> ResolveApiKeyAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
        return await settings.GetCivitaiApiKeyAsync(ct).ConfigureAwait(false);
    }

    /// <summary>e.g. <c>Identify 3 · Tags 68 · Images 0 (~2 min)</c>.</summary>
    private static string DescribePlan(SyncPlan plan)
    {
        var parts = plan.Steps
            .Where(s => s.Kind != SyncStepKind.DiscoverFiles)
            .Select(s => $"{StepLabel(s.Kind)} {s.Count}")
            .ToList();

        if (plan.Steps.Any(s => s.Kind == SyncStepKind.DiscoverFiles)) parts.Insert(0, "Discover");
        if (parts.Count == 0) parts.Add("nothing to do");

        return $"{string.Join(" · ", parts)} (~{Describe(plan.EstimatedDuration)})";
    }

    /// <summary>Imperative, plan-voice labels ("Identify 3"), as opposed to the report's past tense.</summary>
    private static string StepLabel(SyncStepKind kind) => kind switch
    {
        SyncStepKind.DiscoverFiles => "Discover",
        SyncStepKind.IdentifyModel => "Identify",
        SyncStepKind.FetchTags => "Tags",
        SyncStepKind.FetchImages => "Images",
        SyncStepKind.Thumbnails => "Thumbnails",
        _ => kind.ToString(),
    };

    private static string Describe(TimeSpan duration) => duration switch
    {
        { TotalSeconds: < 60 } => $"{duration.TotalSeconds:0} s",
        { TotalMinutes: < 60 } => $"{duration.TotalMinutes:0} min",
        _ => $"{(int)duration.TotalHours} h {duration.Minutes} min",
    };
}
