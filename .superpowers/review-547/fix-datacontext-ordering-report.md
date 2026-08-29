# Fix: Browse Civitai opens empty until you type (DataContext/attach ordering)

Branch `feature/civitai-api-gateway`, starting commit `5f450f87`.

## Root cause (given, not re-derived)

`CivitaiBrowserView.OnAttachedToVisualTree` was the only caller of
`CivitaiBrowserViewModel.EnsureLoadedAsync()`, guarded by
`if (DataContext is CivitaiBrowserViewModel vm)`. The view is hosted as
`<browser:CivitaiBrowserView DataContext="{Binding BrowserViewModel}"/>` inside a `TabItem`
(`LoraViewerView.axaml:331`) — a *binding* on the control itself, which must resolve the
inherited parent DataContext before it produces a value. When the host TabControl realises the
tab's content, the visual-tree attach can fire before that binding has resolved, so the
DataContext check sometimes never sees the view model:

1. Empty grid — the initial search lives in `EnsureLoadedAsync`, never called.
2. Installed badge/filter dead — `_installed` stays `CivitaiInstalledIndex.Empty` because
   `RefreshInstalledSetAsync` (the only thing that populates it) is also only reached from
   `EnsureLoadedAsync`.

Typing or changing a filter works because `DebouncedSearchAsync` → `SearchAsync` is an
independent path that doesn't depend on this trigger.

## The fix

`DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml.cs`:

- Added a private `_attachedToVisualTree` bool, set `true` in `OnAttachedToVisualTree` and
  `false` in a new `OnDetachedFromVisualTree` override.
- Added `OnDataContextChanged(EventArgs e)` (the real Avalonia `StyledElement` signature for
  this Avalonia version — no `sender` parameter; confirmed against
  `Avalonia.Base.xml` 11.3.13, `M:Avalonia.StyledElement.OnDataContextChanged(System.EventArgs)`).
- Both overrides funnel into one `TryEnsureLoaded()`, which requires **both** conditions
  (`_attachedToVisualTree` AND `DataContext is CivitaiBrowserViewModel`) before calling
  `_ = vm.EnsureLoadedAsync()`. Whichever of {attach, DataContext-bound} happens second is the
  one that actually fires the call, so the trigger no longer cares which order the two events
  happen in.
- A DataContext assigned before the view is ever attached does **not** fire the load — the
  attach gate is still required, preserving the deliberate "don't search Civitai for a tab the
  user hasn't opened" laziness this branch introduced.
- Switching tabs away and back re-fires both hooks, but `EnsureLoadedAsync`'s existing
  `Interlocked.Exchange(ref _loaded, 1)` guard (untouched) keeps it a no-op after the first
  successful run.

`DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs`:

- Added `_logger?.Debug(LogCategory.Network, "CivitaiBrowser", "Deferred initial load starting
  (view attached + DataContext bound).")` as the first line inside `EnsureLoadedAsync`'s
  `Interlocked.Exchange` guard, so a future instance of this failure mode is visible in the
  Unified Console instead of silent.

## Testing

### Automated: possible, but not in `DiffusionNexus.Tests`

`DiffusionNexus.Tests` deliberately never initializes an Avalonia platform (see the doc comment
on `LoraViewerLibraryNotifierTests`: "No Avalonia platform is initialised (that deadlocks the
suite)"), and this bug is pure view-lifecycle timing — unreachable without a real visual tree.
Per the task, no global Avalonia init was added there.

`DiffusionNexus.IntegrationTests` already runs every test under a headless Avalonia session
(`[AvaloniaFact]`), so I added
`DiffusionNexus.IntegrationTests/CivitaiBrowserViewDeferredLoadTests.cs` there (3 tests):

- `AttachBeforeDataContext_StillTriggersDeferredLoad` — reproduces the actual regression order.
- `DataContextBeforeAttach_StillTriggersDeferredLoad` — the opposite order, plus asserts the
  load does *not* fire while unattached (the deliberate-laziness requirement).
- `DetachAndReattach_DoesNotReRunDeferredLoad` — the idempotency requirement.

**Could not reproduce with an actual `TabControl`.** I first built the tests hosting
`CivitaiBrowserView` inside a real `TabControl`/`TabItem` (matching production exactly) but
diagnostics showed `TabControl` never resolves a control template in this harness at all —
zero visual children, `SelectedContent` stays `null` regardless of how many dispatcher/layout
passes are pumped. Root cause: this project's `[AvaloniaFact]` tests run under a **themeless**
`Avalonia.Application` (confirmed empirically — `Application.Current.GetType()` is
`Avalonia.Application`, not `DiffusionNexus.UI.App`, `Application.Current.Styles.Count == 0`).
`TabControl`'s control theme is only defined by FluentTheme/SimpleTheme, neither of which is
loaded, so it can never produce a container for its content — there is nothing to select or
attach. (`Window`/`Button`/`ContentPresenter` do get a baseline template even without a theme,
so this is specific to compound controls like `TabControl`.)

I switched the tests to a plain `Panel` as the host instead. `Panel` needs no control theme —
children become direct visual children the moment the panel is attached — so it isolates the
exact mechanism under test (a runtime `Binding` on `DataContext`, whose activation is tied to
visual-tree attachment, versus logical parenting, which can happen in either order) without
depending on theme resources this harness doesn't load. This still exercises the real
production mechanism (Avalonia's binding-activation-on-attach behavior), just without the
`TabControl` chrome. Wiring a themed `DiffusionNexus.UI.App` session into this project's
`[AvaloniaFact]` tests (e.g. via `[assembly: AvaloniaTestApplication]`) would be a bigger,
riskier, assembly-wide change with unknown effect on the two existing test classes there, so I
did not do it.

Verified red/green: reverted the view fix (`git stash` on just that file), rebuilt, and
confirmed `AttachBeforeDataContext_StillTriggersDeferredLoad` fails against the original code
(`Expected invocation on the mock once, but was 0 times` — no `Debug` call at all) while the
other two orderings still pass (they don't exercise the buggy path). Restored the fix and
re-ran clean.

### Unrelated pre-existing bug found and fixed along the way

Adding a third `[AvaloniaFact]` test class to `DiffusionNexus.IntegrationTests` exposed a
latent race in the existing suite: `DatasetManagementIntegrationTests` started failing with
`Call from invalid thread` inside `AvaloniaHeadlessPlatform.Initialize`, thrown from
`TestAppHost.EnsureAvalonia()`. xunit runs different test classes' fixtures on different
threads by default; the headless Avalonia platform is a single process-wide singleton, and two
classes racing to be first to touch it crashes. This is pre-existing fragility (confirmed: with
only the original two test classes it happened not to race; a third class shifted scheduling
enough to expose it) that my new test class merely surfaced, not introduced by anything in the
production fix. Fixed with `[assembly: CollectionBehavior(DisableTestParallelization = true)]`
in `TestAppHost.cs` — serializes all test classes in this small, headless-only project. Ran the
full `DiffusionNexus.IntegrationTests` suite 3× after the fix; consistently 5/5 green.

## Verification

```
dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj
```
```
Passed!  - Failed: 0, Passed: 4945, Skipped: 2, Total: 4947, Duration: 43 s
```
Matches the stated baseline (4945/0/2) exactly — no regression, no new tests added here (per
the task, the automated coverage lives in `DiffusionNexus.IntegrationTests` instead). None of
the four known order-dependent flakes (`CheckScoreAdapterTests`, `GenerationGalleryViewModelTests`,
the Distiller temp-file-lock test, `JsonInfoFileReaderServiceTests`) failed in this run.

```
dotnet test DiffusionNexus.IntegrationTests/DiffusionNexus.IntegrationTests.csproj
```
```
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: ~1s
```
(2 pre-existing + 3 new; run 3× to confirm the parallelization race fix held.)

```
dotnet build DiffusionNexus.sln -c Release
```
```
Build succeeded. 0 Warning(s) [new], 0 Error(s)
```
No app process was running, so this built straight to the normal `bin/Release` output — no
side-output-directory workaround was needed.

## Files touched

- `DiffusionNexus.UI/Views/CivitaiBrowser/CivitaiBrowserView.axaml.cs` — the attach/DataContext
  dual-hook fix.
- `DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs` — one `_logger?.Debug`
  line in `EnsureLoadedAsync`.
- `DiffusionNexus.IntegrationTests/CivitaiBrowserViewDeferredLoadTests.cs` — new, 3 tests.
- `DiffusionNexus.IntegrationTests/DiffusionNexus.IntegrationTests.csproj` — added a `Moq`
  package reference (already used the same way throughout `DiffusionNexus.Tests`).
- `DiffusionNexus.IntegrationTests/TestAppHost.cs` — added
  `[assembly: CollectionBehavior(DisableTestParallelization = true)]` to fix the pre-existing
  parallel-fixture race described above.

## Manual smoke still owed

Open the LoRA Viewer, go straight to "Browse Civitai" without typing anything or touching a
filter — grid should populate and the Installed badge/Hide Installed filter should work
immediately, no Refresh click needed. Also verify switching away and back doesn't re-trigger a
network search (Unified Console should show the new "Deferred initial load starting" line
exactly once for the session, filterable by category Network / source CivitaiBrowser).
