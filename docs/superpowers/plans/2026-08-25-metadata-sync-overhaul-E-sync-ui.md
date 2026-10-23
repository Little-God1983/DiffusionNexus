# Metadata Sync Overhaul — Plan E: Sync Plan Dialog, Report, Settings (WP6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The user sees a truthful plan (counts + estimate + per-step checkboxes + Force options) *before* a library sync runs, a faithful per-step report with an expandable failure list *after* it, and can tune retry windows and thumbnail download parallelism in Settings.

**Architecture:** Everything the dialog and report need already exists in the Domain records (`SyncPlanStep` carries Count/EstimatedDuration/Description; `SyncReport` carries `SyncStepReport` counts and per-item `SyncFailure`s). This plan adds no sync logic — it adds two dialogs (following the `DownloadLoraDialog` VM + `WithViewModel` pattern), four `AppSettings` columns feeding `SyncRetryPolicy`/`SyncOptions`, bounded parallelism for the Thumbnails step only (deferred here from Plan B), and rewires `LoraViewerViewModel.DownloadMissingMetadataAsync` to: discover → plan → dialog → execute → stamp → report.

**Tech Stack:** .NET 10, Avalonia 11, CommunityToolkit.Mvvm, EF Core 10 SQLite, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` — this is WP6 (§4.6, §5). Decision D4 (dialog, not auto-start) is accepted and binding.

**Branch:** `feature/metadata-sync-overhaul-e-sync-ui` (already created off the merged develop at `2d66c385`).

## Global Constraints

- **S1 — Additive schema only.** New nullable/defaulted columns on `AppSettings` only. No column drops, renames, type changes, or rewrites of existing columns.
- **S2 — Pre-migration backup is automatic.** `DatabaseRecoveryService` already VACUUMs a backup before applying any pending migration; do not add another mechanism, do not bypass it.
- **S7 — Nothing new runs at startup.** Startup stays: load tiles → discover new files → background file verify. The dialog, report, and settings reads change nothing about startup work.
- **D4 (spec §7):** "Download Metadata" opens the plan dialog. The expensive (network) run starts only after the user presses Start.
- **Spec §4.6 copy is binding, verbatim:** the all-zero plan shows **"Library is up to date — nothing to do"** with the last-run timestamp and no Start button. The report is per-step counts plus an expandable failure list with reasons — never a bare "N failed".
- **API pacing is untouchable.** The 1.5 s `ICivitaiRequestPacer` on Identify/Tags/Images stays exactly as is. Parallelism applies to the Thumbnails step ONLY (CDN, deliberately unpaced — see the remarks block at the top of `ThumbnailsStep.cs`).
- **Standing rule (log-feature-steps):** every new component logs its working steps to the Unified Console (`IUnifiedLogger`): dialog shown (with counts), the user's choice, report shown. Failures one `Warn` line each, stack traces only at `Debug` — the sync steps already comply; keep it that way.
- **Tests:** `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` — never solution-level. If `bin\Release` is file-locked (user runs the app from it), build/test `-c Debug` instead of killing their process.
- **Line endings:** never flip an existing file's line endings — verify the diff has no EOL-only churn (byte-level check via `git diff` — a full-file diff on a file you edited 3 lines of means you flipped it). New `.cs`/`.axaml` files: LF.
- **Branch discipline:** commit per task on this branch; never merge into develop (PR only, user merges).
- **Ctor convention:** new optional collaborators go at the END of constructor parameter lists with `= null` defaults, resolved via `App.Services?.GetService<T>()` fallback where the class already does that.

---

## File Structure

| File | Role |
|---|---|
| `DiffusionNexus.Domain/Entities/AppSettings.cs` | +4 columns (2 retry-day ints, thumbnail concurrency int, `LastLibrarySyncAt`) |
| `DiffusionNexus.Domain/Services/Sync/SyncRetryPolicy.cs` | +`FromDays` factory |
| `DiffusionNexus.Domain/Services/Sync/SyncOptions.cs` | +`ThumbnailConcurrency` |
| `DiffusionNexus.Domain/Services/Sync/SyncPlan.cs` | `HasWork` fixed to mean "any counted work" |
| `DiffusionNexus.Domain/Services/IAppSettingsService.cs` + `DiffusionNexus.Service/Services/AppSettingsService.cs` | +`UpdateLastLibrarySyncAtAsync` |
| `DiffusionNexus.DataAccess/Migrations/Core/*_AddSyncSettings.cs` (generated) + `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs` | migration + self-heal entries |
| `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs` | bounded-parallel branch for the Thumbnails step |
| `DiffusionNexus.UI/ViewModels/SyncPlanDialogViewModel.cs` (new) | plan dialog VM: rows, forces, live re-plan, up-to-date state |
| `DiffusionNexus.UI/Views/Dialogs/SyncPlanDialog.axaml(.cs)` (new) | plan dialog window |
| `DiffusionNexus.UI/ViewModels/SyncReportDialogViewModel.cs` (new) | report VM: step table + grouped failures |
| `DiffusionNexus.UI/Views/Dialogs/SyncReportDialog.axaml(.cs)` (new) | report window |
| `DiffusionNexus.UI/Services/IDialogService.cs` + `DialogService.cs` | +2 show methods |
| `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` | new flow: discover → plan → dialog → execute → stamp → report |
| `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs` | scroll gate takes the settings-derived policy |
| `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs` + `Views/SettingsView.axaml` | "Metadata Sync" settings block |
| `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.Editing.cs` | rider: `UploadThumbnailAsync` via `ThumbnailWriter` |
| `DiffusionNexus.UI/Doc/LoraViewer.md` + spec §5 | docs |

Task order: 1 (settings backbone) → 2 (parallelism) → 3 (plan dialog) → 4 (report dialog) → 5 (wire the flow) → 6 (settings UI) → 7 (rider + docs). Tasks 2, 3, 4 are mutually independent; 5 needs 1–4; 6 needs 1; 7 is last.

---

### Task 1: Settings backbone — columns, migration, policy factory

**Files:**
- Modify: `DiffusionNexus.Domain/Entities/AppSettings.cs`
- Modify: `DiffusionNexus.Domain/Services/Sync/SyncRetryPolicy.cs`
- Modify: `DiffusionNexus.Domain/Services/IAppSettingsService.cs`
- Modify: `DiffusionNexus.Service/Services/AppSettingsService.cs`
- Modify: `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs` (self-heal dictionary, ~line 284)
- Create (generated): `DiffusionNexus.DataAccess/Migrations/Core/<ts>_AddSyncSettings.cs` + Designer + snapshot update
- Test: `DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs` (extend)

**Interfaces:**
- Produces: `AppSettings.SyncNotIdentifiedRetryDays` (int, 30), `AppSettings.SyncErrorRetryDays` (int, 1), `AppSettings.SyncThumbnailConcurrency` (int, 4), `AppSettings.LastLibrarySyncAt` (DateTimeOffset?); `SyncRetryPolicy.FromDays(int notIdentifiedDays, int errorDays)`; `Task IAppSettingsService.UpdateLastLibrarySyncAtAsync(DateTimeOffset lastLibrarySyncAt, CancellationToken cancellationToken = default)`. Tasks 5 and 6 consume all of these.

- [ ] **Step 1: Write the failing test for the policy factory**

Append to `DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs` (match the file's existing test naming style):

```csharp
    [Fact]
    public void FromDays_BuildsWindowsFromSettingsValues()
    {
        var policy = SyncRetryPolicy.FromDays(notIdentifiedDays: 14, errorDays: 3);

        policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(14));
        policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(3));
        policy.MaxErrorAttempts.Should().Be(SyncRetryPolicy.Default.MaxErrorAttempts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void FromDays_FloorsAtOneDay_BecauseZeroWouldMeanAlwaysDue(int days)
    {
        var policy = SyncRetryPolicy.FromDays(days, days);

        policy.NotIdentifiedRetryAfter.Should().Be(TimeSpan.FromDays(1));
        policy.ErrorRetryAfter.Should().Be(TimeSpan.FromDays(1));
    }
```

- [ ] **Step 2: Run it to make sure it fails**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SyncRetryPolicyTests"`
Expected: compile error — `FromDays` does not exist.

- [ ] **Step 3: Implement `SyncRetryPolicy.FromDays`**

Add to `DiffusionNexus.Domain/Services/Sync/SyncRetryPolicy.cs`, after the `Default` property:

```csharp
    /// <summary>
    /// Builds a policy from the user's saved settings. Floors both windows at one day: zero would
    /// mean "always due", which turns every plan into a full re-run — when that is wanted it is a
    /// Force checkbox on the plan dialog, not a saved setting. <see cref="MaxErrorAttempts"/> stays
    /// at the default: it caps API hammering inside one run window and is not a user-facing knob.
    /// </summary>
    public static SyncRetryPolicy FromDays(int notIdentifiedDays, int errorDays) =>
        new(TimeSpan.FromDays(Math.Max(1, notIdentifiedDays)),
            TimeSpan.FromDays(Math.Max(1, errorDays)),
            Default.MaxErrorAttempts);
```

- [ ] **Step 4: Run the tests and make sure they pass**

Same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Add the four entity columns**

In `DiffusionNexus.Domain/Entities/AppSettings.cs`, add a new region after the existing LoRA-Helper/Viewer-related settings (place it next to `LoraUpdateCheckStalenessDays` so related knobs read together):

```csharp
    // --- Metadata Sync Settings (issue #521 Plan E) ---

    /// <summary>
    /// Days before the bulk sync re-checks a model whose identity attempt found nothing
    /// (NotIdentified/Sidecar/Header/Heuristic outcomes). Default 30 (spec §4.1 retry policy).
    /// </summary>
    public int SyncNotIdentifiedRetryDays { get; set; } = 30;

    /// <summary>Days before the bulk sync retries a model whose last attempt errored. Default 1.</summary>
    public int SyncErrorRetryDays { get; set; } = 1;

    /// <summary>
    /// How many thumbnail CDN downloads the bulk sync runs in parallel (1–8, default 4).
    /// Applies to the Thumbnails step only; API steps are paced, never parallel.
    /// </summary>
    public int SyncThumbnailConcurrency { get; set; } = 4;

    /// <summary>When the last user-started library-wide metadata sync completed uncancelled, if ever.</summary>
    public DateTimeOffset? LastLibrarySyncAt { get; set; }
```

No `AppSettingsConfiguration` change is needed — precedent: `LoraUpdateCheckStalenessDays` has no fluent config; its CLR initializer flowed into the migration as `defaultValue: 3` (see `Migrations/Core/20260510202951_AddLoraUpdateCheckStalenessDays.cs`).

- [ ] **Step 6: Add the timestamp setter to the settings service**

`DiffusionNexus.Domain/Services/IAppSettingsService.cs` — add next to `UpdateLastBackupAtAsync`:

```csharp
    /// <summary>
    /// Stamps when the last user-started library sync completed, without touching any other setting.
    /// </summary>
    Task UpdateLastLibrarySyncAtAsync(DateTimeOffset lastLibrarySyncAt, CancellationToken cancellationToken = default);
```

`DiffusionNexus.Service/Services/AppSettingsService.cs` — implement by copying the `UpdateLastBackupAtAsync` body shape exactly (read settings via `_unitOfWork.AppSettings.GetSettingsAsync`, set `LastLibrarySyncAt` and `UpdatedAt`, save). Read `UpdateLastBackupAtAsync` at line ~703 first and mirror it.

Check for other implementers: `Grep " : IAppSettingsService" across the repo` — if a test fake implements the interface (rather than Moq), add the method there too.

- [ ] **Step 7: Generate the migration and verify it is additive-only**

```powershell
cd e:\Repos\DiffusionNexus\DiffusionNexus.DataAccess
dotnet ef migrations add AddSyncSettings --context DiffusionNexusCoreDbContext --output-dir Migrations/Core
```

Open the generated `<ts>_AddSyncSettings.cs` and verify `Up` contains exactly four `AddColumn` calls on `AppSettings`: `SyncNotIdentifiedRetryDays` (INTEGER, not null, defaultValue 30), `SyncErrorRetryDays` (INTEGER, not null, defaultValue 1), `SyncThumbnailConcurrency` (INTEGER, not null, defaultValue 4), `LastLibrarySyncAt` (TEXT, nullable) — and NOTHING touching any other table. If anything else appears, the snapshot was stale: stop and report.

- [ ] **Step 8: Add the self-heal entries**

In `DiffusionNexus.DataAccess/Recovery/DatabaseRecoveryService.cs`, extend the AppSettings column-repair dictionary (the one at ~line 284 holding `MaxBackups`, `LastBackupAt`, `LoraUpdateCheckStalenessDays`, …) with:

```csharp
                { "SyncNotIdentifiedRetryDays", "ALTER TABLE AppSettings ADD COLUMN SyncNotIdentifiedRetryDays INTEGER NOT NULL DEFAULT 30" },
                { "SyncErrorRetryDays", "ALTER TABLE AppSettings ADD COLUMN SyncErrorRetryDays INTEGER NOT NULL DEFAULT 1" },
                { "SyncThumbnailConcurrency", "ALTER TABLE AppSettings ADD COLUMN SyncThumbnailConcurrency INTEGER NOT NULL DEFAULT 4" },
                { "LastLibrarySyncAt", "ALTER TABLE AppSettings ADD COLUMN LastLibrarySyncAt TEXT" },
```

- [ ] **Step 9: Full suite green, commit**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`
Expected: PASS (existing DataAccess migration/round-trip tests pick up the new columns automatically; if a settings round-trip test enumerates properties explicitly, extend it).

```bash
git add -A
git commit -m "feat(sync): sync retry/concurrency settings columns + last-library-sync stamp"
```

---

### Task 2: Bounded parallelism for the Thumbnails step

**Files:**
- Modify: `DiffusionNexus.Domain/Services/Sync/SyncOptions.cs`
- Modify: `DiffusionNexus.Service/Services/Sync/LibrarySyncService.cs` (`RunStepAsync`, ~line 179)
- Test: `DiffusionNexus.Tests/Sync/Service/LibrarySyncServiceTests.cs` (extend)

**Interfaces:**
- Consumes: nothing from Task 1 (independent).
- Produces: `SyncOptions.ThumbnailConcurrency` (int, default 4, trailing optional ctor param). Task 5 sets it from settings.

**Why this is safe:** `ThumbnailsStep.ExecuteOneAsync` creates a fresh `IServiceScope` + `IUnitOfWork` per item (read `ThumbnailsStep.cs:90-156` to confirm this is still true), the `IThumbnailProvider` is a typed-`HttpClient` singleton (thread-safe), and the step deliberately has no `ICivitaiRequestPacer` (its own remarks block says so). The `LibrarySyncService` remarks at ~line 276 state the invariant: "every step owns its own scope inside `ExecuteOneAsync`". Only the run tally and progress reporting are shared — those are what this task synchronizes.

- [ ] **Step 1: Add the option**

`SyncOptions.cs` — add a trailing parameter to the record (source-compatible with every existing construction site):

```csharp
public sealed record SyncOptions(
    IReadOnlySet<SyncStepKind> Steps,
    bool ForceIdentify = false,
    bool ForceTags = false,
    bool ForceImages = false,
    bool ForceThumbnails = false,
    SyncRetryPolicy? RetryPolicy = null,
    int ThumbnailConcurrency = 4)
```

Keep the existing `All` static and `Policy` member untouched. Add an XML doc line on the new parameter position (`/// <param>` style if the record uses it, else a `<remarks>` sentence): "Thumbnails-step download parallelism, clamped to 1–8 by the service. API steps ignore it."

- [ ] **Step 2: Write the failing concurrency tests**

Append to `DiffusionNexus.Tests/Sync/Service/LibrarySyncServiceTests.cs`. Read the file's existing fixture first (how it constructs `LibrarySyncService` with a step list and builds plans) and reuse that; the probe step is self-contained:

```csharp
    private sealed class ConcurrencyProbeStep : ISyncStep
    {
        private readonly IReadOnlyList<SyncItem> _items;
        private readonly bool _failOddItems;
        private int _inFlight;

        public int MaxObservedConcurrency;
        public int Executed;

        public ConcurrencyProbeStep(int itemCount, bool failOddItems = false)
        {
            _items = Enumerable.Range(1, itemCount)
                .Select(i => new SyncItem(i, $"model-{i}", i))
                .ToList();
            _failOddItems = failOddItems;
        }

        public SyncStepKind Kind => SyncStepKind.Thumbnails;
        public string Description => "probe";
        public TimeSpan EstimatedPerItem => TimeSpan.Zero;

        public Task<IReadOnlyList<SyncItem>> SelectAsync(SyncScope scope, SyncOptions options, DateTimeOffset now, CancellationToken ct)
            => Task.FromResult(_items);

        public async Task<SyncItemResult> ExecuteOneAsync(SyncItem item, string? apiKey, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            do
            {
                seen = MaxObservedConcurrency;
                if (now <= seen) break;
            } while (Interlocked.CompareExchange(ref MaxObservedConcurrency, now, seen) != seen);

            await Task.Delay(25, ct);

            Interlocked.Decrement(ref _inFlight);
            Interlocked.Increment(ref Executed);

            return _failOddItems && (int)item.Payload % 2 == 1
                ? SyncItemResult.Failure("probe-failure")
                : SyncItemResult.Success;
        }
    }

    [Fact]
    public async Task Thumbnails_RunInParallel_UpToTheConfiguredConcurrency()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 12);
        var service = /* build LibrarySyncService with [probe] exactly as the file's other tests do */;
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 4);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        var report = await service.ExecuteAsync(plan);

        probe.Executed.Should().Be(12);
        probe.MaxObservedConcurrency.Should().BeGreaterThan(1).And.BeLessThanOrEqualTo(4);
        report.Steps.Single().Succeeded.Should().Be(12);
    }

    [Fact]
    public async Task Thumbnails_ConcurrencyOne_StaysStrictlySequential()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 6);
        var service = /* same fixture */;
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 1);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        await service.ExecuteAsync(plan);

        probe.MaxObservedConcurrency.Should().Be(1);
    }

    [Fact]
    public async Task ParallelThumbnails_RecordEveryFailure_WithoutLosingAny()
    {
        var probe = new ConcurrencyProbeStep(itemCount: 10, failOddItems: true);
        var service = /* same fixture */;
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.Thumbnails }, ThumbnailConcurrency: 4);

        var plan = await service.PlanAsync(SyncScope.Library, options);
        var report = await service.ExecuteAsync(plan);

        var step = report.Steps.Single();
        step.Processed.Should().Be(10);
        step.Failed.Should().Be(5);
        report.Failures.Should().HaveCount(5);
        report.Failures.Select(f => f.Reason).Should().AllBe("probe-failure");
    }
```

The `/* build LibrarySyncService ... */` comments are for you, the implementer: replace them with the file's real construction helper — do not invent a new fixture. If the fixture's step registration is keyed by kind, register the probe as the Thumbnails step.

- [ ] **Step 3: Run to verify they fail**

Run: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~LibrarySyncServiceTests"`
Expected: the two parallel tests FAIL (`MaxObservedConcurrency` is 1 — today's loop is sequential); the sequential test passes.

- [ ] **Step 4: Implement the parallel branch in `RunStepAsync`**

In `LibrarySyncService.RunStepAsync` (read it fully first — the current sequential loop is at ~lines 198-224), restructure the body so both paths share one result-recording local function:

```csharp
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
            // (existing finally block — unchanged)
```

Notes that are part of the requirement, not suggestions:
- The sequential path must remain byte-for-byte the current behavior for all non-Thumbnails steps (the pacing tests depend on it).
- `ExecuteItemAsync` already swallows everything except genuine cancellation, so the only exception a parallel body can throw is `OperationCanceledException` — `Parallel.ForEachAsync` then cancels the remaining work and the existing `catch (OperationCanceledException)` in `ExecuteAsync` plus the `finally` tally handling produce a correct partial report. Do not add a second cancellation handler.
- The `finally` block and the `RunTally` class are untouched.

- [ ] **Step 5: Run the step tests, then the full suite**

Run the filter from Step 3 → all PASS. Then the full suite → PASS (watch `CivitaiRequestPacerTests` and the step tests for any accidental behavior change).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(sync): bounded parallelism for the thumbnails step (SyncOptions.ThumbnailConcurrency)"
```

---

### Task 3: SyncPlanDialog — ViewModel, window, DialogService method

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/SyncPlanDialogViewModel.cs`
- Create: `DiffusionNexus.UI/Views/Dialogs/SyncPlanDialog.axaml`
- Create: `DiffusionNexus.UI/Views/Dialogs/SyncPlanDialog.axaml.cs`
- Modify: `DiffusionNexus.UI/Services/IDialogService.cs`, `DiffusionNexus.UI/Services/DialogService.cs`
- Test: Create `DiffusionNexus.Tests/ViewModels/SyncPlanDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `SyncPlan`/`SyncPlanStep`/`SyncOptions`/`SyncStepKind`/`SyncReport.Label` from `DiffusionNexus.Domain.Services.Sync` (existing).
- Produces: `SyncPlanDialogResult(bool Confirmed, SyncOptions? Options)` with `static Cancelled()`; `SyncPlanDialogViewModel(SyncPlan initialPlan, SyncOptions baseOptions, Func<SyncOptions, Task<SyncPlan>> replanAsync, DateTimeOffset? lastLibrarySyncAt, int newFilesDiscovered, IUnifiedLogger? logger = null)`; `Task<SyncPlanDialogResult> IDialogService.ShowSyncPlanDialogAsync(SyncPlanDialogViewModel viewModel)`. Task 5 consumes all three.

Design rules (binding):
- The dialog shows **four rows** in fixed order — IdentifyModel, FetchTags, FetchImages, Thumbnails — never a DiscoverFiles row (discovery has already run by the time the dialog opens; Task 5). Row labels come from `SyncReport.Label`.
- A row with `Count == 0` is unchecked and disabled ("there is nothing to start"). A row going `0 → N` after a Force re-plan becomes checked; a row the user explicitly unchecked while it was enabled STAYS unchecked across re-plans.
- Toggling any Force checkbox re-plans immediately via the injected delegate (DB-only, sub-second) and updates the row counts. Re-plans always request **all four kinds** — selection filters at Start, not at plan time.
- All-zero plan (every row `Count == 0`): show verbatim **"Library is up to date — nothing to do"** plus "Last full sync: `yyyy-MM-dd HH:mm`" (or "Last full sync: never"), and the Start button is hidden. The Force expander stays visible — forcing from the up-to-date state is exactly its purpose, and a force that produces counts un-hides Start.
- The result's `SyncOptions` = `baseOptions` (which carries `RetryPolicy` and `ThumbnailConcurrency` from Task 5) with `Steps` = the checked kinds and the four Force flags as toggled.

- [ ] **Step 1: Write the failing VM tests**

`DiffusionNexus.Tests/ViewModels/SyncPlanDialogViewModelTests.cs` (new file, LF):

```csharp
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public class SyncPlanDialogViewModelTests
{
    private static readonly IReadOnlySet<SyncStepKind> FourKinds = new HashSet<SyncStepKind>
    {
        SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
    };

    private static SyncPlan PlanWith(int identify, int tags, int images, int thumbs, SyncOptions? options = null)
    {
        options ??= new SyncOptions(FourKinds);
        return new SyncPlan(SyncScope.Library, options, new[]
        {
            new SyncPlanStep(SyncStepKind.IdentifyModel, identify, TimeSpan.FromSeconds(3 * identify), "identify"),
            new SyncPlanStep(SyncStepKind.FetchTags, tags, TimeSpan.FromSeconds(1.6 * tags), "tags"),
            new SyncPlanStep(SyncStepKind.FetchImages, images, TimeSpan.FromSeconds(1.6 * images), "images"),
            new SyncPlanStep(SyncStepKind.Thumbnails, thumbs, TimeSpan.FromSeconds(0.4 * thumbs), "thumbs"),
        }, DateTimeOffset.UtcNow);
    }

    private static SyncPlanDialogViewModel Vm(
        SyncPlan plan,
        Func<SyncOptions, Task<SyncPlan>>? replan = null,
        DateTimeOffset? lastSync = null,
        int discovered = 0)
        => new(plan, new SyncOptions(FourKinds), replan ?? (_ => Task.FromResult(plan)), lastSync, discovered);

    [Fact]
    public void RowsWithWork_ArePreChecked_AndEmptyRowsAreDisabled()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 0, thumbs: 12));

        vm.Rows.Should().HaveCount(4);
        vm.Rows.Single(r => r.Kind == SyncStepKind.IdentifyModel).IsSelected.Should().BeTrue();
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchImages).IsSelected.Should().BeFalse();
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchImages).IsEnabled.Should().BeFalse();
        vm.IsUpToDate.Should().BeFalse();
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public void AllZeroPlan_IsUpToDate_WithNoStart_AndShowsTheLastRun()
    {
        var last = new DateTimeOffset(2026, 8, 25, 14, 3, 0, TimeSpan.Zero);
        var vm = Vm(PlanWith(0, 0, 0, 0), lastSync: last);

        vm.IsUpToDate.Should().BeTrue();
        vm.CanStart.Should().BeFalse();
        vm.UpToDateText.Should().Be("Library is up to date — nothing to do");
        vm.LastRunText.Should().Contain("Last full sync:").And.NotContain("never");
    }

    [Fact]
    public void NoRecordedRun_SaysNever()
    {
        var vm = Vm(PlanWith(0, 0, 0, 0), lastSync: null);
        vm.LastRunText.Should().Be("Last full sync: never");
    }

    [Fact]
    public async Task TogglingAForce_Replans_AndAppliesTheNewCounts()
    {
        SyncOptions? seen = null;
        Task<SyncPlan> Replan(SyncOptions o)
        {
            seen = o;
            return Task.FromResult(PlanWith(0, 0, 0, thumbs: 40, options: o));
        }

        var vm = Vm(PlanWith(0, 0, 0, 0), Replan);
        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        seen.Should().NotBeNull();
        seen!.ForceThumbnails.Should().BeTrue();
        seen.Steps.Should().BeEquivalentTo(FourKinds);
        vm.Rows.Single(r => r.Kind == SyncStepKind.Thumbnails).Count.Should().Be(40);
        vm.Rows.Single(r => r.Kind == SyncStepKind.Thumbnails).IsSelected.Should().BeTrue();
        vm.IsUpToDate.Should().BeFalse();
        vm.CanStart.Should().BeTrue();
    }

    [Fact]
    public async Task AUserUntick_SurvivesAReplan()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 0, thumbs: 12),
            o => Task.FromResult(PlanWith(3, 68, 0, 40, o)));

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;
        vm.ForceThumbnails = true;
        await vm.WhenReplanSettles();

        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected.Should().BeFalse();
    }

    [Fact]
    public void BuildResult_CarriesTheCheckedKindsAndForces()
    {
        var vm = Vm(PlanWith(identify: 3, tags: 68, images: 2, thumbs: 12));
        vm.Rows.Single(r => r.Kind == SyncStepKind.FetchTags).IsSelected = false;
        vm.ForceIdentify = true;

        var result = vm.BuildResult();

        result.Confirmed.Should().BeTrue();
        result.Options!.Steps.Should().BeEquivalentTo(new[]
        {
            SyncStepKind.IdentifyModel, SyncStepKind.FetchImages, SyncStepKind.Thumbnails,
        });
        result.Options.ForceIdentify.Should().BeTrue();
        result.Options.ForceTags.Should().BeFalse();
    }
}
```

Note the test seam `WhenReplanSettles()` — the VM exposes `internal Task WhenReplanSettles()` returning the in-flight re-plan task (or `Task.CompletedTask`), so tests never poll. `ForceThumbnails` etc. are the `[ObservableProperty]`-generated setters (property change triggers the re-plan).

- [ ] **Step 2: Run to verify they fail to compile** (types don't exist yet).

- [ ] **Step 3: Implement the ViewModel**

`DiffusionNexus.UI/ViewModels/SyncPlanDialogViewModel.cs` (new). Full shape — implement exactly this contract, filling in the obvious bodies:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>The plan dialog's outcome. A cancelled dialog carries no options.</summary>
public sealed record SyncPlanDialogResult(bool Confirmed, SyncOptions? Options)
{
    public static SyncPlanDialogResult Cancelled() => new(false, null);
}

/// <summary>One step row: what it would do, how many items, whether it runs.</summary>
public sealed partial class SyncPlanStepRowViewModel : ObservableObject
{
    public SyncPlanStepRowViewModel(SyncStepKind kind, string description) { ... }

    public SyncStepKind Kind { get; }
    public string Label => SyncReport.Label(Kind);
    public string Description { get; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private bool _isSelected;

    /// <summary>A row with nothing to do cannot be ticked.</summary>
    public bool IsEnabled => Count > 0;
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(IsEnabled));
    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
    internal Action? SelectionChanged { get; set; }
}

public sealed partial class SyncPlanDialogViewModel : ObservableObject
{
    internal const string UpToDateMessage = "Library is up to date — nothing to do";

    private readonly SyncOptions _baseOptions;
    private readonly Func<SyncOptions, Task<SyncPlan>> _replanAsync;
    private readonly IUnifiedLogger? _logger;
    private Task _replanTask = Task.CompletedTask;

    public SyncPlanDialogViewModel(
        SyncPlan initialPlan,
        SyncOptions baseOptions,
        Func<SyncOptions, Task<SyncPlan>> replanAsync,
        DateTimeOffset? lastLibrarySyncAt,
        int newFilesDiscovered,
        IUnifiedLogger? logger = null)
    {
        // Rows in fixed order; descriptions come from the plan's steps (fall back to the label).
        // Apply the initial plan, wire row.SelectionChanged -> RefreshDerived().
    }

    public IReadOnlyList<SyncPlanStepRowViewModel> Rows { get; }

    [ObservableProperty] private bool _forceIdentify;      // "Re-check models not found on Civitai"
    [ObservableProperty] private bool _forceTags;
    [ObservableProperty] private bool _forceImages;
    [ObservableProperty] private bool _forceThumbnails;
    [ObservableProperty] private bool _isReplanning;

    public bool IsUpToDate { get; private set; }           // all rows Count == 0
    public bool CanStart { get; private set; }             // any selected row with Count > 0, and not replanning
    public string UpToDateText => UpToDateMessage;
    public string LastRunText { get; }                     // "Last full sync: yyyy-MM-dd HH:mm" local time, or "Last full sync: never"
    public string DiscoveredText { get; }                  // "N new file(s) discovered" — "" when 0
    public bool HasDiscoveredFiles { get; }
    public string TotalEstimateText { get; private set; } = "";  // sum of SELECTED rows' plan durations, via FormatDuration

    partial void OnForceIdentifyChanged(bool value) => QueueReplan();
    partial void OnForceTagsChanged(bool value) => QueueReplan();
    partial void OnForceImagesChanged(bool value) => QueueReplan();
    partial void OnForceThumbnailsChanged(bool value) => QueueReplan();

    private void QueueReplan()
    {
        // Chain onto _replanTask so toggles serialize; set IsReplanning around the await;
        // log Info(LogCategory.Network, "CivitaiSync", "Plan dialog: re-planning with forces ...").
        // On the replan's plan: ApplyPlan(plan). Exceptions: log one Warn, keep the old counts.
    }

    internal Task WhenReplanSettles() => _replanTask;

    private void ApplyPlan(SyncPlan plan)
    {
        // Per row: Count/EstimateText ("~2 min" via FormatDuration of the step's EstimatedDuration).
        // Selection rule: a row that was DISABLED before (Count==0) and now has work becomes checked;
        // a row that was enabled keeps whatever the user chose. Then RefreshDerived().
    }

    private void RefreshDerived()
    {
        // IsUpToDate, CanStart, TotalEstimateText (+ OnPropertyChanged for each).
    }

    public SyncPlanDialogResult BuildResult()
    {
        var steps = Rows.Where(r => r.IsSelected && r.Count > 0).Select(r => r.Kind).ToHashSet();
        var options = _baseOptions with
        {
            Steps = steps,
            ForceIdentify = ForceIdentify,
            ForceTags = ForceTags,
            ForceImages = ForceImages,
            ForceThumbnails = ForceThumbnails,
        };
        return new SyncPlanDialogResult(true, options);
    }

    /// <summary>"~45 s" under 90 s, "~4 min" under 90 min, else "~1.5 h".</summary>
    internal static string FormatDuration(TimeSpan t) { ... }
}
```

(`SyncOptions.Steps` is an `IReadOnlySet<SyncStepKind>` positional record property, so `with { Steps = steps }` works.)

- [ ] **Step 4: Run the VM tests** — filter `SyncPlanDialogViewModelTests`. Expected: PASS.

- [ ] **Step 5: The window**

`SyncPlanDialog.axaml` — follow `DownloadLoraDialog.axaml`'s conventions (theme, spacing, `x:DataType="vm:SyncPlanDialogViewModel"`). Layout, top to bottom:
- Title bar text "Metadata Sync". `Width="560" SizeToContent="Height" CanResize="False" WindowStartupLocation="CenterOwner"`.
- Header block: `DiscoveredText` (visible via `HasDiscoveredFiles`); the up-to-date block (`IsVisible="{Binding IsUpToDate}"`): `UpToDateText` bold + `LastRunText` dimmed.
- Rows (`IsVisible="{Binding !IsUpToDate}"`): `ItemsControl ItemsSource="{Binding Rows}"`; each row a Grid: `CheckBox IsChecked="{Binding IsSelected}" IsEnabled="{Binding IsEnabled}"`, count (`TextBlock Text="{Binding Count}"` right-aligned, min-width 40), `Label` semibold + `Description` dimmed `FontSize="12"` `TextWrapping="Wrap"`, `EstimateText` dimmed right-aligned.
- `Expander Header="Force re-check…"` (collapsed by default, always visible): four CheckBoxes bound to the Force properties, contents: "Models not found on Civitai", "Tags (even when already checked)", "Image records (even when already checked)", "Thumbnails (re-fetch existing attempts)". A small "Re-planning…" `TextBlock IsVisible="{Binding IsReplanning}"`.
- Footer: `TotalEstimateText` left; right-aligned buttons **Start** (`Classes="accent"` if the app's dialogs use an accent class — copy whatever `DownloadLoraDialog`'s primary button does, `IsVisible="{Binding !IsUpToDate}" IsEnabled="{Binding CanStart}"`) and **Cancel**.

`SyncPlanDialog.axaml.cs` — the `DownloadLoraDialog` code-behind pattern verbatim (`InitializeComponent` via `AvaloniaXamlLoader.Load(this)`; `WithViewModel(SyncPlanDialogViewModel)` fluent; `public SyncPlanDialogResult? Result { get; private set; }`). Start click: `Result = _viewModel!.BuildResult(); Close();`. Cancel click and window close: leave `Result` null.

- [ ] **Step 6: DialogService**

`IDialogService.cs`:

```csharp
    /// <summary>Shows the metadata-sync plan dialog. Never null: closing the window is a cancel.</summary>
    Task<SyncPlanDialogResult> ShowSyncPlanDialogAsync(SyncPlanDialogViewModel viewModel);
```

`DialogService.cs` — copy the UI-thread-marshalling shape of `ShowSelectLoraVersionsToDeleteDialogAsync` (re-invoke on `Dispatcher.UIThread` when off-thread), then:

```csharp
        var dialog = new SyncPlanDialog().WithViewModel(viewModel);
        await dialog.ShowDialog(_window);
        return dialog.Result ?? SyncPlanDialogResult.Cancelled();
```

- [ ] **Step 7: Build the UI project, run the full suite, commit**

`dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release` → 0 warnings. Full suite → PASS.

```bash
git add -A
git commit -m "feat(sync): SyncPlanDialog — per-step counts, checkboxes, force re-plan, up-to-date state"
```

---

### Task 4: SyncReportDialog — faithful post-run report

**Files:**
- Create: `DiffusionNexus.UI/ViewModels/SyncReportDialogViewModel.cs`
- Create: `DiffusionNexus.UI/Views/Dialogs/SyncReportDialog.axaml` + `.axaml.cs`
- Modify: `DiffusionNexus.UI/Services/IDialogService.cs`, `DialogService.cs`
- Test: Create `DiffusionNexus.Tests/ViewModels/SyncReportDialogViewModelTests.cs`

**Interfaces:**
- Consumes: `SyncReport`/`SyncStepReport`/`SyncFailure` (existing).
- Produces: `SyncReportDialogViewModel(SyncReport report, int newFilesDiscovered)`; `Task IDialogService.ShowSyncReportDialogAsync(SyncReportDialogViewModel viewModel)`. Task 5 consumes both.

- [ ] **Step 1: Write the failing VM tests**

`DiffusionNexus.Tests/ViewModels/SyncReportDialogViewModelTests.cs` (new, LF):

```csharp
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

public class SyncReportDialogViewModelTests
{
    private static SyncReport Report(
        IReadOnlyList<SyncStepReport> steps,
        IReadOnlyList<SyncFailure>? failures = null,
        bool cancelled = false,
        int unexpected = 0)
    {
        var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel });
        var plan = new SyncPlan(SyncScope.Library, options, Array.Empty<SyncPlanStep>(), DateTimeOffset.UtcNow);
        return new SyncReport(plan, steps, failures ?? Array.Empty<SyncFailure>(), cancelled,
            TimeSpan.FromSeconds(90), NewFilesDiscovered: 0, UnexpectedFailures: unexpected);
    }

    [Fact]
    public void FailuresAreGroupedByStep_WithNameAndReasonPerRow()
    {
        var report = Report(
            new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 7, 0, 3),
                    new SyncStepReport(SyncStepKind.Thumbnails, 5, 5, 4, 0, 1) },
            new[]
            {
                new SyncFailure(SyncStepKind.FetchTags, 1, "ModelA", "Timeout"),
                new SyncFailure(SyncStepKind.FetchTags, 2, "ModelB", "Timeout"),
                new SyncFailure(SyncStepKind.FetchTags, 3, "ModelC", "Http500"),
                new SyncFailure(SyncStepKind.Thumbnails, 4, "ModelD", "Http404"),
            });

        var vm = new SyncReportDialogViewModel(report, newFilesDiscovered: 0);

        vm.FailureGroups.Should().HaveCount(2);
        var tags = vm.FailureGroups.Single(g => g.Kind == SyncStepKind.FetchTags);
        tags.Header.Should().Be("Tags — 3 failed");
        tags.Items.Should().HaveCount(3);
        tags.Items[0].Name.Should().Be("ModelA");
        tags.Items[0].Reason.Should().Be("Timeout");
        vm.HasFailures.Should().BeTrue();
    }

    [Fact]
    public void ACleanRun_ShowsNoFailureGroups()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 10, 0, 0) }),
            newFilesDiscovered: 3);

        vm.HasFailures.Should().BeFalse();
        vm.FailureGroups.Should().BeEmpty();
        vm.DiscoveredText.Should().Contain("3");
    }

    [Fact]
    public void ACancelledRun_SaysPartial()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 4, 4, 0, 0) }, cancelled: true),
            newFilesDiscovered: 0);

        vm.IsPartial.Should().BeTrue();
        vm.PartialText.Should().Contain("Cancelled");
    }

    [Fact]
    public void UnexpectedFailures_AreCalledOut()
    {
        var vm = new SyncReportDialogViewModel(
            Report(new[] { new SyncStepReport(SyncStepKind.FetchTags, 10, 10, 9, 0, 1) }, unexpected: 1),
            newFilesDiscovered: 0);

        vm.UnexpectedText.Should().Contain("1").And.ContainEquivalentOf("log");
    }
}
```

- [ ] **Step 2: Run — fails to compile.**

- [ ] **Step 3: Implement the VM**

`SyncReportDialogViewModel.cs` — plain read-only projection, no observable state:

```csharp
public sealed class SyncReportStepRowViewModel   // Label, Planned, Processed, Succeeded, Skipped, Failed, bool HasFailed
public sealed record SyncReportFailureItem(string Name, string Reason);
public sealed class SyncReportFailureGroup       // SyncStepKind Kind, string Header ("Tags — 3 failed"), IReadOnlyList<SyncReportFailureItem> Items

public sealed class SyncReportDialogViewModel
{
    public SyncReportDialogViewModel(SyncReport report, int newFilesDiscovered) { ... }

    public IReadOnlyList<SyncReportStepRowViewModel> StepRows { get; }   // one per report.Steps, in report order
    public IReadOnlyList<SyncReportFailureGroup> FailureGroups { get; }  // grouped by Step, report order, only steps with failures
    public bool HasFailures { get; }
    public bool IsPartial { get; }            // report.Cancelled
    public string PartialText { get; }        // "Cancelled — partial run. Completed items are recorded and will not be redone."
    public string SummaryText { get; }        // report.Summary
    public string ElapsedText { get; }        // reuse SyncPlanDialogViewModel.FormatDuration
    public string DiscoveredText { get; }     // "N new file(s) discovered" or ""
    public bool HasDiscoveredFiles { get; }
    public string UnexpectedText { get; }     // "N item(s) failed unexpectedly — see the log." or ""
    public bool HasUnexpected { get; }
}
```

Group headers use `SyncReport.Label(kind)`.

- [ ] **Step 4: Run the VM tests** — PASS.

- [ ] **Step 5: The window + DialogService**

`SyncReportDialog.axaml`: Title "Sync Report", `Width="560" SizeToContent="Height"` (cap with `MaxHeight="700"`, failures list inside a `ScrollViewer`). Top: `SummaryText` semibold; `PartialText` in the app's warning color (`IsVisible="{Binding IsPartial}"`); `DiscoveredText`; `UnexpectedText` warning-colored (`IsVisible="{Binding HasUnexpected}"`); `ElapsedText` dimmed. Middle: step table — `ItemsControl` over `StepRows` with a header row: Step | Planned | Processed | Succeeded | Skipped | Failed (Failed cell colored when `HasFailed`). Below (`IsVisible="{Binding HasFailures}"`): one collapsed `Expander` per `FailureGroups` entry (`Header="{Binding Header}"`), content an `ItemsControl` of `Name — Reason` rows (`FontSize="12"`, name semibold, reason dimmed, `TextWrapping="Wrap"`). Footer: a single **Close** button.

Code-behind: same pattern; no result (`Close()` only). `IDialogService`:

```csharp
    /// <summary>Shows the post-run sync report.</summary>
    Task ShowSyncReportDialogAsync(SyncReportDialogViewModel viewModel);
```

`DialogService` implementation mirrors Task 3's (marshal, construct, `await dialog.ShowDialog(_window)`).

- [ ] **Step 6: Build UI (0 warnings), full suite, commit**

```bash
git add -A
git commit -m "feat(sync): SyncReportDialog — per-step counts and grouped, expandable failures"
```

---

### Task 5: Wire the flow — discover → plan → dialog → execute → stamp → report

**Files:**
- Modify: `DiffusionNexus.Domain/Services/Sync/SyncPlan.cs` (`HasWork`)
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (`DownloadMissingMetadataAsync` ~line 820, `DownloadMetadataForTileAsync` ~line 1555, tile-dependency construction)
- Modify: `DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs` (`IsScrollFetchDue` ~line 1400, its call site ~line 1340, `ModelTileDependencies`)
- Test: `DiffusionNexus.Tests/Viewer/LoraViewerViewModelSyncTests.cs` (rework), `DiffusionNexus.Tests/Viewer/ModelTileThumbnailTests.cs` (signature updates), `DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs:98` area (HasWork assertion)

**Interfaces:**
- Consumes: everything Tasks 1–4 produced.
- Produces: the final user-facing flow. Also `internal static bool ModelTileViewModel.IsScrollFetchDue(ModelImage image, DateTimeOffset now, SyncRetryPolicy policy)` (3-arg) and `ModelTileDependencies.RetryPolicyProvider` (`Func<SyncRetryPolicy>?`).

The new flow, replacing the body between the guards and the catch blocks of `DownloadMissingMetadataAsync` (the guards, CTS bookkeeping, catch/finally stay as they are):

1. Busy on: `BusyMessage = "Scanning source folders…"`, `SyncStatus = "Planning sync..."`; keep the existing backfill check + message.
2. Read settings once: `var settings = await _settingsService.GetSettingsAsync(ct)`; derive `var policy = SyncRetryPolicy.FromDays(settings.SyncNotIdentifiedRetryDays, settings.SyncErrorRetryDays)`; cache it in a new field `private SyncRetryPolicy _scrollRetryPolicy = SyncRetryPolicy.Default;` (`_scrollRetryPolicy = policy;`) — the tiles' scroll gate reads this via the dependency provider.
3. **Discovery pre-run** (so the dialog's counts include files added since startup, and the dialog needs no un-countable Discover row):
   ```csharp
   var discoverOptions = new SyncOptions(DiscoverOnly, RetryPolicy: policy);
   var discoverPlan = await Task.Run(() => _librarySync.PlanAsync(SyncScope.Library, discoverOptions, ct), ct);
   var discoverReport = await Task.Run(() => _librarySync.ExecuteAsync(discoverPlan, null, ct), ct);
   var discovered = discoverReport.NewFilesDiscovered;
   ```
   with `private static readonly IReadOnlySet<SyncStepKind> DiscoverOnly = new HashSet<SyncStepKind> { SyncStepKind.DiscoverFiles };` and `private static readonly IReadOnlySet<SyncStepKind> PlannedStepKinds = new HashSet<SyncStepKind> { SyncStepKind.IdentifyModel, SyncStepKind.FetchTags, SyncStepKind.FetchImages, SyncStepKind.Thumbnails };`
4. Plan: `var baseOptions = new SyncOptions(PlannedStepKinds, RetryPolicy: policy, ThumbnailConcurrency: settings.SyncThumbnailConcurrency);` then `PlanAsync(SyncScope.Library, baseOptions, ct)`. Log Info (`"CivitaiSync"`): `$"Plan dialog: {string.Join(" · ", plan.Steps.Select(s => $"{SyncReport.Label(s.Kind)} {s.Count}"))} · {discovered} discovered"`.
5. Drop the overlay for the dialog (`IsBusy = false; IsCancellable = false; BusyMessage = null;`) and show it:
   ```csharp
   var dialogService = DialogService ?? App.Services?.GetService<IDialogService>();
   if (dialogService is null) { SyncStatus = "Dialog service not available."; return; }

   var dialogVm = new SyncPlanDialogViewModel(plan, baseOptions,
       replanAsync: o => Task.Run(() => _librarySync.PlanAsync(SyncScope.Library, o, ct), ct),
       settings.LastLibrarySyncAt, discovered, _logger);
   var choice = await dialogService.ShowSyncPlanDialogAsync(dialogVm);
   if (!choice.Confirmed || choice.Options is null)
   {
       SyncStatus = plan.HasWork ? "Sync cancelled — nothing was run." : UpToDateStatus;
       _logger?.Info(LogCategory.Network, "CivitaiSync", "User cancelled at the plan dialog");
       return;
   }
   ```
6. Re-arm the overlay (`IsBusy = true; IsCancellable = true; BusyMessage = "Syncing with Civitai...";`), **re-plan with the chosen options** (`PlanAsync(SyncScope.Library, choice.Options, ct)` — cheap, and `RunStepAsync` re-selects anyway; the dialog may have been open for minutes), log the choice (`$"User started sync: steps [{string.Join(", ", choice.Options.Steps)}] forces [I:{...} T:{...} Im:{...} Th:{...}]"`), then execute with the existing `UiProgress` wiring, unchanged.
7. After execute: if `!report.Cancelled`, `await _settingsService.UpdateLastLibrarySyncAtAsync(DateTimeOffset.UtcNow, CancellationToken.None);` (NOT `ct` — the stamp records what already happened and must survive a just-pressed Cancel).
8. `await Task.Run(RebuildTilesFromDatabaseAsync);` once (existing), set `SyncStatus = DescribeOutcome(report)` and log it (existing), then drop the overlay explicitly (`IsBusy = false; BusyMessage = null;` — the finally re-does this harmlessly) and show the report: `await dialogService.ShowSyncReportDialogAsync(new SyncReportDialogViewModel(report, discovered));`
9. Wrap BOTH `ExecuteAsync` calls' `InvalidOperationException` ("A library sync is already running" — thrown by the service's `Wait(0)` gate; a post-download completion sync can briefly hold the slot): catch it, set `SyncStatus = AlreadyRunningStatus`, log Info, return. Do not retry in a loop.
10. Delete the now-satisfied comment `// Plan B replaces this with a confirmation dialog showing the same numbers.` and the old pre-run `!plan.HasWork` early return (the dialog's up-to-date state replaces it).

- [ ] **Step 1: Fix `HasWork`**

`SyncPlan.cs`: change to

```csharp
    /// <summary>
    /// Whether any step has counted work. DiscoverFiles is deliberately not special-cased: its
    /// count is always 0 (it scans, it cannot be counted in advance), so a discovery-bearing plan
    /// must be executed on its own terms, not smuggled in as "work" here — that special case made
    /// this property constant-true for every SyncOptions.All plan and the up-to-date branch dead.
    /// </summary>
    public bool HasWork => Steps.Any(s => s.Count > 0);
```

Then `Grep HasWork` across Tests + UI and fix the assertions: `DiffusionNexus.Tests/Sync/Domain/SyncRetryPolicyTests.cs:98` (read the surrounding test — if it asserts the old always-true behavior for a discovery plan, invert it into a test that a DiscoverFiles-only zero-count plan has NO counted work) and `LoraViewerViewModelSyncTests` (reworked below anyway).

- [ ] **Step 2: Scroll gate takes the policy**

`ModelTileViewModel.cs`:
- `IsScrollFetchDue` (~line 1400) becomes `internal static bool IsScrollFetchDue(ModelImage image, DateTimeOffset now, SyncRetryPolicy policy) => policy.IsThumbnailDue(image.ThumbnailAttemptedAt, image.ThumbnailFailure, now, force: false);` — update its `<remarks>` to say the policy now comes from the user's settings via the tile dependencies.
- `ModelTileDependencies` (find its declaration — grep `ModelTileDependencies`): add `public Func<SyncRetryPolicy>? RetryPolicyProvider { get; init; }` (match the type's existing property style).
- Call site (~line 1340): `var policy = _dependencies?.RetryPolicyProvider?.Invoke() ?? SyncRetryPolicy.Default;` then pass it. (Adapt the field name to whatever the class stores its dependencies in.)
- `LoraViewerViewModel`: where it builds `ModelTileDependencies` (grep `new ModelTileDependencies`), add `RetryPolicyProvider = () => _scrollRetryPolicy,`. Initialize `_scrollRetryPolicy` from settings in the existing startup settings-read path (the same method that first reads `IAppSettingsService` when the viewer loads — find it; if none reads full settings, add the read to the initial `RebuildTilesFromDatabaseAsync` entry path, once, not per tile).
- Update the four `IsScrollFetchDue` call sites in `DiffusionNexus.Tests/Viewer/ModelTileThumbnailTests.cs` (lines ~238–280) to pass `SyncRetryPolicy.Default` explicitly, and add one new test: a policy with a 7-day error window makes an image due at day 8 that `Default` (1-day window would also pass — so instead use the inverse: a 7-day-old `HttpError` attempt is due under Default's 1-day window but NOT due under `SyncRetryPolicy.FromDays(30, 30)`). Assert both directions.

- [ ] **Step 3: Rework the VM flow tests RED-first**

`DiffusionNexus.Tests/Viewer/LoraViewerViewModelSyncTests.cs` — read the whole file first; it already mocks `ILibrarySyncService`, uses `ImmediateUiScheduler`, and injects `IServiceScopeFactory`. Extend the fixture: a `Mock<IDialogService>` assigned to the VM's inherited `DialogService` property, with `ShowSyncPlanDialogAsync` returning a canned `SyncPlanDialogResult` and `ShowSyncReportDialogAsync` returning `Task.CompletedTask`. The `IAppSettingsService` mock's `GetSettingsAsync` returns an `AppSettings` with the four new defaults. New/updated tests (adapt names to the file's style):

1. `BulkSync_RunsDiscoveryFirst_ThenShowsThePlanDialog` — `ExecuteAsync` is called with a DiscoverFiles-only plan BEFORE `ShowSyncPlanDialogAsync`; the dialog VM handed over carries the discovered count from that report and rows for the four kinds only.
2. `CancellingThePlanDialog_RunsNothing` — dialog returns `Cancelled()`; `ExecuteAsync` was called exactly once (discovery); `UpdateLastLibrarySyncAtAsync` never; status is "Sync cancelled — nothing was run." (or `UpToDateStatus` for an all-zero plan — cover both).
3. `ConfirmedOptions_AreForwardedToTheRun` — dialog returns options with `Steps = {FetchTags}` + `ForceTags = true`; the second `PlanAsync` and the executed plan carry exactly those options; `ThumbnailConcurrency` and `RetryPolicy` came from the settings mock (assert `NotIdentifiedRetryAfter == TimeSpan.FromDays(settings value)`).
4. `ACompletedRun_StampsLastLibrarySyncAt_ACancelledRunDoesNot` — two cases via the mocked report's `Cancelled` flag; verify `UpdateLastLibrarySyncAtAsync` accordingly.
5. `TheReportDialog_IsShownAfterTheRun` — with the report the sync mock returned, `ShowSyncReportDialogAsync` received a VM built from it.
6. `ASecondRunningSync_IsReportedNotThrown` — `ExecuteAsync` throws `InvalidOperationException`; status becomes `AlreadyRunningStatus`, no crash, no report dialog.

Run the filter → the new tests FAIL against the current flow.

- [ ] **Step 4: Implement the flow** as specified in the numbered list above. Keep the catch/finally structure, `UiProgress`, `DescribeOutcome`, and the per-tile path's own status strings untouched except where named.

- [ ] **Step 5: Per-tile options through settings**

`DownloadMetadataForTileAsync` (~line 1575): the `new SyncOptions(...)` gains `RetryPolicy: _scrollRetryPolicy` (the cached settings-derived policy; forces already make most windows moot — this matters for the un-forced tags/images fetches). Leave `ThumbnailConcurrency` at its default (a single model's handful of images gains nothing).

- [ ] **Step 6: Run the reworked tests, then the full suite** — PASS. Build UI `-c Release` → 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "feat(sync): plan dialog gates the bulk sync; report dialog after; settings-driven retry windows"
```

---

### Task 6: Settings UI — the "Metadata Sync" block

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/SettingsViewModel.cs`
- Modify: `DiffusionNexus.UI/Views/SettingsView.axaml` (LoRA Viewer expander, between "Civitai Update Check" ~line 317 and "Base Model Filter" ~line 320)
- Test: extend whichever `DiffusionNexus.Tests/ViewModels/SettingsViewModel*Tests.cs` file covers load/save round-trips (read `SettingsViewModelValidationTests.cs` and `SettingsViewModelSchedulerTests.cs` first to pick the right home; create `SettingsViewModelSyncSettingsTests.cs` if neither fits)

**Interfaces:**
- Consumes: the four `AppSettings` columns from Task 1.
- Produces: user-editable values that Task 5's flow reads on every press of Download Metadata (no restart needed — the flow reads settings per run).

- [ ] **Step 1: Failing test**

Test that loading maps the three values onto the VM, that changing each sets `HasChanges`, and — **critical** — that the save path preserves `LastLibrarySyncAt`: the save command builds a `new AppSettings { ... }` object (see ~line 626), and a field it forgets is silently nulled on every settings save. Read how the save block preserves `LastBackupAt` (or whether it re-reads and mutates) and assert `LastLibrarySyncAt` survives a save the same way. If the existing test harness can drive `SaveSettingsAsync` (the scheduler/validation tests show how), write:

```csharp
    [Fact]
    public async Task SavingSettings_PreservesLastLibrarySyncAt()
    {
        // arrange: settings service mock returns AppSettings { LastLibrarySyncAt = <known value>, ... };
        // act: load VM, change SyncErrorRetryDays, invoke the save command;
        // assert: the AppSettings instance passed to SaveSettingsAsync carries the known LastLibrarySyncAt.
    }
```

(Replace the comment skeleton with real arrange/act/assert against the file's existing mock fixture — the fixture already exists in those test files; this is adaptation, not invention.)

- [ ] **Step 2: Run — RED.**

- [ ] **Step 3: ViewModel**

`SettingsViewModel.cs`, mirroring the `LoraUpdateCheckStalenessDays` triple exactly (observable property + `Available…` list + `On…Changed → HasChanges` + load assignment + save assignment):

```csharp
    [ObservableProperty]
    private int _syncNotIdentifiedRetryDays = 30;

    [ObservableProperty]
    private int _syncErrorRetryDays = 1;

    [ObservableProperty]
    private int _syncThumbnailConcurrency = 4;

    /// <summary>Selectable windows (days) before a not-identified model is re-checked.</summary>
    public IReadOnlyList<int> AvailableSyncNotIdentifiedRetryDays { get; } = new[] { 7, 14, 30, 60, 90 };

    /// <summary>Selectable windows (days) before an errored attempt is retried.</summary>
    public IReadOnlyList<int> AvailableSyncErrorRetryDays { get; } = new[] { 1, 3, 7 };

    /// <summary>Parallel thumbnail downloads during a bulk sync.</summary>
    public IReadOnlyList<int> AvailableSyncThumbnailConcurrency { get; } = new[] { 1, 2, 4, 6, 8 };
```

plus the three `partial void On…Changed(int value) => HasChanges = true;` next to the existing ones, the three load assignments in `LoadSettingsAsync`, and the three save assignments in the `new AppSettings { ... }` — **and** `LastLibrarySyncAt = <however LastBackupAt is preserved there>` (copy the existing mechanism; if `LastBackupAt` is itself dropped by that block, that is a pre-existing bug — preserve `LastLibrarySyncAt` correctly anyway and note the `LastBackupAt` finding in your report rather than fixing it unasked).

- [ ] **Step 4: XAML block**

Insert into the LoRA Viewer expander between the "Civitai Update Check" StackPanel and the "Base Model Filter" block, separated by the section divider used there (`<Border Height="1" Background="#40FFFFFF" Margin="0,8"/>` — copy the neighbors' exact divider markup):

```xml
                      <StackPanel Spacing="8">
                        <TextBlock Text="Metadata Sync" FontWeight="SemiBold" FontSize="14"/>
                        <TextBlock Text="How the bulk metadata sync retries models it could not resolve, and how many thumbnails it downloads at once. Applies from the next sync run."
                                   FontSize="12"
                                   Opacity="0.7"
                                   TextWrapping="Wrap"/>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                          <ComboBox ItemsSource="{Binding AvailableSyncNotIdentifiedRetryDays}"
                                    SelectedItem="{Binding SyncNotIdentifiedRetryDays, Mode=TwoWay}"
                                    Width="80"/>
                          <TextBlock Text="Days before re-checking models Civitai did not identify" VerticalAlignment="Center"/>
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                          <ComboBox ItemsSource="{Binding AvailableSyncErrorRetryDays}"
                                    SelectedItem="{Binding SyncErrorRetryDays, Mode=TwoWay}"
                                    Width="80"/>
                          <TextBlock Text="Days before retrying failed lookups" VerticalAlignment="Center"/>
                        </StackPanel>
                        <StackPanel Orientation="Horizontal" Spacing="8">
                          <ComboBox ItemsSource="{Binding AvailableSyncThumbnailConcurrency}"
                                    SelectedItem="{Binding SyncThumbnailConcurrency, Mode=TwoWay}"
                                    Width="80"/>
                          <TextBlock Text="Thumbnail downloads in parallel" VerticalAlignment="Center"/>
                        </StackPanel>
                      </StackPanel>
```

(Indentation: match the neighboring blocks in the file, not this snippet.)

- [ ] **Step 5: Run tests + full suite, build UI 0 warnings, commit**

```bash
git add -A
git commit -m "feat(settings): Metadata Sync section — retry windows and thumbnail parallelism"
```

---

### Task 7: Rider (`UploadThumbnailAsync` → `ThumbnailWriter`) + docs

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.Editing.cs` (~lines 750–756)
- Modify: `DiffusionNexus.UI/Doc/LoraViewer.md`
- Modify: `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` (§5: tick WP6)

**Interfaces:** consumes `ThumbnailWriter.ApplySuccess(ModelImage, ThumbnailPayload, DateTimeOffset)` + `ThumbnailPayload(byte[] Data, string MimeType, int Width, int Height)` from `DiffusionNexus.Service.Services.Sync.Thumbnails` (existing since Plan B).

- [ ] **Step 1: The rider**

In `UploadThumbnailAsync`, replace the four direct thumbnail-column writes (`image.ThumbnailData = data;` … `image.ThumbnailHeight = height;`) with:

```csharp
            // Plan B made ThumbnailWriter the one writer of the six thumbnail columns; this was the
            // last path around it. Routing through it also stamps the attempt and clears any prior
            // failure verdict — a user-uploaded image must not sit next to yesterday's "Corrupt".
            ThumbnailWriter.ApplySuccess(image, new ThumbnailPayload(data, mime, width, height), DateTimeOffset.UtcNow);
```

Keep the cache-invalidation lines that follow (`IsLocalCacheValid = false; LocalCachePath = null; CachedAt = …`) — those are local-cache columns, not ThumbnailWriter's six. Keep `dbModel.IsUserEdited = true;`. Add the `using DiffusionNexus.Service.Services.Sync.Thumbnails;` import. If an existing test harness covers `UploadThumbnailAsync`, extend it to assert `ThumbnailFailure` is cleared and `ThumbnailAttemptedAt` stamped; if none exists (it needs the file dialog), do NOT build a harness for it — state that in your report.

- [ ] **Step 2: Docs — `DiffusionNexus.UI/Doc/LoraViewer.md`**

Read §4 ("Data Flow — Download Metadata") and §7 ("UI Layout") first, then:
- Replace the line `(Plan B puts a confirmation dialog here; for now the plan is logged and started)` (~line 189) and update §4's flow diagram/steps to the real flow: button → discovery run → plan → **SyncPlanDialog** (rows, Force expander, up-to-date state with last-run stamp) → execute (chosen steps only) → `LastLibrarySyncAt` stamp → one rebuild → **SyncReportDialog** (step table + grouped failures).
- In §4's "Retry windows" subsection (~line 381): note the windows now come from Settings → LoRA Viewer → Metadata Sync (`SyncRetryPolicy.FromDays`, floors at 1 day, `MaxErrorAttempts` fixed at 3), and that the scroll-time thumbnail gate uses the same settings-derived policy.
- In §9's "Bulk sync — ThumbnailsStep" subsection: document the bounded parallelism (Thumbnails only, `SyncOptions.ThumbnailConcurrency`, clamp 1–8, default 4, API steps stay paced and sequential).
- §7 UI Layout: add the two dialogs where the section lists windows/overlays.
- Keep the doc's voice and heading style; do not renumber sections.

- [ ] **Step 3: Spec bookkeeping**

In `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` §5, tick WP6 (`- [x] **WP6 — UI** …`) and append `(Plan E — this branch)` the way WP5's line does.

- [ ] **Step 4: Full suite + UI build, commit**

```bash
git add -A
git commit -m "refactor(detail): route thumbnail upload through ThumbnailWriter; document the Plan E sync UI"
```

---

## Verification (whole branch, after Task 7)

- `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` → 0 failures.
- `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release` → 0 warnings.
- `git diff develop --stat` — no unrelated files; no EOL-only churn on any touched file.
- Manual smokes owed by the user (record in the PR): press Download Metadata on the reference library → dialog counts match expectation, Start runs only checked steps, report groups failures; second press → up-to-date dialog with last-run stamp and no Start; a Force from the up-to-date dialog produces counts; settings changes alter the next run's plan; cancel mid-run → partial report, next plan resumes remainder (spec §6 acceptance items 1–3, 7 — WP7 will formalize them).
