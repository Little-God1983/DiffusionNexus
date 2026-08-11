# Lora Dataset Helper — General Refresh Button

**Date:** 2026-08-11
**Status:** Approved design, first slice scoped to Dataset Management
**Branch:** `feature/lora-dataset-helper-refresh`

## Goal

Add a single "↻ Refresh" button to the Lora Dataset Helper module shell that
refreshes the **currently open tab**. The pattern (interface + shell dispatch)
must be reusable so other modules can adopt the same button later.

This first slice implements the button, the dispatch mechanism, and the
**Dataset Management** tab's refresh. The other tabs (Image Edit, Captioning,
Batch Crop/Scale, Batch Upscale) are follow-up slices; until a tab implements
the interface, the button is disabled while that tab is active.

Existing inline refresh buttons anywhere in the module are **not** touched.

## UI

- One button in `DiffusionNexus.UI/Views/LoraDatasetHelperView.axaml`,
  top-right on the same grid row as the `TabControl` (tab headers are
  left-aligned; the right side of the strip is free).
- Visual: ↻ glyph (`&#x21BB;`) + "Refresh" label, matching the LoRA Viewer
  toolbar button (`LoraViewerView.axaml:194-202`). Tooltip: "Refresh the
  current tab".
- Disabled while a refresh is running, or while the active tab is busy, or
  while the active tab does not (yet) support refresh.
- The shell status bar (`StatusMessage`) shows progress/result
  ("Refreshing…" → "Refreshed." or the error message).

## Mechanism

New interface in `DiffusionNexus.UI` (module-agnostic, reusable):

```csharp
public interface IRefreshableTab
{
    /// <summary>False while the tab is busy and must not be refreshed.</summary>
    bool CanRefresh { get; }

    /// <summary>Reload the tab's current view from its underlying source.</summary>
    Task RefreshAsync();
}
```

`LoraDatasetHelperViewModel` (shell coordinator):

- `RefreshCurrentTabCommand` (`AsyncRelayCommand`), `CanExecute` =
  active tab is `IRefreshableTab` **and** its `CanRefresh` is true **and**
  no refresh is already in flight.
- Maps `SelectedTabIndex` → the corresponding tab ViewModel property
  (0=DatasetManagement, 1=ImageEdit, 2=Captioning, 3=BatchCropScale,
  4=BatchUpscale), casts to `IRefreshableTab`, awaits `RefreshAsync()`.
- Re-evaluates `CanExecute` when `SelectedTabIndex` changes.
- Exceptions are caught and surfaced via `StatusMessage`; the button
  re-enables afterwards.

No tab-specific logic lives in the shell.

## Dataset Management refresh semantics (this slice)

`DatasetManagementViewModel : IRefreshableTab`:

- **Dataset list view** (no dataset open): re-run the existing load path
  (`LoadDatasetsAsync` via `CheckStorageConfigurationAsync`) — re-scans the
  storage folders from disk, rebuilds groups, re-applies the current filter.
  This tab's view *is* the dataset list, so its refresh is the rescan.
- **Dataset open** (`IsViewingDataset`): refresh the open dataset instead —
  existing `RefreshActiveDatasetAsync()` (reloads the dataset's media) and
  re-push context to `DatasetQualityTab` as `OpenDatasetAsync` already does.
- `CanRefresh` is false while `IsLoading` (the existing guard) — refresh is a
  no-op rather than a queued or interrupting action.
- Selection/filter state is preserved where the existing load paths already
  preserve it; no new state-preservation machinery in this slice.

## Follow-up slices (out of scope here, for reference)

| Tab | Planned refresh |
|---|---|
| Image Edit | Reload selected dataset's versions + thumbnails |
| Captioning | Rebuild dataset/version dropdowns, reload image + caption list |
| Batch Crop/Scale | Recompute folder metadata, counts, next version, version list |
| Batch Upscale | Rebuild dataset/version dropdowns |

## Logging

Per the project's standing rule, refresh steps log to the Unified Console at
trace/debug level: refresh requested (which tab), which path taken
(list rescan vs. active dataset), and completion/failure.

## Testing

- Shell: `RefreshCurrentTabCommand` dispatches to the active tab's
  `RefreshAsync`; disabled when the active tab lacks `IRefreshableTab`,
  when `CanRefresh` is false, and while a refresh is in flight.
- `DatasetManagementViewModel`: refresh in list view triggers the disk
  rescan path; refresh with a dataset open triggers
  `RefreshActiveDatasetAsync`; no-op while `IsLoading`.
- Existing test conventions in `DiffusionNexus.Tests/LoraDatasetHelper/`
  (no Avalonia global init).
