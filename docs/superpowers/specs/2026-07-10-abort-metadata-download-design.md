# Abort Metadata Download in the LoRA Viewer — Design

**Date:** 2026-07-10
**Repo:** DiffusionNexus (main app)
**Branch:** feature/abort-metadata

## Problem

In the LoRA viewer, the **Download Metadata** button starts a long, multi-phase
sync against Civitai (hashing every local LoRA, API lookups, image and thumbnail
downloads). Once started there is no way to stop it — the user must wait for the
whole batch to finish. We need a way to abort the sync after it has started.

## Current behavior

- `DownloadMissingMetadataCommand` → `DownloadMissingMetadataAsync`
  ([LoraViewerViewModel.cs:428](../../../DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs))
  runs six sequential phases, each a loop over tiles doing network + file I/O:
  - Phase 0 — discover new files + rebuild tiles
  - Phase 1 — `SyncMetadataPhaseAsync` (hash + Civitai lookup)
  - Phase 1b — `ReprocessLocalFileModelsPhaseAsync`
  - Phase 2 — `RefetchMissingImagesPhaseAsync`
  - Phase 3 — `BackfillMissingTagsPhaseAsync`
  - Phase 4 — `DownloadMissingThumbnailsPhaseAsync`
- None of these methods accept a `CancellationToken`.
- While the sync runs, `IsBusy = true` shows a full-screen busy overlay (indeterminate
  progress bar + `BusyMessage`) at
  [LoraViewerView.axaml:266](../../../DiffusionNexus.UI/Views/LoraViewerView.axaml).
- The busy overlay is **shared** with `Refresh`, which is *not* cancellable.
- The codebase already uses a cancellation idiom for other work
  (`_updateCheckCts`, `_searchDebounceCts` — `CancellationTokenSource` fields).

## Approach

Cooperative cancellation that stops **after the current model finishes** (chosen over
hard-cancelling in-flight HTTP requests). Abort is non-destructive: metadata already
written to the DB for processed models is kept. This is the standard, safe approach and
matches the existing `_updateCheckCts` pattern.

## Behavior (target)

- Clicking **Download Metadata** starts the sync as today.
- While it runs, the busy overlay shows a **Cancel** button.
- Clicking Cancel requests cancellation; the sync stops cleanly after the in-flight model
  finishes (typically <1s). Already-synced metadata is preserved.
- After the click, the button flips to a "Cancelling…" affordance / disables, signalling
  the request was received.
- Final status reads `Metadata sync cancelled` — distinct from the error branch.

## ViewModel changes — `LoraViewerViewModel`

- Add `private CancellationTokenSource? _metadataSyncCts;`.
- In `DownloadMissingMetadataAsync`:
  - Create `_metadataSyncCts = new CancellationTokenSource()` at the top; capture
    `var ct = _metadataSyncCts.Token`.
  - Set `IsCancellable = true` alongside `IsBusy = true`.
  - Thread `ct` through the phase methods (`SyncMetadataPhaseAsync`,
    `ReprocessLocalFileModelsPhaseAsync`, `RefetchMissingImagesPhaseAsync`,
    `BackfillMissingTagsPhaseAsync`, `DownloadMissingThumbnailsPhaseAsync`) and check
    `ct.ThrowIfCancellationRequested()` at the top of each per-model loop iteration.
    Phase 0 discovery is quick and treated as a single unit.
  - Add a `catch (OperationCanceledException)` branch that sets
    `SyncStatus = "Metadata sync cancelled"` (before the generic `catch (Exception)`).
  - In `finally`: clear `IsBusy`, `IsCancellable`, `BusyMessage`; dispose and null
    `_metadataSyncCts`.
- Add `[ObservableProperty] private bool _isCancellable;` — true only while the metadata
  sync runs. Scopes the Cancel button to this operation so it does not appear during
  `Refresh`.
- Add `[RelayCommand] private void CancelMetadataDownload()` that calls
  `_metadataSyncCts?.Cancel()` and sets `SyncStatus = "Cancelling…"`.

## View changes — `LoraViewerView.axaml`

- Inside the busy overlay `StackPanel` (line 266), below the progress bar, add a **Cancel**
  button bound to `CancelMetadataDownloadCommand` with
  `IsVisible="{Binding IsCancellable}"`. Disable it after first click (or rely on the
  status flipping to "Cancelling…") so the user sees the request was received.

## Testing

- Unit test on the VM: start the sync against a fake `ICivitaiClient` whose lookup blocks
  on a signal, fire `CancelMetadataDownloadCommand`, release the signal, and assert:
  - the sync completes with `SyncStatus == "Metadata sync cancelled"`, and
  - partial results are preserved (models processed before the cancel keep their metadata).
- Follows the existing `LoraViewerViewModel` test setup
  (`DiffusionNexus.Tests/Viewer/`).

## Out of scope

- Cancelling `Refresh`.
- Hard-aborting in-flight HTTP requests (token is not threaded into `ICivitaiClient`).
- Any confirmation dialog before aborting.
