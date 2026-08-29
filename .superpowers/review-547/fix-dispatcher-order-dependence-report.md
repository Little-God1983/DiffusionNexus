# Fix: dispatcher pump order-dependence in CivitaiBrowser tests

## Phase 1 — reproducing and root-causing the mechanism

### Reproduction

At HEAD (`1abee0df`), filtering `DiffusionNexus.Tests` to `FullyQualifiedName~CivitaiBrowser`
(69 tests) is genuinely flaky, not deterministic-always-fails-the-same-way as originally
hypothesized:

- 4 back-to-back isolated runs: 2 passed, 2 failed.
- Failures were NOT always the same test: observed failures on both
  `DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch` (BaseModelFilterTests) and
  `RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted`
  (FilterPersistenceTests), each timing out inside `RunWithDispatcherPumpAsync` with the
  helper's own `TimeoutException` after the full 5-second budget.

This already refines the starting hypothesis: it is not "the same test always fails when run
alone" — it's a genuine flaky race, and either of the two classes' pump-dependent tests can be
the casualty.

### Instrumentation

Added temporary `Console.WriteLine` instrumentation to both `RunWithDispatcherPumpAsync` copies
(thread IDs, iteration counts, `Dispatcher.UIThread.HasJobsWithPriority` snapshots) and re-ran
until a failure was caught live. Key captured evidence from an actual failing run:

```
FilterPersistenceTests caller thread=26 task.IsCompleted(pre-pump)=False
...
FilterPersistenceTests TIMEOUT - caller thread=26, pump thread=22, iterations=318, task.Status=WaitingForActivation
```

The pump thread ran **318 iterations of `Dispatcher.UIThread.RunJobs()` over the full 5 seconds,
never threw, and the awaited task never progressed.** Meanwhile, in the very same window,
*other*, unrelated tests' dispatcher operations completed instantly (1 iteration) — proving the
shared dispatcher mechanism was generally functional during the hang; it just never happened to
service this particular test's operation in time.

### What the pump mechanism actually is (read from Avalonia 11.3.13 source,
`AvaloniaUI/Avalonia` on GitHub — `Dispatcher.cs`, `Dispatcher.Queue.cs`, `Dispatcher.Invoke.cs`,
`IDispatcherImpl.cs`, `DispatcherOperation.cs`)

- `DiffusionNexus.Tests` never initializes an Avalonia `Application`/platform. With no
  `IDispatcherImpl`/`IPlatformThreadingInterface` registered in `AvaloniaLocator`,
  `Dispatcher.UIThread` falls back to Avalonia's internal `NullDispatcherImpl`:
  `CurrentThreadIsLoopThread => true` **unconditionally**, and `Signal()` / the `Signaled` event
  are no-ops.
- Confirmed empirically with an isolated control test (fresh process, single test): binding
  `Dispatcher.UIThread` on one background thread, then calling `CheckAccess()` and
  `Dispatcher.UIThread.RunJobs()` from a **different** thread — both succeeded immediately, no
  throw, no wrong-thread no-op. **This disproves a simple "wrong thread" / "first-toucher wins"
  theory** — the originating hypothesis in the task brief. `RunJobs()` is not thread-affine here;
  it just drains whatever's in one process-wide priority queue (`Dispatcher.UIThread` is a single
  static, `s_uiThread`, shared by the entire test process), locked only around the dequeue.
- Because `Signal()`/`Signaled` are no-ops, nothing ever *wakes* a waiting pump automatically —
  every dispatcher operation posted via `await Dispatcher.UIThread.InvokeAsync(...)` only gets
  serviced when *some* thread, anywhere in the process, happens to call
  `Dispatcher.UIThread.RunJobs()` again. `RunWithDispatcherPumpAsync`'s background `Task.Run` loop
  is exactly that: a best-effort poller with no real signal to rely on, racing against a
  `TaskCompletionSource`-backed `DispatcherOperation` whose completion schedules its continuation
  via `RunContinuationsAsynchronously` (i.e., asynchronously, not inline).
- In the **full 4989-test suite**, there is always some *other* concurrently-running test class
  independently polling the same shared queue (many other `CivitaiBrowser*`/`GenerationGallery*`
  tests, `CivitaiDownloadQueueStartResumeTests`, etc.), so there is effectively always ambient
  "backup pumping" pressure that reliably services any given test's operation well inside 5
  seconds. Filtered down to just the ~11–69 `CivitaiBrowser*` tests, that ambient pressure
  collapses to just these two classes' own pumps, and the timing margin gets thin enough that the
  fixed 5-second budget is occasionally blown — confirmed sensitive to the *smallest* perturbation:
  adding one extra cheap diagnostic read per pump iteration was enough to take the observed
  failure rate from ~35–50% (9 runs, 3 failures) to 0/18 in a follow-up batch, then flaky again
  after reverting it. This is a genuine, timing-sensitive race on a shared, unsignaled queue —
  **not** a "test X must run before test Y" ordering bug.

**Correction to the original hypothesis:** the mechanism is not "whoever runs first sets up
usable dispatcher state, and running alone means nobody did." It is concurrency *volume*: many
unrelated concurrently-running tests act as involuntary backup pumps for each other's shared,
unsignaled `NullDispatcherImpl` queue. Fewer concurrent dispatcher-touching tests means fewer
backup pumps means a real chance of losing the race against the fixed timeout.

## Phase 2 — the fix

### Options considered

1. **Move the dispatcher-dependent tests to `DiffusionNexus.IntegrationTests`.** That project
   already carries a *real* headless Avalonia platform (`TestAppHost.EnsureAvalonia`, and
   `[AvaloniaFact]`'s own bootstrap) with a genuine `ManagedDispatcherImpl` — real thread
   affinity, a real `AutoResetEvent`-based `Signal()`/wakeup, and reentrant dispatcher-frame
   pumping (`DispatcherOperation.Wait()` → `Dispatcher.PushFrame`) for an in-flight `await` on the
   dispatcher's own thread. `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
   is already set there, so no other test class can be concurrently contending for the same
   dispatcher singleton — the entire race class from Phase 1 cannot occur.

2. **Establish the dispatcher properly inside `DiffusionNexus.Tests`, scoped to just these
   fixtures.** Rejected: `Dispatcher.UIThread` is a process-wide static — there is no way to
   "scope" a real platform registration to one test class without it persisting for the rest of
   the process, which is functionally the forbidden global Avalonia init (other tests in this
   project deliberately run with no Avalonia app instance; a global init has broken them before).

3. **Remove the dispatcher dependency from the tests by restructuring what they exercise.**
   Rejected for the two load-bearing regression tests
   (`DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch`,
   `RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted`): they specifically
   pin real interleavings through the actual `await Dispatcher.UIThread.InvokeAsync(...)` hop in
   production code (`LoadNextAsync`'s UI-thread write racing a cancellation;
   `RestoreSavedFilterAsync`'s dispatch racing a user-initiated search). Restructuring them to
   avoid the dispatcher would either stop testing the real race or require contorting production
   code purely for testability — both explicitly out of bounds.

**Chosen: option 1.** Before committing to it, spiked it empirically rather than assuming:
wrote a throwaway `[AvaloniaFact]` test doing a plain `await vm.EnsureLoadedAsync()` (no manual
pump at all) — 5/5 green. Then a harder spike mirroring the `Task.WhenAll` race shape exactly —
5/5 green (10/10 across both spikes). This confirms `[AvaloniaFact]`'s real dispatcher resolves
these `await`s with **no manual pump helper needed at all**, so the fix also deletes
`RunWithDispatcherPumpAsync` entirely rather than porting it.

The "themeless headless session" caveat noted for `CivitaiBrowserViewDeferredLoadTests` (a real
`TabControl` never resolving a template under this project's unstyled `Application`) does not
apply here — these are pure view-model tests with no view/visual tree involved at all.

### What moved

Six tests — the only ones that called `RunWithDispatcherPumpAsync` — moved from
`DiffusionNexus.Tests` to a new file,
`DiffusionNexus.IntegrationTests/CivitaiBrowserViewModelDispatcherTests.cs`, as `[AvaloniaFact]`
tests with plain `await` in place of the pump wrapper:

- `EnsureLoadedAsync_SearchesOnce_HoweverOftenItIsCalled`
- `DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch`
- `SaveThenRestore_RoundTripsThroughAppSettings`
- `EnsureLoadedAsync_CorruptSavedFilterJson_StillRunsTheFirstSearch`
- `EnsureLoadedAsync_SettingsServiceThrows_StillRunsTheFirstSearch`
- `RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted`

`RunWithDispatcherPumpAsync` (duplicated in both source files) was deleted from both — it only
ever existed to route around the unreliable polling this fix eliminates.

**Left in place** (not flaky, doesn't use the helper):
`ApplySavedFilter_NameAbsentFromMirror_SelectsOnceTheSourceMaterializesIt` in
`CivitaiBrowserFilterPersistenceTests.cs` — it uses a plain, same-thread, synchronous
`Dispatcher.UIThread.RunJobs()` call immediately after a fire-and-forget `Post`, with no
`TaskCompletionSource`/background-thread race involved (the "post-hoc drain" idiom its own doc
comment already documents as a different, safe mechanism). All other tests in both files are
fully synchronous and never touch the dispatcher.

None of the six moved tests touch any `internal` `CivitaiBrowserViewModel` member — verified
before moving, since `DiffusionNexus.IntegrationTests` does not have the
`InternalsVisibleTo("DiffusionNexus.Tests")` grant `DiffusionNexus.UI` declares.

### CI coverage

`.github/workflows/dotnet.yml`'s `Test` step runs `dotnet test` with no project argument at the
repo root, which resolves against `DiffusionNexus.sln`. Both `DiffusionNexus.Tests` and
`DiffusionNexus.IntegrationTests` are listed as projects in that solution, so both are already
built and run by the one existing CI invocation — no workflow change needed.

## Phase 3 — verification

All runs on a cold build to `/tmp/dntests_out` / `/tmp/dnint_out` (side output paths, avoiding
any `bin/Release` lock from a stray running `DiffusionNexus.UI.exe`).

- `DiffusionNexus.Tests --filter "FullyQualifiedName~CivitaiBrowser"`, 5 consecutive isolated
  runs: **63/63 passed, every run** (63 = 69 baseline − 6 moved).
- Full `DiffusionNexus.Tests` suite: **4983 passed / 0 failed / 2 skipped** (4985 total = 4989
  baseline passed − 6 moved; skipped count unchanged at 2). Reconciles exactly with the baseline
  minus the six moved tests.
- Full `DiffusionNexus.IntegrationTests` suite: **11 passed / 0 failed** (5 baseline + 6 moved).
- `DiffusionNexus.IntegrationTests --filter "FullyQualifiedName~CivitaiBrowserViewModelDispatcherTests"`,
  5 consecutive isolated runs: **6/6 passed, every run**.

No test in either project now depends on another test having run first, or on ambient
concurrency from unrelated tests to make its own dispatcher operation get serviced in time.

## Pre-existing flakes (explicitly not touched)

`CheckScoreAdapterTests`, `GenerationGalleryViewModelTests`, a Distiller temp-file-lock test, and
`JsonInfoFileReaderServiceTests` are known pre-existing order-dependent flakes in unrelated
subsystems. Not investigated or modified as part of this fix.
