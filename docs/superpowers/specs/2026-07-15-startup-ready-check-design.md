# Startup Ready-Check Screen — Design

**Date:** 2026-07-15
**Branch / PR:** `feature/ui-performance-improvements` → PR #410 (user chose: everything into this PR)
**Status:** Approved by user (design conversation 2026-07-15)

## Problem

After the perf-plan restructure, the window appears in ~2 s, but live testing and a CPU
trace of a cold Release launch showed two UI-thread blockades that make startup feel worse,
not better:

1. **`RegisterModules` builds all 8 module VM graphs + XAML views in one ~3.6 s synchronous
   UI-thread block.** The "Starting DiffusionNexus…" overlay freezes mid-animation, then the
   whole UI "refreshes" at once.
2. **`LoraViewerViewModel.VerifyFilesInBackgroundAsync` is background in name only.** Its
   async continuations resume on the UI dispatcher; the trace shows **10.1 s of UI-thread
   time inside `ModelFileSyncService.TryFindMovedFileAsync`, 7.3 s of it hashing model files
   (`ComputeFileHashAsync`)**. The app is SHA-hashing multi-GB safetensors on the UI thread
   right after modules appear — the "can't click anywhere for 10+ seconds" hang.

Secondary: the autocomplete trie build burns ~22 s of background CPU (Hunspell
suffix-expansion `Check()` calls) during startup, competing for cores while everything else
warms up.

The user's core requirement: **the app must never hang with nothing to show.** Duration is
secondary; visible progress and a responsive UI are primary.

## Goals

- No UI-thread block longer than ~1 frame during startup (the animation must never freeze).
- A startup screen that shows an animated ready-check list, semi-transparent so the real app
  is visible behind it, vanishing automatically when the app is genuinely usable.
- **Hard acceptance criterion (user): the UI is responsive at the moment the screen
  vanishes** — nothing heavy may remain queued on the dispatcher.

## Non-Goals

- Real health-check logic (checks are a live progress display of actual startup phases; a
  phase that throws shows ✗ and the app continues degraded — no new validation code).
- Gating the screen on network work (update checks) or on the model-file verify.
- Optimizing Hunspell/trie internals (only scheduling changes).
- Lazy per-module construction on first navigation (out of scope per the original perf plan).

## Design

### 1. De-blocking prerequisites

**1a. Chunked module registration.** `RegisterModules` becomes an async sequence: each
module's (ViewModel + View) construction runs in its own `DispatcherPriority.Background`
tick (`await Dispatcher.UIThread.InvokeAsync(BuildModuleN, Background)` or equivalent). Frames
render and input pumps between builds. Total build time is unchanged (~3.6 s) but the UI
breathes throughout. Registration order and everything each registration does today
(including firing `LoadStartupDataAsync` at the end) is preserved.

**1b. File verify truly off-thread.** The body of the LoRA viewer's
`VerifyFilesInBackgroundAsync` work (`ModelFileSyncService.VerifyAndSyncFilesAsync` call)
moves inside `Task.Run(...)` with a fresh DI scope — the same pattern `RefreshAsync` already
uses for its scan. Only final VM property updates marshal back via the dispatcher. This
deletes hang #2 outright.

**1c. Trie taming.** `AutoCompleteService`'s dictionary load keeps its own thread but runs at
`ThreadPriority.BelowNormal`, and its start is deferred until the ready gate has fired
(readiness signal from the progress service; a plain `TaskCompletionSource` hook — the
service must not reference Avalonia). Autocomplete suggestions simply become available a few
seconds later; `GetSuggestions`/`RecordWord` already tolerate the not-yet-loaded state.

### 2. Progress model — `StartupProgressService`

Plain C# (no Avalonia types), registered as a singleton, unit-testable:

- Ordered, fixed list of `StartupCheck` items created at startup:
  `Database` → one item per module in registration order (display names: Installer Manager,
  LoRA Dataset Helper, LoRA Viewer, Generation Gallery, Image Comparer, Workflows, Settings)
  → `Diffusion Engine` → `Updates`.
  (Module items are created from the same table `RegisterModules` iterates — the list can
  never drift from the real module registry. Hidden modules still get built, so they still
  get an item.)
- Item states: `Pending → Running → Done | Failed`. State changes raise an event
  (`CheckChanged`) the overlay ViewModel subscribes to; all raises happen on the UI thread
  (callers are UI-thread phases) so no marshaling is needed in the VM.
- `Diffusion Engine` flips `Running → Done` when the module whose construction performs the
  local-diffusion backend/LLama warm-up finishes building (the LLama native-library load
  happens inside module construction today — the item completes together with that module's
  build step, identified in the implementation plan by where `LocalDiffusionBackendProvider`
  is first resolved). When `DiffusionFeatureFlags.UseLocalDiffusionBackend` is off, the item
  completes immediately as Done (plain ✓, no special "skipped" styling).
- `Updates` maps to the installer-manager update checks; it may still be Running when the
  screen vanishes (non-gating) and completes invisibly — results surface exactly as today
  (status-bar/update badges).
- A phase that throws marks its item `Failed` (✗, tooltip carries the message), logs to the
  activity log, and startup continues — mirrors today's catch-and-continue behavior.

### 3. The screen

- Replaces the current "Starting DiffusionNexus…" StackPanel in the same `Panel` slot of
  `DiffusionNexusMainWindow.axaml` (module host wrap). The dim Border stays
  (`#80000000` — semi-transparent per user requirement; the app is visible building up
  behind it) and continues to block input while visible.
- Centered card: one row per check — item name + state glyph. Running item shows the
  existing small spinner idiom (indeterminate `ProgressBar` or rotating glyph — reuse the
  app's busy styling); Done flips to an animated ✓ (simple opacity/scale transition,
  Avalonia `Transitions` — no custom animation framework).
- Overlay ViewModel: `StartupOverlayViewModel` (list of row VMs mirroring the service).
  Bound with the same compiled-binding conventions as the rest of the window.

**Vanish gate (the hard criterion):** the screen fades out only when
1. `Database`, all module items, and `Diffusion Engine` are Done/Failed, **and**
2. a **dispatcher-drain sentinel** has run: after (1), post a no-op at
   `DispatcherPriority.Background`; when it executes, the queue at-and-above Background is
   provably drained — nothing heavy is left. Then set `IsStartupComplete = true` (existing
   overlay dismissal flag) and fade (brief opacity transition).
`Updates`, the file verify, and the trie never gate the fade.

### 4. Failure & edge behavior

- Any phase exception: item ✗, activity-log error, startup continues; the gate treats
  Failed as terminal so the screen still vanishes.
- Deferred-startup catastrophic failure (existing `CompleteStartupAsync` catch): marks all
  remaining items Failed, gate fires, screen vanishes — same degraded-but-alive shell as
  today, but now the user can see *which* step died.
- Window closed during startup: existing shutdown path unchanged; overlay needs no special
  handling (it dies with the window).

### 5. Verification / acceptance

- Unit tests: `StartupProgressService` state machine (ordering, gate readiness with
  Failed items, Updates non-gating); chunked-registration helper if extracted as a pure
  sequence utility. No Avalonia bootstrapping in tests (repo constraint).
- App run (controller-verified, PrintWindow screenshots): multiple distinct frames during
  startup showing the checklist progressing (proves animation isn't frozen);
- **Responsiveness proof:** injected click (PostMessage) within 500 ms after the overlay
  vanishes must land (e.g. nav toggle acts) — verifies the drain-sentinel gate.
- Cold-start check: `LoadStartupData:`/phase instrumentation lines show no synchronous
  UI-thread head > ~250 ms; no silent multi-second gaps attributable to the UI thread.
- Full suite green; all commits into PR #410.

## Out of scope (explicit)

- Hunspell/trie algorithmic optimization; per-module lazy construction; real health checks;
  making Updates or file-verify gating; skip/cancel affordances on the startup screen.
