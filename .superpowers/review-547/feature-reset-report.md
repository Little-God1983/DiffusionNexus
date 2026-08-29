# Civitai Browser filter bar — Reset button

## What was built

- `CivitaiBrowserViewModel.ResetFilter()` (`[RelayCommand]` → `ResetFilterCommand`): returns
  `SearchText`, the four Show toggles, the base-model selection (live mirror **and**
  `_stickyBaseModelSelections`), `SelectedSort`, `SelectedPeriod` and `SelectedModelType` to
  the constructor's defaults, then fires exactly one `SearchAsync()`.
- `CanReset` computed property: true when anything differs from those same defaults. Bound to
  the new Reset button's `IsEnabled`.
- New XAML button in `CivitaiBrowserView.axaml`'s filter bar, right after Save filter, styled
  the same way (`↺ Reset`, tooltip explaining what it clears). Grid grew one `Auto` column.
- `_defaultModelType` field: the "All LoRA types" `ModelTypeOption` is now looked up **once**
  in the constructor and reused by both the constructor's own `SelectedModelType` assignment and
  `ResetFilter`/`CanReset` — no duplicated label string.

## Guaranteeing exactly one search

Three independent search triggers exist in this VM: `OnSearchTextChanged` →
`DebouncedSearchAsync`, `OnQueryOptionChanged` (Sort/Period/Model type) → cursor clear +
`DebouncedSearchAsync`, and each base-model item's `SelectionChanged` →
`OnBaseModelFilterToggled` → immediate `SearchAsync`.

- Base-model half: reused the existing `_suppressBaseModelFilterEvents` flag (same one
  `ClearBaseModelFilters` already uses).
- New `_suppressFilterChangeSearch` flag gates the *search* call in `OnSearchTextChanged` and
  `OnQueryOptionChanged` — but **not** `OnQueryOptionChanged`'s synchronous cursor
  clear/`HasMore` raise, which must still run for a reset exactly like any other query-option
  change (per the method's own doc comment about the pagination bug it exists to prevent).
- `MaybeTopUpVisibleResults` (the auto-load-more triggered from `ApplyClientSideFilters`, which
  the Show-flag hooks call) also checks this flag, so a Show-flag write mid-reset can't sneak in
  an extra API call via the auto-top-up path regardless of property-write order or leftover
  cursor state.
- `_debounceCts?.Cancel()` is called explicitly at the top of `ResetFilter`: a search-text or
  query-option change made just before Reset (but inside its 400ms debounce window) leaves a
  pending `DebouncedSearchAsync` timer; suppressing Reset's own writes never cancels that
  *pre-existing* timer on its own, so without this call it would still fire its own `SearchAsync`
  up to 400ms later — a second, delayed search. Covered by
  `ResetFilter_CancelsAPendingDebounce_SoNoSearchArrivesLater`, which waits 600ms past a dirtied
  `SearchText` and asserts zero further `GetModelsAsync` calls.
- Exactly one explicit `SearchAsync()` call at the end, mirroring `ClearBaseModelFilters`'s
  existing pattern.

## Reset does NOT touch the saved filter

No call to `SaveFilterAsync`/`SetCivitaiBrowserFilterJsonAsync` anywhere in `ResetFilter`.
Covered by `ResetFilter_DoesNotWriteToSettings` (mocked `IAppSettingsService`, `Times.Never`).

## Tests added — `DiffusionNexus.Tests/Viewer/CivitaiBrowserFilterResetTests.cs` (16 tests)

- `ResetFilter_ReturnsEveryPropertyToItsDefault_IncludingParkedStickySelections`
- `ResetFilter_FiresExactlyOneSearch_NotOnePerRestoredProperty` (asserts `Times.Once` on the
  mocked `ICivitaiClient`)
- `ResetFilter_CancelsAPendingDebounce_SoNoSearchArrivesLater`
- `ResetFilter_DoesNotWriteToSettings`
- `CanReset_IsFalseAtDefaults`
- `CanReset_IsTrueAfterChangingSearchText`
- `CanReset_IsTrueAfterUntickingAnyShowFlag` (`[Theory]`, one case per Show flag — 4 tests)
- `CanReset_IsTrueAfterSelectingABaseModel`
- `CanReset_IsTrueWithOnlyAParkedStickySelection_NoLiveSelectionAtAll`
- `CanReset_IsTrueAfterChangingSort` / `...Period` / `...ModelType`
- `CanReset_GoesFalseAgain_AfterReset`

All 16 pass in isolation and as part of the full suite.

## Verification

- `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Debug` — 0 warnings, 0 errors.
- `dotnet build DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Debug` — 0 errors (pre-existing
  unrelated warnings only).
- `dotnet test DiffusionNexus.Tests` (full suite, run twice): **4989 passed / 0 failed / 2
  skipped** of 4991 total. Baseline was 4973/0/2 (4975 total); 4991 − 4975 = 16, matching the new
  test count exactly.
- `dotnet test DiffusionNexus.IntegrationTests`: **5/5 passed**.
- `dotnet build DiffusionNexus.sln -c Release -p:OutDir="bin-verify\"` (side output path, per-project,
  cleaned up afterward) — 0 errors.

### Pre-existing flake investigated (not caused by this change)

Filtering the test run down to just `~CivitaiBrowser` (53–69 tests, not the full 4991-test suite)
intermittently fails one of two dispatcher-pump race tests —
`CivitaiBrowserViewModelBaseModelFilterTests.DeferredInitialSearch_DoesNotClobberAUserInitiatedSearch`
or `CivitaiBrowserFilterPersistenceTests.RestoreSavedFilter_SkipsBaseModelSelection_WhenUserSearchAlreadyStarted`.
Verified via `git stash` that this reproduces identically on the pre-change baseline with zero
relation to this diff — same failure, same alternation between the two test names, same
pass-in-isolation behavior. Both tests already document in their own comments that
`Dispatcher.UIThread` is a process-wide singleton shared across parallel test classes; this is
that same class of contention, not a new flake. It never appeared in either of the two full-suite
runs above. Not one of the four flakes named in the task brief, but same shape — order/parallelism
dependent, solid alone, absent from the full run.

## Not done / follow-ups

- None identified. The feature, its tests, and the two full-suite/Release verifications are all
  clean.
