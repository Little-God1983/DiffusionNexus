# LoRA Sorter — Design Spec

**Date:** 2026-08-20 (rev 3, approved by user)
**Module:** LoRA Viewer (new third tab)
**Status:** Approved — implementation plan follows this spec.
**Artifact:** interactive version with wireframe published via Claude artifact (same content).

A third tab in the LoRA Viewer that reorganizes installed LoRA files on disk into a
clean folder hierarchy — by base model, optionally by category — with a live preview
of the resulting structure, a move-or-copy choice, and a disk-space pre-flight check.

## 1. What it does

Users accumulate LoRAs in flat or inconsistently structured folders. The Sorter takes
the LoRAs the app already knows about (the same set the Installed tab shows), computes
a target folder layout, shows it as a preview tree, and — only after the user clicks
Start — moves or copies each LoRA **together with its sidecar files** into that layout,
updating the database so the library stays intact.

- **Structure choice:** `{BaseModel}\` only, or `{BaseModel}\{Category}\`.
- **Operation choice:** move (reorganize in place) or copy (build a sorted duplicate
  elsewhere). Selecting Move shows an inline warning that the old folder structure
  cannot be restored automatically.
- **Any folder as source:** besides the registered LoRA sources, the user can browse
  to any folder on disk; files unknown to the database get their metadata resolved on
  the fly (§3).
- **Preview first:** nothing touches the disk until the user has seen the exact
  resulting tree and pressed Start.
- **Disk-space check:** required space vs. free space on the target drive, computed
  per the chosen operation, blocking Start when insufficient.

## 2. Where it lives

The LoRA Viewer's tabs are plain `TabItem`s in `LoraViewerView.axaml`; the second tab
already follows a child-view-model pattern (`CivitaiBrowserView` bound to
`BrowserViewModel`). The Sorter follows it exactly:

- New `<TabItem Header="LoRA Sorter">` after the Browse Civitai tab, hosting a new
  `LoraSorterView` user control.
- New `LoraSorterViewModel : BusyViewModelBase, IDialogServiceAware`, exposed as a
  `SorterViewModel` property on `LoraViewerViewModel` and constructed in its
  constructor — mirroring how `BrowserViewModel` is wired. Dialog service is forwarded
  in `OnDialogServiceSet()` as other parents do.

## 3. Key decision: drive it from the database, not a fresh disk scan

**Decision — sorting engine.** A dormant legacy engine already exists in the Service
layer (`FileCopyService` + `SelectedOptions` + `ModelClass`, currently referenced only
by tests). It scans the filesystem and infers metadata from sidecar JSON. The rest of
the Viewer, however, is DB-driven: `IModelSyncService.LoadCachedFilesAsync()` yields
`InstalledModelFile(Model, ModelVersion, ModelFile, SourceRoot)` with `BaseModelRaw`
and richer category data than sidecars can provide.

**The Sorter uses the DB graph as its source of truth** — same data the Installed tab
renders, so the preview always matches what the user sees there. We reuse the proven
low-level primitives from the legacy stack (`ExtractBaseName`/sidecar grouping
semantics, `DiskUtility`, `FolderNode` tree shape) but not `FileCopyService` itself,
which stays untouched.

### Category resolution — same logic as the downloader

The Civitai download pipeline already sorts fresh downloads into
`{BaseModel}\{Category}` folders. The Sorter must land files in **exactly the same
places**, so the planner reuses the download pipeline's category/folder logic,
extracted into one shared helper (today it exists as two half-implementations:
`MetaDataUtilService.GetCategoryFromTags` in the Service layer,
`CivitaiResultViewModel.InferCategoryFromTags` in the UI):

```csharp
CivitaiCategory ResolveCategory(Model m)
    => m.UserCategory              // explicit user override wins
       ?? InferFromTags(m.Tags)    // identical tag inference the downloader uses
       ?? CivitaiCategory.Unknown; // → "Unknown" folder
```

The full `CivitaiCategory` enum is used; folders are created only for categories that
actually occur, plus an `Unknown` bucket.

Base model comes from `ModelVersion.BaseModelRaw`; the un-synced placeholder `"???"`
and null/empty map to an `Unknown` folder. Folder names are sanitized for invalid
path characters.

### Files the database doesn't know (arbitrary source folders)

Since any folder on disk can be a source, the plan can contain files with no DB row.
Metadata for those is resolved per file, cheapest first:

1. **DB by path**, then **DB by SHA256 hash** (file already known under another path).
2. **Local sidecars** — `.civitai.info` / `.json` next to the file (existing parsers
   from the Installed tab's local-metadata fallback).
3. **Civitai hash lookup** — `by-hash` API call, exactly what the sync flow does.
   Downloaded responses are cached in
   `%LocalAppData%\DiffusionNexus\SorterCache\{sha256}.json` so a re-run or re-preview
   never hits the network twice for the same file. The cache is a lookup cache, not a
   library import — the DB is not polluted with folders the user never registered.
4. Still unresolved → `Unknown\Unknown`; counted and shown in the preview footer.

Hashing and API lookups make preview for an unknown folder a progress-reported,
cancellable step (unlike the instant DB-only path).

## 4. UI layout

The tab opens with the headline **"Sort your LoRAs"** and a one-line subtitle
("Reorganize your LoRA library into clean folders by base model and category.").
Below it: left options rail, dominant right pane titled **"Folder structure preview"**,
status bar — the same structural grammar as the rest of the Viewer (toolbar/status
borders `#1E1E1E`, borders `#333`, accent `#4CAF50`, warning `#FFA726`, danger
`#FF6B6B`, inline-hex styling with local `Classes`, Unicode-glyph buttons, busy
overlay with cancel). When *Move* is selected, an inline warning appears under the
operation choice: the old folder structure cannot be restored automatically.

```
┌ Installed ┊ Browse Civitai ┊ [LoRA Sorter] ─────────────────────────────────┐
│ Sort your LoRAs                                                             │
│ Reorganize your LoRA library into clean folders by base model and category. │
├──────────────────────────┬──────────────────────────────────────────────────┤
│ SOURCE FOLDER            │ FOLDER STRUCTURE PREVIEW                         │
│ [E:\AI\Loras (favorite)▾]│ E:\AI\Loras\                                     │
│  (LoRA sources or Browse)│ ├─ Illustrious        96 LoRAs · 41.2 GB         │
│ TARGET FOLDER            │ │   ├─ Character      44 LoRAs · 19.0 GB         │
│ [Same as source      …]  │ │   ├─ Style          37 LoRAs · 15.8 GB         │
│ FOLDER STRUCTURE         │ │   └─ Concept        15 LoRAs ·  6.4 GB         │
│  ○ Base model only       │ ├─ SDXL 1.0           61 LoRAs · 27.9 GB         │
│  ● Base model + category │ ├─ Flux.2 Klein       38 LoRAs · 30.4 GB         │
│ OPERATION                │ └─ Unknown            19 LoRAs ·  7.7 GB         │
│  ● Move (reorganize)     │                                                  │
│  ○ Copy (duplicate)      │ ✓ 198 will move · 16 already in place            │
│ ⚠ Move rearranges files… │ ✎ 2 auto-renamed · 1 duplicate skipped           │
│ ☐ Delete empty src dirs  │                                                  │
│ Required 0 B / Free 412GB│                                                  │
│ [▶ Start Sorting (214)]  │                                                  │
├──────────────────────────┴──────────────────────────────────────────────────┤
│ Preview computed from 214 cached LoRAs · last library refresh 12 min ago    │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Preview tree

No `TreeView` exists anywhere in the UI project yet. Rather than introduce and style
Avalonia's `TreeView` for one screen, the preview renders `FolderNode`-shaped view
models through nested `ItemsControl`s with indentation and an expander toggle. Each
node shows name, LoRA count, and aggregate size; expanding a node lists its files,
including auto-renames and skipped duplicates. Files already at their computed
destination are shown dimmed and excluded from the operation.

The preview recomputes automatically whenever an option changes (structure, target,
operation) — a pure in-memory computation over the cached list for DB-known sources.

## 5. Options in detail

| Option | Choices | Default | Notes |
|---|---|---|---|
| **Source** | One enabled LoRA source folder (from `GetEnabledLoraSourcesAsync`), or any folder on disk via Browse… | Favorite source, else first | One source per run. Arbitrary folders trigger the metadata-resolution chain from §3. |
| **Target** | "Same as source" or any picked folder | Same as source | Picked via existing `ShowOpenFolderDialogAsync`. If the target lies inside a *different* enabled LoRA source, warn that two colliding LoRA sources can lead to unpredictable outcomes (duplicate imports on the next scan). |
| **Structure** | Base model only · Base model + category | Base model + category | Category level is opt-out. |
| **Operation** | Move · Copy | Move | Move shows the cannot-be-restored warning. Copy into the source root itself is blocked (would create duplicates the next scan re-imports). |
| **Delete empty source folders** | on/off | off | Move mode only; `DiskUtility.DeleteEmptyDirectoriesAsync` after the run. |

## 6. Disk-space pre-flight

Computed from the plan (exact file sizes incl. sidecars, from disk at preview time),
using `DiskUtility.GetAvailableSpace`:

- **Copy:** required = total size of all planned files.
- **Move, same volume:** required ≈ 0 (renames). Shown as such.
- **Move, cross-volume:** required = total size of files whose source and target roots
  are on different drives (worst case, since each file is copy-then-delete).

The panel shows required vs. free with a bar. If `free < required + 1 GB` safety
margin, the figure turns red (`#FF6B6B`) and Start is disabled with the reason.

## 7. Execution

1. **Confirm.** `ShowConfirmAsync` summarizing: operation, file count, total size,
   target root, and how many files will be auto-renamed or skipped as duplicates. No
   per-file conflict dialog — collision handling is fully automatic (§7.1).
2. **Per LoRA** (sequential, cancellable):
   - Gather the file set: the model file plus every same-directory sidecar sharing its
     base name (`.civitai.info`, `.json`, `.preview.*`, `.txt`, … — the established
     sidecar extension set).
   - Create the target directory; move/copy via `IFileOperations` (its `MoveFile`
     already falls back to copy+delete across volumes).
   - **Move mode:** update `ModelFile.LocalPath` (and `LocalFileVerifiedAt`) in a
     fresh unit-of-work scope, batched per ~20 files. **Copy mode:** DB untouched; the
     library keeps pointing at the originals.
3. **Progress & logging.** Runs under `RunBusyAsync` (busy overlay + cancel) and an
   `ITaskTracker.BeginTask` handle for the global status bar. Standing project rule:
   every step logs to the Unified Console — `IUnifiedLogger`,
   `LogCategory.FileSystem`, source `"LoraSorter"` — plan summary, each file's
   source→target, each DB batch, final tally.
4. **Cancellation / failure:** already-completed moves stay (their DB rows are already
   updated — the library remains consistent); the run stops at the current file and
   reports moved/skipped/failed counts. A failed single file (locked, access denied)
   is logged and skipped, not fatal.
5. **Sort history manifest (v1, every run):** before the first file is touched, the
   full plan is written to `%LocalAppData%\DiffusionNexus\SortHistory\{timestamp}.json`
   — one record per file: old path → new path, operation, sizes. Each completed file
   is flagged in the manifest as it finishes. This enables the later "Restore previous
   structure" feature (the Restore UI itself is a follow-up, not v1).
6. **Afterwards:** result summary in the status bar; the Installed tab's cached tiles
   are refreshed so paths shown are current.

### 7.1 Collision policy — automatic, no dialogs (decided 2026-08-20)

Generic names like `V1.safetensors` from different models collide once flattened into
the same `BaseModel\Category` folder (this exact failure once overwrote a model in the
download pipeline). The planner detects intra-plan and target-exists collisions during
preview and classifies them by content — stored DB hashes for known files, lazy SHA256
only for colliding candidates — then resolves them with two automatic rules:

1. **Different content, same target name → deterministic auto-rename**: suffix with
   the Civitai version id (the downloader's existing convention), `_2`/`_3` fallback
   for files without one. Deterministic names make re-runs idempotent — a second pass
   computes the same targets, finds them in place, and skips.
2. **Identical content, same target → skip the second copy** and count it; the summary
   points to the existing Find Duplicates tool for cleanup.

**No overwrite path exists anywhere.** Renamed files take their sidecars with them
(a `.civitai.info` that stops sharing the base name goes blind) and their DB row gets
the renamed path. **One DB path, one file** — if two DB rows point at the same path
(historic dedup edge), the file moves once and both rows are updated. **Files outside
the source root** are simply not part of the plan.

## 8. Testing

- **Planner unit tests** (pure, no disk): structure option → expected paths;
  `"???"`/null base model → Unknown; category fallback chain; sanitization; collision
  classification (rename vs. skip); deterministic rename suffixes; already-in-place
  detection; re-run idempotence.
- **Disk-space calculator tests:** copy vs. same-volume move vs. cross-volume move.
- **Executor tests** against a temp directory with fake sidecars: sidecars travel with
  the model file (including on rename); DB row updated on move, untouched on copy;
  manifest written and per-file completion flagged; cancellation mid-run leaves
  consistent state; skip-on-locked-file.
- **Manual GUI smoke** (owed): real folder with a handful of LoRAs, both structure
  modes, move and copy, cancel mid-run, arbitrary (non-source) folder.

## 9. Decisions from review (2026-08-20)

| Question | Decision |
|---|---|
| Scope of a run | One source folder at a time — but the source can be **any folder on disk**, not only registered LoRA sources. |
| Category granularity | Full `CivitaiCategory` enum, **same logic as the downloader**, so sorted folders and downloads land in identical places. |
| Copy/move into another scanned folder | Warn explicitly that colliding LoRA sources can lead to **unpredictable outcomes**; allow after the warning. |
| Model type subfolders | No. |
| Filename collisions | Automatic: deterministic version-id rename for different content, skip-and-report for identical content. No dialog, no overwrite. |
| Downloaded metadata | Per-hash lookup cache in `%LocalAppData%\DiffusionNexus\SorterCache\` (not temp, not the DB, not sidecars). |
| Sort history | **Manifest written on every run in v1**; Restore UI ships as a follow-up. Move warning stays as-is since restore is best-effort. |

## 10. Out of scope (v1)

- Custom tag→folder mapping rules (legacy `CustomTagMapXmlService` could be revived as v2).
- Sorting checkpoints/embeddings/other model types — LoRA family only, same as the Viewer.
- The Restore UI (manifest data for it is written from day one).
- Editing categories from within the Sorter — that stays in the model detail view (`UserCategory`).

## Files touched (estimate)

| Area | Files |
|---|---|
| New UI | `Views\LoraSorterView.axaml(.cs)` · `ViewModels\LoraSorterViewModel.cs` · small node/plan view models |
| New service | `Services\Lora\LoraSortPlanner.cs` (pure planning) · `Services\Lora\LoraSortExecutor.cs` (disk + DB) · shared `CategoryResolver` · metadata resolver + per-hash cache · sort-history manifest writer |
| Edited | `Views\LoraViewerView.axaml` (new TabItem) · `ViewModels\LoraViewerViewModel.cs` (child VM property + ctor wiring) · DI registration in `App.axaml.cs` |
| Tests | Planner, disk-space, executor suites under `Tests\` |
