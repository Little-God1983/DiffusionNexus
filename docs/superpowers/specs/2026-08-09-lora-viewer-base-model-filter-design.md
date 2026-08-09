# LoRA Viewer — Base Model Filter Rework (Installed Tab)

**Date:** 2026-08-09
**Status:** Approved by user (design + scope choices confirmed interactively)

## Goal

Make the Installed tab's base-model filter usable for large libraries:
it must stay open while multi-selecting, be searchable, be able to hide
base models the user doesn't own, cover files with unknown base model,
and be saveable so the filter is restored when the module opens.

## Requirements (as confirmed)

1. Rename the filter to **"Base Model"**, matching the Browse Civitai tab's
   layout (caption above the button).
2. The flyout must **stay open until the user clicks outside it** (or Esc) —
   replace `ShowMode="TransientWithDismissOnPointerMoveAway"` with
   `Transient`. Apply the same change to the Browse Civitai tab's identical
   flyout for consistency.
3. A **search box inside the flyout** that narrows the base-model list as the
   user types. Independent of the toolbar model search.
4. A **checkbox, off by default: "Only models I have installed"** — when on,
   the option list shrinks to base models actually present among installed
   LoRAs.
5. An **"Unknown"** filter entry matching tiles whose base model is the
   `"???"` placeholder (i.e. `LoraViewerViewModel.IsPlaceholderBaseModel`:
   null/whitespace/`"???"`). Today such tiles can never be matched by any
   selection. When "only installed" is on, Unknown appears only if such
   files exist in the library.
6. Toolbar order: model search box → Base Model filter → **"Save filter"**
   button. Saving persists the base-model filter state; it is loaded and
   applied automatically when the LoRA Viewer opens. Single slot, no named
   presets. Search text, NSFW toggle and sort remain session-only.

## Architecture

### Flyout option list: composed view, shared source untouched

`LoraViewerViewModel.AvailableBaseModels` is shared with
`CivitaiBrowserViewModel` (passed into its ctor; the browser mirrors it and
sends selections to the Civitai API, where "Unknown" would be invalid).
Therefore:

- The shared `AvailableBaseModels` collection is **not** filtered in place
  and never receives the Unknown sentinel.
- The Installed flyout's `ItemsControl` binds to a new
  `FlyoutBaseModels` collection on `LoraViewerViewModel`, rebuilt from:
  `AvailableBaseModels` (+ an `Unknown` sentinel `BaseModelFilterItem`),
  filtered by the flyout search text and the only-installed checkbox.
- Selection state stays on the underlying shared `BaseModelFilterItem`
  instances so the existing `ApplyFilters()` pipeline, active-count badge,
  Clear all, and the browser mirror keep working unchanged. The Unknown
  sentinel is owned by the viewer VM only.

New VM members (names indicative):

- `string? BaseModelFilterSearchText` — flyout search box text.
- `bool OnlyInstalledBaseModels` — checkbox state (default `false`).
- `BaseModelFilterItem UnknownBaseModelItem` — sentinel; display text
  "Unknown".
- `ObservableCollection<BaseModelFilterItem> FlyoutBaseModels` — the
  composed, filtered view.
- `RebuildFlyoutBaseModels()` — recomputes the view; triggered by changes to
  the search text, the checkbox, `AvailableBaseModels` rebuilds, and tile
  reloads (installed-set changes).

"Installed" base models = distinct non-placeholder `BaseModelRaw` values
across `AllTiles[*].Versions`, compared case-insensitively (same comparison
`ApplyFilters` already uses).

### Filter predicate extension

`ApplyFilters()` currently matches a tile when any version's `BaseModelRaw`
is in the active set. Extension: when the Unknown sentinel is selected, a
tile also matches when any of its versions has a placeholder base model
(`IsPlaceholderBaseModel`). Clear all clears every selection including the
sentinel; Reset additionally clears the flyout search text and the
only-installed checkbox.

### Persistence

New nullable string column `AppSettings.LoraViewerFilterJson` (precedent:
`DistillerRuleSetsJson`), with one EF Core migration in
`DiffusionNexus.DataAccess\Migrations\Core\`. `schema.sql` is not touched —
the `DistillerRuleSetsJson` precedent (commit `af40cef`) changed only the
entity + migration; migrations apply automatically at app start.

JSON payload (versionless, tolerant deserialize):

```json
{
  "selectedBaseModels": ["SDXL 1.0", "Illustrious"],
  "includeUnknown": true,
  "onlyInstalled": false
}
```

- **Save:** "Save filter" toolbar button → serialize on UI thread → scoped
  `IUnitOfWork` write (same pattern as
  `BatchMetadataDistillerViewModel.SaveRuleSetsJsonAsync`). Fire-and-forget
  with non-fatal catch + log; brief visual confirmation on the button
  (e.g. checkmark) is optional polish.
- **Load:** during module open (`RefreshAsync`), after tiles and the
  base-model list are first built, deserialize and apply selections with a
  `_suppressFilterSave`-style guard. Selection is applied by raw name;
  `RebuildAvailableBaseModels()` already preserves selection by name across
  catalog rebuilds, so a later catalog refresh does not wipe the restored
  state. Saved names not present in the current list are ignored silently.
- Corrupt/unreadable JSON → ignore, log, behave as unfiltered.

## UI changes (`LoraViewerView.axaml`)

- Replace the `Filter` label + button with the Browse Civitai pattern:
  `TextBlock "Base Model"` caption above the button, red indicator +
  active-count badge unchanged.
- Flyout body becomes: search `TextBox` (watermark "Search base models…"),
  `CheckBox "Only models I have installed"`, `Clear all` (existing,
  visibility unchanged), then the `ScrollViewer`/`ItemsControl` bound to
  `FlyoutBaseModels`.
- `ShowMode="Transient"` here and in `CivitaiBrowserView.axaml`.
- New toolbar `Button "Save filter"` after the filter button, bound to
  `SaveFilterCommand`.

## Error handling

- Settings write failures are logged and non-fatal (viewer keeps working).
- Restore failures (missing column data, bad JSON, unknown names) degrade
  to the unfiltered default silently.
- The Unknown sentinel must never be handed to the browser mirror or the
  Civitai query builder (guaranteed structurally: it is never added to the
  shared collection).

## Testing

VM-level unit tests (existing patterns in
`DiffusionNexus.Tests\Viewer\`):

1. Flyout search narrows `FlyoutBaseModels` (case-insensitive, substring).
2. Only-installed checkbox reduces the list to models present in
   `AllTiles`; Unknown included only when placeholder tiles exist.
3. Selecting Unknown matches `"???"`/null tiles in `ApplyFilters`.
4. Save serializes the expected JSON; load restores selection, checkbox and
   Unknown; unknown saved names are ignored.
5. Restore survives a subsequent `RebuildAvailableBaseModels()`.
6. Clear all / Reset clears sentinel + checkbox + flyout search.

Manual GUI smoke afterwards: open module → saved filter applied; flyout
stays open while toggling; browser tab's filter list unaffected by the
checkbox.

## Out of scope

- Named/multiple filter presets.
- Persisting search text, NSFW, sort.
- Any change to the Civitai query semantics on the Browse tab.
