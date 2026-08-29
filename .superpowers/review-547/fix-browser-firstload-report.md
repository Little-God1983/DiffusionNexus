# Fix: Civitai browser empty-on-open / "no results, then refreshes" regression

Branch `feature/civitai-api-gateway`, starting commit `444645fc`.

## Root cause (given, not re-derived)

`EnsureLoadedAsync` (called from `CivitaiBrowserView.OnAttachedToVisualTree`) replaced the
old constructor's two concurrent fire-and-forget tasks with:

```csharp
await RefreshInstalledSetAsync();
await SearchAsync();
```

1. The first search now waits on a full-library DB scan it never used to wait for →
   perceived "empty browser" on open.
2. `SearchAsync` has no re-entrancy guard, and `LoadNextAsync`'s
   `if (ct.IsCancellationRequested) return;` check runs *before* the UI-thread write, not
   inside it — so a cancellation landing between the check and the dispatched write still
   lets a superseded pipeline write stale data into `Results`, producing the
   no-results-then-refresh flicker when the deferred load and a user-typed search race.

## Step 1 — reproduction (before any fix)

New test:
`DiffusionNexus.Tests/Viewer/CivitaiBrowserViewModelBaseModelFilterTests.cs` →
`DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch`.

Mechanism: two `TaskCompletionSource<CivitaiPagedResponse<CivitaiModel>>` back the two
`ICivitaiClient.GetModelsAsync` calls (call 1 = the deferred `EnsureLoadedAsync` search,
call 2 = a user-triggered `SearchCommand` invocation). Because `TaskCompletionSource`
continuations run synchronously on the thread that completes them (no sync context in
xunit), releasing `tcsA` drives the deferred pipeline forward *exactly* to its queued
`Dispatcher.UIThread.InvokeAsync` write (past the pre-dispatch cancellation check, since
nothing had cancelled it yet) and no further, since nothing pumps the dispatcher queue yet.
Only then is the user's search started — cancelling the deferred pipeline's token *after*
its check already passed, which is the exact narrow window the root-cause note describes.
Releasing `tcsB` and then calling `Dispatcher.UIThread.RunJobs()` drains both queued writes
together.

Ran against the unmodified code:

```
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj \
  --filter "FullyQualifiedName~DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch"
```

Result: **FAIL**, for the right reason —

```
Expected vm.Results.Select(r => r.Model!.Id) to be equal to {2} because the deferred load
was cancelled by the user's own search (after its check had already passed) and must not
write its stale response into Results, but {1, 2} contains 1 item(s) too many.
```

`Results` contained `{1, 2}` — the cancelled deferred pipeline's stale "StaleModel" (id 1)
leaked in alongside the user's real "RealModel" (id 2), confirming the dispatcher-callback
race exactly as described.

## Step 2 — the fix

### 2a. First search no longer waits on the installed-set load

`EnsureLoadedAsync` now runs `RefreshInstalledSetAsync()` and the (guarded) initial search
concurrently via `Task.WhenAll`, restoring the old constructor's concurrency.
`RefreshInstalledSetAsync` already re-applies itself to whatever is in `Results` when it
lands (loops `ApplyInstalledIndex` + `ApplyClientSideFilters` on the UI thread), so it does
not need to finish first for correctness — this was stated as the reason the old code could
run them in parallel, and nothing about that changed.

### 2b. The deferred search must not clobber an already-started user search

Chosen mechanism: a one-shot `Interlocked` flag, `_searchStarted`, set at the very top of
`SearchAsync` (the single choke point every search path already goes through — the
`SearchCommand`, `DebouncedSearchAsync`, `OnQueryOptionChanged`, `ClearBaseModelFilters`,
`OnBaseModelFilterToggled`, `RefreshAsync` all end up here). `EnsureLoadedAsync` no longer
calls `SearchAsync()` directly; it calls a new `SearchIfNotAlreadyStartedAsync()`:

```csharp
private Task SearchIfNotAlreadyStartedAsync() =>
    Interlocked.CompareExchange(ref _searchStarted, 1, 0) == 0
        ? SearchAsync()
        : Task.CompletedTask;
```

Whichever of {the deferred kickoff, a user action that reaches `SearchAsync`} gets there
first wins the `CompareExchange` and runs the search; the loser is a no-op. This is scoped
to the *first* search only — `_searchStarted` is never reset, so it has zero effect on
every subsequent search-vs-search interaction, which keeps using the existing
cancel-and-restart behavior (debounce, filter changes, etc.) unchanged.

I considered instead making `EnsureLoadedAsync`'s search "join" an in-flight user search
(await it rather than skip it) but rejected it: there is no cheap way to hand back the
in-flight `Task` from `SearchCommand.ExecuteAsync` at the point `EnsureLoadedAsync` needs
it without adding new shared state, and the two searches are for the same initial query
in the overwhelming common case, so "the first one to start wins, the other is a no-op"
is both simpler and behaviorally equivalent to "join" for this call site.

### 2c. Harden the interleave: cancellation is now re-checked inside the dispatcher callback

Both places `LoadNextAsync`/`RunTagFallbackAsync` write into `Results` via
`Dispatcher.UIThread.InvokeAsync(() => { ... })` now check `ct.IsCancellationRequested`
as the first line *inside* that callback (not only before the `await`), and `LoadNextAsync`
also re-checks immediately after the dispatched write returns, before continuing to update
`_nextCursor` / run the tag-fallback / set `StatusMessage`. This directly closes the
check-then-write race regardless of which of the two mechanisms above raced it.

## Step 3 — verification

Reproduction test, post-fix:

```
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj \
  --filter "FullyQualifiedName~CivitaiBrowserViewModelBaseModelFilterTests"
```
```
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 213 ms
```

Full suite:

```
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj
```
```
Passed!  - Failed: 0, Passed: 4945, Skipped: 2, Total: 4947, Duration: 35 s
```

(Baseline was 4944/0/2; +1 is the new reproduction test. None of the four known
order-dependent flakes — `CheckScoreAdapterTests`, `GenerationGalleryViewModelTests`, the
Distiller temp-file-lock test, `JsonInfoFileReaderServiceTests` — failed in this run.)

Release build:

```
dotnet build -c Release
```

Failed on file-locking (`DiffusionNexus.UI.exe`, PID 8644, was running and had its own
`bin\Release\net10.0\*.dll` outputs open — `devenv.exe` also attached), unrelated to this
change — a pre-existing environmental condition (the app was already running before this
session started). Verified the Release compile itself is clean by building to a side output
directory instead of overwriting the locked one:

```
dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release -p:OutDir=bin/ReleaseVerify/
```
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

The side output directories were deleted afterward; nothing was left behind.

## Files touched

- `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs` —
  `EnsureLoadedAsync`, new `_searchStarted` + `SearchIfNotAlreadyStartedAsync`, the
  `Interlocked.Exchange(ref _searchStarted, 1)` at the top of `SearchAsync`, and the
  cancellation re-checks inside/after the two `Dispatcher.UIThread.InvokeAsync` write
  callbacks in `LoadNextAsync` and `RunTagFallbackAsync`.
- `DiffusionNexus.Tests/Viewer/CivitaiBrowserViewModelBaseModelFilterTests.cs` — new
  `DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch` test plus its
  `SingleModelResponse` helper.
