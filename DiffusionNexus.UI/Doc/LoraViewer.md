# LoRA Viewer — Architecture & Data Flow

## Overview

The LoRA Viewer is the primary UI module for browsing, managing, and enriching locally stored LoRA model files. It presents models as a tile grid, groups multiple versions of the same Civitai model into a single tile, and provides a detail panel for inspecting all versions (local + remote).

---

## 1. High-Level Architecture

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         LoraViewerView.axaml                           │
│  ┌──────────┐  ┌───────────────────────────┐  ┌──────────────────────┐ │
│  │ Toolbar  │  │ ScrollViewer > WrapPanel  │  │ ModelDetailView      │ │
│  │ Search   │  │  ┌──────┐ ┌──────┐       │  │ (overlay, right)     │ │
│  │ Filters  │  │  │ Tile │ │ Tile │  ...  │  │ Version tabs         │ │
│  │ Actions  │  │  └──────┘ └──────┘       │  │ Download button      │ │
│  └──────────┘  └───────────────────────────┘  └──────────────────────┘ │
└─────────────────────────────────────────────────────────────────────────┘
         │                      │                         │
         ▼                      ▼                         ▼
  LoraViewerViewModel    ModelTileViewModel      ModelDetailViewModel
         │                      │                         │
         ▼                      ▼                         ▼
  ┌─────────────────────────────────────────────────────────────────────┐
  │                        Service Layer                                │
  │  ModelFileSyncService    ICivitaiClient    IAppSettingsService      │
  └─────────────────────────────────────────────────────────────────────┘
         │                      │
         ▼                      ▼
  ┌──────────────┐    ┌──────────────────┐
  │ Diffusion_   │    │ Civitai REST API │
  │ Nexus-core.db│    │ /api/v1/...      │
  └──────────────┘    └──────────────────┘
```

---

## 2. Database Entity Hierarchy

The DB schema mirrors the Civitai API structure exactly:

```
Model (= Civitai page, e.g. civitai.com/models/3036)
├── CivitaiId            — Civitai model page ID (unique per DB row, nullable)
├── CivitaiModelPageId   — Civitai model page ID (grouping key, NOT unique — 
│                           multiple Model rows can share this value)
├── Name, Description, Type, IsNsfw, Source, Creator, Tags, ...
│
├── ModelVersion (= one release, e.g. "v2 - For 2.1")
│   ├── CivitaiId        — Civitai model version ID (e.g. 9857)
│   ├── Name, BaseModel, BaseModelRaw, TriggerWords, DownloadUrl, ...
│   │
│   ├── ModelFile (= one downloadable file within a version)
│   │   ├── CivitaiId    — Civitai file ID (e.g. 9500)
│   │   ├── FileName, LocalPath, Format, Precision, Hashes, SizeKB, ...
│   │   └── IsPrimary    — whether this is the main file for the version
│   │
│   └── ModelImage (= preview image/video)
│       ├── CivitaiId, Url, BlurHash, Width, Height, ...
│       ├── ThumbnailData — cached BLOB for instant display on next startup
│       └── Prompt, NegativePrompt, Seed, Steps, ... (generation params)
│
└── ModelTag → Tag (many-to-many)
```

### Key distinctions

| Entity | Maps to | Example |
|--------|---------|---------|
| **Model** | A Civitai **page** (model card) | "CharTurner" |
| **ModelVersion** | A **release** within that page | "V2 - For 2.1", "V1 - For 1.5" |
| **ModelFile** | A specific **downloadable file** within a release | `charturner_v2.safetensors` (SafeTensor) vs `charturner_v2.ckpt` (Pickle) |

For LoRAs, most versions have **1 file**. Checkpoints commonly have 2-4 (different formats/precisions).

### `CivitaiId` vs `CivitaiModelPageId` on the Model table

| Column | Purpose | Uniqueness |
|--------|---------|------------|
| `CivitaiId` | Direct Civitai model ID. Intended as a **unique** identifier per DB row. | Unique (or null) |
| `CivitaiModelPageId` | **Grouping key**. Set on ALL Model rows that belong to the same Civitai page so `TileGroupingHelper` can merge them into one tile. | NOT unique — multiple rows may share the same value |

In most cases both hold the same integer (the Civitai page ID). The split exists because `ModelFileSyncService.DiscoverNewFilesAsync` creates **one Model row per local file** (it has no Civitai data yet), and `CivitaiModelPageId` is later populated to group them.

### `DataSource` enum (the `Source` column)

| Value | Meaning |
|-------|---------|
| `Unknown` | Default / not set |
| `LocalFile` | Discovered by scanning local folders (no Civitai data yet) |
| `CivitaiApi` | Created or enriched from a Civitai API call |
| `Manual` | Manually added by the user |

### Update-availability fields on `Model`

| Column | Purpose |
|--------|---------|
| `TotalVersionCount` | Number of versions that exist on Civitai for this model page, captured during the most recent sync. Default `0`. |
| `LastCheckedForUpdatesUtc` | UTC timestamp of the last successful Civitai check. `null` = never checked — the "more versions available" badge is hidden in this case. |

These are populated **for free** by the sync pipeline's `CivitaiMetadataApplier` when a model is identified (no extra HTTP request — the version list is already in the `/api/v1/models/{id}` response).

The "+N more versions" tile badge is computed as
`max(TotalVersionCount − ownedLocalVersionsForSameCivitaiPage, 0)`. It is intentionally informational only: with this minimal schema the app does **not** distinguish a "newer version exists" badge from a generic "other versions exist" badge — adding a true *Update available* badge would require also storing the latest version id / publish date.

---

## 3. Data Flow — Startup / Refresh

When the user opens the LoRA Viewer or clicks **Refresh**, `LoraViewerViewModel.RefreshAsync` executes:

```
RefreshAsync
│
├── 1. DiscoverNewFilesAsync (background thread)
│   │   Calls ModelFileSyncService.DiscoverNewFilesAsync:
│   │   ├── Gets enabled LoRA source folders from IAppSettingsService
│   │   ├── Scans folders for .safetensors / .pt / .ckpt / .pth files
│   │   ├── Filters out files already in DB (by LocalPath)
│   │   ├── For each new file:
│   │   │   ├── TryMatchByHashAndSize → if a DB record exists with same hash
│   │   │   │   but invalid path (file was moved), update the path
│   │   │   └── Otherwise → CreateModelFromFile:
│   │   │       Creates Model + ModelVersion + ModelFile with:
│   │   │         Source = LocalFile
│   │   │         Name = filename (no extension)
│   │   │         BaseModelRaw = "???" (unknown without metadata)
│   │   └── SaveChangesAsync
│   │
├── 2. BackfillCivitaiModelPageIdAsync
│   │   Fixes up grouping for models synced before CivitaiModelPageId existed:
│   │   ├── Step 1: Copy CivitaiId → CivitaiModelPageId where missing
│   │   └── Step 2: Propagate by name (case-insensitive) to siblings
│   │
├── 3. LoadCachedModelsAsync
│   │   Calls ModelFileSyncService.LoadCachedModelsAsync →
│   │   IModelRepository.GetModelsWithLocalFilesAsync
│   │   Returns all Model entities with full navigation graph
│   │   (Versions, Files, Images, TriggerWords, Creator, Tags)
│   │
├── 4. GroupModelsIntoTiles (via TileGroupingHelper)
│   │   Phase 1: Group by CivitaiModelPageId (preferred)
│   │   Phase 2: Group remaining by Name (case-insensitive fallback)
│   │   Within each group: deduplicate by primary filename, keep richest data
│   │   Output: List<ModelTileViewModel>
│   │
├── 5. UI thread: populate AllTiles, subscribe events, apply filters
│   │
└── 6. VerifyFilesInBackgroundAsync (fire-and-forget, low priority)
        Uses scoped IModelSyncService to avoid DbContext conflicts
        Checks File.Exists for every ModelFile.LocalPath
        Tries to find moved files by hash+size match
```

### Fallbacks at each stage

| Stage | Failure | Fallback |
|-------|---------|----------|
| Source folders not configured | No files discovered | Empty state shown: "Add LoRA source folders in Settings" |
| Folder doesn't exist on disk | Skipped with progress message | Other folders still scanned |
| File already in DB | Skipped (dedup by LocalPath) | — |
| File was moved (old path invalid) | `TryMatchByHashAndSize` matches by SHA256 + file size | Path updated in-place |
| No Civitai data yet | `Source = LocalFile`, `BaseModelRaw = "???"` | Tile shows filename, "???" badge |
| DB load fails | Exception caught, SyncStatus shows error | — |
| Verification finds missing file | Scans all source folders for same filename + hash | `IsLocalFileValid = false` if not found |

---

## 4. Data Flow — "Download Metadata" (Library Sync)

The viewer owns none of this logic. The toolbar button and the detail panel's per-LoRA
button both drive `ILibrarySyncService` (`DiffusionNexus.Service/Services/Sync/`), which
plans a run, executes it step by step and records **per-model state** so a second run only
does what is genuinely outstanding. `LoraViewerViewModel` just starts the run, shows
progress, and rebuilds its grid once at the end.

```
DownloadMissingMetadataAsync                      (both service calls run on Task.Run —
│                                                  SQLite is synchronous, and the planning
├── PlanAsync(SyncScope.Library, SyncOptions.All)   pass + the folder scan would otherwise
│     → SyncPlan: one SyncPlanStep per step         freeze the overlay and the Cancel button)
│       (Kind, Count, EstimatedDuration, Description)
│     → plan.HasWork == false ⇒ "Library is up to date — nothing to do", no run.
│       Only reachable for an option set WITHOUT DiscoverFiles: a plan carrying the
│       discovery step always reports work, because nobody knows what a scan will
│       find until it has run.
│     (Plan B puts a confirmation dialog here; for now the plan is logged and started)
│
├── ExecuteAsync(plan, progress, ct)
│     progress → status bar: "{Label} [{index}/{total}] {currentItem}"
│     steps run in registration order — a file must be discovered before it can be
│     identified, and only an identified model has the ids tags/images need
│
├── RebuildTilesFromDatabaseAsync()   ← exactly once, after the run
│
└── SyncStatus:
      report.NewFilesDiscovered == 0 && every step Planned == 0
        ⇒ "Library is up to date — nothing to do"   ← the honest verdict, from the report
      otherwise report.Summary (+ " · N failed" when report.Failures is non-empty)
        (+ " · N items failed unexpectedly (see log)" when report.UnexpectedFailures > 0)
```

### The steps

| Step | What it does |
|------|--------------|
| `DiscoverFiles` | Walks every enabled LoRA source folder and inserts rows for files that are not in the DB yet. It ignores the scope — a folder- or model-scoped run that *includes* this step still scans everything — but it is only in the run at all when the caller asks for it, and the per-LoRA button does not. |
| `IdentifyModel` | The identity chain for one file: full-file SHA256 → Civitai `GET /model-versions/by-hash/{sha}` → on 404, the local `.civitai.info` / `.json` sidecar. Writes name, base model, trigger words, Civitai ids, image records — see *user edits* and *what counts as applied* below. |
| `FetchTags` | For a model that has a Civitai id but no tags yet: `GET /models/{id}` and replace the tag set (reusing existing `Tag` rows by normalized name). |
| `FetchImages` | For a version with a Civitai version id but no image records: `GET /model-versions/{id}` and persist the returned images. |
| `Thumbnails` | Reserved. Not implemented yet (Plan B). Until it lands, tiles download their own preview when they scroll into view (`ModelTileViewModel.Activate()`), so nothing is missing on screen — only the bulk pre-fetch is gone. |

Civitai requests are paced ~1.5 s apart — per **request**, not per item, by the singleton
`ICivitaiRequestPacer` awaited immediately before every call. One item is not one request: the
images step calls once per *version* and identify calls twice (hash lookup, model page), so pacing
between items left those bursts unpaced. The pacer measures from the last call, so the first
request of a run never waits. Cancellation is cooperative: a cancelled run still reports what it
completed, because those stamps are already committed.

**User edits are never overwritten.** A model the user has edited (`Model.IsUserEdited`) is not
even offered to a bulk identify run — nothing upstream is more authoritative than what the user
typed. A *forced* run (the per-LoRA button, or an explicit id scope) does select it, because that
is the user asking; what protects them there is the appliers, which decide "may I write this
text?" in one place each (`CanWriteModelText` / `CanWriteVersionText`) and reference it from every
write site, all three sidecar formats included. Model name, description and tags hang off
`Model.IsUserEdited`; version name, description, trigger words **and base model** off
`ModelVersion.IsUserEdited` — the detail view lets the user pick a base model and stamps the
version as edited, so a refresh that rewrote it undid that choice. Every write of `BaseModelRaw`
also writes the `BaseModel` enum (`BaseModelTypeExtensions.ParseCivitai`, the same helper the
editor uses), so the viewer's base-model filter and the label the detail view shows never disagree.
Facts nobody authored locally — Civitai ids, download URL, file hashes, images, NSFW — are applied
either way.

**One bug does not cost the run.** An exception no step claimed used to escape to the caller: the
tally of everything already synced was thrown away and the user saw a raw exception message where
the report should have been. Such an item is now failed, logged at Error with the exception, and
counted in `SyncReport.UnexpectedFailures` / `FirstUnexpectedError` so the status line says so out
loud. A real cancellation — `OperationCanceledException` while the run's own token is cancelled —
still unwinds; a `TaskCanceledException` carrying somebody else's token is a timeout, not the user.

**What counts as applied.** A sidecar is `Sidecar` only when metadata actually came out of it. A
`.json` next to a LoRA is as often a kohya training config as it is metadata, and a `.civitai.info`
can be truncated; either way the outcome is `NotIdentified`, `Source`/`LastSyncedAt` are left
alone, and the file's real signature is still stored — so it is not read again until it changes (or
the 30-day window comes round), rather than costing a re-hash and a Civitai request on every run.

### Where the state lives

One `ModelSyncStates` row per model (PK = FK to `Model`), so *"checked and genuinely
empty"* is distinguishable from *"never checked"* — the distinction the old
`LastSyncedAt`-only flag could not express:

| Field | Meaning |
|-------|---------|
| `MetadataOutcome` | `None`, `Matched`, `Sidecar`, `Header`, `Heuristic`, `NotIdentified`, `Error` |
| `MetadataCheckedAt` / `MetadataAttempts` | When identity was last attempted, and how many consecutive failures |
| `LastError` | One-line reason for the last failure (never a stack trace) |
| `TagsCheckedAt` / `ImagesCheckedAt` | Stamped **even when the result was empty** — that is what makes "no tags" final |
| `SidecarSignature` | `{path}|{lastWriteUtcTicks}|{length}` of the sidecar last **looked at**, so an unchanged sidecar is not re-read and a changed one is. Recorded whether or not anything was applied. `""` = looked, no sidecar; `null` = never recorded |
| `HeaderCheckedAt` | Safetensors header read (WP4) |

Models that predate the table get a row derived from data already in the database
(`SyncStateDeriver`, via `SyncStateInitializer`) on the first plan — never by calling
the network.

**Checked-and-empty is final.** A model whose tags were fetched and came back empty is
never re-fetched; only an explicit Force re-asks.

### Retry windows (`SyncRetryPolicy.Default`)

| Stored outcome | Re-checked |
|----------------|-----------|
| `Matched` | Never (only Force) |
| `NotIdentified`, `Sidecar`, `Header`, `Heuristic` | After 30 days — a better source may have appeared |
| `Error` | After 1 day, at most 3 consecutive attempts |
| `None` / no row | Immediately |
| Tags / images already stamped | Never (only Force) |

What counts as an answer worth stamping, for the tags and images steps:

| Civitai's reply | Recorded as |
|-----------------|-------------|
| Data, or an empty list | Checked (stamped) |
| 404 (`GetAsync` returns null) | Checked (stamped), item skipped |
| 401/403 and the rest of 4xx except 429 | Checked (stamped), item skipped — a refusal is the same refusal tomorrow |
| 429, 5xx, connection/TLS failure, timeout | Not stamped — the item returns on the next run |

A rejected database write (`DbUpdateException`, its unit-of-work translation, or a change-tracker
conflict) is a fault of **one item**, not of the run: the tracker is dropped, identify stamps
`Error` and the fetch steps stamp nothing, and the run carries on. Before that it aborted the whole
sync, so every model queued behind the bad one went unchecked.

### Forcing a re-check

`SyncOptions` carries `ForceIdentify`, `ForceTags`, `ForceImages`, `ForceThumbnails`.
A forced step ignores the stored verdict and the retry window. The per-LoRA button in the
detail panel is exactly this: `DownloadMetadataForTileAsync` plans
`SyncScope.ForModels(modelId)` with `IdentifyModel + FetchTags + FetchImages` and
`ForceIdentify: true` — same service, same steps, one model — then re-reads that model and
refreshes the tile.

Forcing also widens *selection*, not just due-ness: `SelectIdentifyCandidatesAsync` takes an
`includeMatched` flag (`ForceIdentify || scope.Kind == Models`) that drops the
"no `CivitaiId` yet" predicate, and an explicit id scope additionally drops the LoRA-family
type filter. Without that, a forced run over an already-matched model planned zero items and
the detail panel reported "No metadata found on Civitai for this file." about a model Civitai
knows — for most of the library.

The method returns `TileMetadataSyncResult(Applied, Report)`: `Applied` (any step succeeded)
tells the detail view to reload; `IdentifyPlanned` is what separates the two failure
wordings — "No metadata found on Civitai for this file." when the step ran and found nothing,
"Nothing to refresh for this model." when it planned nothing and therefore asked nobody.

### Fallbacks

| Situation | Fallback |
|-----------|----------|
| No API key configured | Requests still work (public models), but at a lower rate limit |
| Hash lookup returns 404 | Sidecar is tried; outcome recorded as `Sidecar` when metadata came out of it, `NotIdentified` otherwise (unreadable, or not metadata at all), re-checked after 30 days |
| Hash returns a version but no images | The `FetchImages` step covers it via the version endpoint |
| Network/disk failure on one item | Recorded as a `SyncFailure` (step, model, reason) and counted in the report; the run continues |
| `CivitaiId` already owned by another DB row | Only `CivitaiModelPageId` is set (grouping still works), warning logged |
| A second run started while one is going | `ExecuteAsync` throws immediately — the service is single-flight process-wide |
| Service not registered | Button reports "Library sync not available." |

---

## 5. Data Flow — Download New Version (Detail Panel)

When the user clicks **Download** on a not-yet-downloaded version tab in the detail panel:

```
ModelDetailViewModel.DownloadSelectedVersionAsync
│
├── Resolve download URL from CivitaiModelVersion.Files[primary].DownloadUrl
├── Show destination folder dialog (IDialogService.ShowDownloadLoraVersionDialogAsync)
│   Lists enabled LoRA source folders
│
├── DownloadFileAsync (background thread, with ITaskTracker progress):
│   ├── Try unauthenticated GET first (public models)
│   ├── On 401/403 → retry with ?token={apiKey} (early access models)
│   ├── Stream to .tmp file with 80KB buffer, report progress
│   ├── Rename .tmp → final on completion
│   │
│   └── PersistDownloadedModelAsync:
│       ├── Resolve model page ID:
│       │   1. Fetch full CivitaiModel via GetModelAsync (if ModelId > 0)
│       │   2. Use civitaiModel.Id as authoritative page ID
│       │   3. Fallback: civitaiVersion.ModelId (may be 0 for nested versions)
│       │
│       ├── Check if Model with same CivitaiModelPageId already exists in DB
│       │   ├── YES → add version to existing model (proper grouping)
│       │   └── NO  → create new Model entity
│       │
│       ├── Create ModelVersion + ModelFile + TriggerWords + Images
│       ├── Create Tags (only for new models)
│       └── SaveChangesAsync
│
└── Finally: tab.IsDownloading = false (UI thread)
```

### Fallbacks

| Situation | Fallback |
|-----------|----------|
| `civitaiVersion.ModelId` is 0 | Uses `GetModelVersionAsync` result's `modelId` if available |
| Full model fetch fails | Creates Model without description/tags/license (can be enriched later via "Download Metadata") |
| File already tracked in DB | Skipped (dedup by LocalPath) |
| Download cancelled | Temp file cleaned up |
| DB persist fails | File stays on disk; next `DiscoverNewFilesAsync` will pick it up |

---

## 6. Class Responsibilities

### ViewModels

| Class | Responsibility |
|-------|---------------|
| **`LoraViewerViewModel`** | Top-level orchestrator. Owns `AllTiles` and `FilteredTiles` collections. Coordinates refresh (discover → backfill → load → group → display). Starts a library sync through `ILibrarySyncService` and shows its plan / progress / report (§4) — it owns no sync logic itself. Manages detail panel lifecycle. Handles filtering (search text, NSFW toggle, base model multi-select). |
| **`ModelTileViewModel`** | Represents one tile in the grid. May group multiple `Model` entities (same Civitai page). Manages version buttons, thumbnail loading (image + video), clipboard operations, "Open on Civitai", "Open Folder", deletion (single + multi-version picker). Factory methods: `FromModel`, `FromModelGroup`. |
| **`ModelDetailViewModel`** | Right-side detail panel. Shows all versions (local = blue, remote = yellow tabs). Fetches full version list from Civitai API. Handles downloading new versions with progress. Manages `PersistDownloadedModelAsync` for DB persistence after download. |
| **`CivitaiVersionTabItem`** | One version tab in the detail panel. Wraps `CivitaiModelVersion` (API data) + optional `ModelVersion` (local data). `IsDownloaded` = has local version. |
| **`VersionButtonViewModel`** | One version toggle button on a tile. Short label derived from `BaseModelRaw` mapping (e.g., "XL", "Pony 🐎", "F.1D"). Tooltip shows full version name + filename. |
| **`BaseModelFilterItem`** | One item in the base model filter flyout. Fires `SelectionChanged` event when toggled. |
| **`DownloadLoraVersionDialogViewModel`** | Dialog for choosing download destination folder + confirming download. |

### Services

| Class | Responsibility |
|-------|---------------|
| **`ModelFileSyncService`** (`IModelSyncService`) | Database-first sync engine. `LoadCachedModelsAsync`: fast path for cached data. `DiscoverNewFilesAsync`: scans folders, creates stub Model entities for new files, detects moved files by hash. `VerifyAndSyncFilesAsync`: background verification of file existence. |
| **`LibrarySyncService`** (`ILibrarySyncService`) | The metadata sync pipeline (§4). `PlanAsync` reports what a run would do; `ExecuteAsync` runs the steps under a process-wide single-flight gate, stamping `ModelSyncStates` as it goes. Steps: `DiscoverFilesStep`, `IdentifyModelStep`, `FetchTagsStep`, `FetchImagesStep`; persistence lives in `CivitaiMetadataApplier` / `SidecarMetadataApplier`. |
| **`CivitaiClient`** (`ICivitaiClient`) | HTTP client for Civitai REST API. `GetModelAsync`: full model with all versions. `GetModelVersionAsync`: single version by ID. `GetModelVersionByHashAsync`: version lookup by file hash. Handles auth headers, JSON deserialization. |
| **`IAppSettingsService`** | Provides configured LoRA source folder paths, API key storage, and general app settings. |
| **`ISecureStorage`** | Encrypts/decrypts the Civitai API key (stored as `EncryptedCivitaiApiKey` in settings). |
| **`IVideoThumbnailService`** | Extracts a mid-frame from video previews using FFmpeg. Returns WebP thumbnail bytes. |
| **`IDialogService`** | Shows confirmation dialogs, version pickers, download destination dialogs. |
| **`ITaskTracker`** | Unified progress tracking for background tasks (shown in status bar). |

### Helpers

| Class | Responsibility |
|-------|---------------|
| **`TileGroupingHelper`** | Pure-logic helper (no DI). Groups `Model` entities into `ModelTileViewModel` tiles. Phase 1: group by `CivitaiModelPageId`. Phase 2: group remaining by `Name` (case-insensitive). Deduplicates re-discovery duplicates within each group by primary filename. |
| **`HtmlTextHelper`** | Converts Civitai HTML descriptions to plain text for display in the detail panel. |
| **`BaseModelTypeExtensions`** | Parses Civitai base model strings (e.g., "SDXL 1.0") to the `BaseModelType` enum. Convention-based `Enum.TryParse` — no hardcoded mapping to maintain. |

---

## 7. UI Layout (LoraViewerView.axaml)

```
Grid (3 rows: Auto, *, Auto)
│
├── Row 0: Toolbar
│   ├── Left:   Search TextBox, NSFW CheckBox, Reset Button
│   ├── Center: "X of Y models" counter
│   └── Right:  Refresh, Download Metadata, Scan Duplicates, Base Model Filter Flyout
│
├── Row 1: Main Content (Grid overlay)
│   ├── Background: ScrollViewer > StackPanel > ItemsControl (WrapPanel)
│   │   Each tile = ModelTileControl (250px wide, 6px margin)
│   │   Bottom spacer (40px) for status bar clearance
│   │
│   ├── Empty State: "No Models Found — Add LoRA source folders in Settings"
│   ├── Loading Overlay: ProgressBar + BusyMessage
│   │
│   └── Detail Panel (overlay, HorizontalAlignment=Right, Width=624px)
│       ModelDetailView, shown when IsDetailOpen = true
│
└── Row 2: Status Bar (SyncStatus text, auto-hides when empty)
```

---

## 8. Filtering Pipeline

`LoraViewerViewModel.ApplyFilters` runs whenever search text, NSFW toggle, or base model selection changes:

```
AllTiles
  │
  ├── Search filter (OR across DisplayName, FileName, CreatorName)
  ├── NSFW filter (hide IsNsfw tiles unless ShowNsfw is checked)
  └── Base model filter (multi-select, OR logic across version BaseModelRaw values)
  │
  └── → FilteredTiles (displayed in the WrapPanel)
```

`RebuildAvailableBaseModels` scans all versions across all tiles to build the distinct base model list. Previous selections are preserved when the list is rebuilt.

### Base Model flyout (Installed tab)

The toolbar's self-labeled **Base Model** button (same control style on both tabs — no caption above) opens a `Flyout` with `ShowMode="Standard"` — it stays open while multi-selecting, dismisses on outside click / Esc, and moves keyboard focus into the flyout so the search box is immediately typeable. Both tabs have the in-flyout search; only the Installed tab has the only-installed checkbox and the Unknown entry (searching online makes neither meaningful there). The Installed flyout renders `FlyoutBaseModels`, a composed view rebuilt by `RebuildFlyoutBaseModels`:

- **"Unknown" entry first** — the `UnknownBaseModelItem` sentinel matches tiles whose base model is the `"???"` placeholder (files discovered without metadata). It is owned by the Installed tab only and is never added to `AvailableBaseModels`, which the Civitai browser mirrors and sends to the Civitai API.
- **Search box** (`BaseModelFilterSearchText`) — narrows the visible option list case-insensitively; selections are untouched.
- **"Only models I have installed" checkbox** (`OnlyInstalledBaseModels`, off by default) — hides catalog-only entries; Unknown stays visible only when placeholder tiles exist.
- The composed view reuses the shared `BaseModelFilterItem` instances, so selection state stays single-sourced across the flyout, the filter pipeline, and the browser mirror.
- **Selected items are pinned visible** even when the search text or the only-installed toggle would hide them — otherwise an active filter could become un-toggleable. The installed set is cached (`_installedBaseModels`) and refreshed on tile changes, not per keystroke.
- Installed base models missing from the Civitai catalog (renamed/dropped labels like "Krea 2", hand-edited sidecars) are union-appended, and the Browse Civitai mirror renders the **same full list — single source of truth**. This is safe because Civitai's API tolerates unknown `baseModels` values (200 + zero items; verified live 2026-08, and "Krea 2" is in fact a valid API value despite being absent from the scraped constants). The browser has its own `FlyoutBaseModels` + `BaseModelFilterSearchText` (search + pinning only).

**Clear all** clears every selection including Unknown; **Reset** additionally clears the flyout search text and the only-installed checkbox.

### Saved filter

The toolbar's **Save filter** button (`SaveFilterCommand`) serializes the current selections + Unknown flag + only-installed toggle (`LoraViewerFilterData`) into `AppSettings.LoraViewerFilterJson`. On startup, `InitializeBaseModelFilterAsync` loads the catalog first, then restores the saved filter (`RestoreSavedFilterAsync`), REPLACING the current selection in one batch (single filter pass). Saved names the list doesn't contain yet (stale/offline catalog) are held in a pending set that the next `RebuildAvailableBaseModels` reconciles, and `CaptureFilter` includes them so re-saving never truncates the saved intent; corrupt JSON degrades silently to the unfiltered default. Save/restore resolve a fresh scoped `IAppSettingsService` (via `UseSettingsServiceAsync`) instead of the constructor-shared instance, so they never race the other startup tasks on one DbContext. Single slot — saving overwrites the previous filter.

---

## 9. Thumbnail Pipeline

```
LoadThumbnailFromVersion (called when SelectedVersion changes)
│
├── Path 1: BLOB cached in DB (ModelImage.ThumbnailData)
│   → Instant: new Bitmap(stream)
│
├── Path 2: No BLOB but has URL (Civitai image URL)
│   → Fire-and-forget DownloadThumbnailAsync:
│       ├── Image: GET {url}/width=300 → bytes → BLOB to DB → Bitmap
│       └── Video: GET {url} → temp file → FFmpeg mid-frame → WebP → BLOB to DB → Bitmap
│
└── Path 3: No image data at all
    → ThumbnailImage = null → ShowPlaceholder = true
```

---

## 10. Detail Panel Flow

```
User clicks tile → ModelTileViewModel.OpenDetails()
  → raises DetailRequested event
  → LoraViewerViewModel.OnTileDetailRequested → OpenDetailAsync(tile)
    → creates new ModelDetailViewModel
    → detailVm.LoadAsync(tile):
        1. PopulateFromLocalVersion(tile)  — instant, from DB data
           ├── ModelIdDisplay, VersionIdDisplay, BaseModel, FileName
           ├── Description (HTML→text), TriggerWords, Tags
           └── BuildLocalVersionTabs (blue tabs from local versions)
        
        2. FetchCivitaiDataAsync(tile)     — async, from API
           ├── Requires Model.CivitaiId or CivitaiModelPageId > 0
           ├── GET /api/v1/models/{modelId}
           └── BuildCivitaiVersionTabs:
               ├── Merges API versions with local versions
               ├── Local match by CivitaiId, fallback by Name
               ├── Downloaded versions = blue tabs
               └── Remote-only versions = yellow tabs

User clicks yellow tab → Download button enabled
  → DownloadSelectedVersionAsync → DownloadFileAsync → PersistDownloadedModelAsync
```

### Detail panel fallbacks

| Situation | Behavior |
|-----------|----------|
| No `CivitaiId` on model | Shows "No Civitai ID — run 'Download Metadata' first" |
| API fetch fails | Shows error in StatusMessage, local tabs remain usable |
| Version has no `modelId` (0) | `PersistDownloadedModelAsync` fetches via `GetModelVersionAsync` to discover the page ID |
| Model already exists in DB | New version added to existing model (proper grouping) |

---

## 11. Event Wiring

```
LoraViewerViewModel
  ├── tile.Deleted       → OnTileDeleted       → remove from AllTiles + FilteredTiles
  ├── tile.DetailRequested → OnTileDetailRequested → OpenDetailAsync
  └── detailVm.CloseRequested → OnDetailCloseRequested → CloseDetail

ModelTileViewModel
  ├── VersionButton.SelectCommand → OnVersionButtonSelected → SelectedVersion = ...
  └── Delete → dialog → ExecuteDeletion → Deleted event (if fully deleted)

BaseModelFilterItem
  └── SelectionChanged → OnBaseModelFilterChanged → ApplyFilters
```

---

## 12. Known Design Gaps

1. **Duplicate Model rows per file**: `ModelFileSyncService.DiscoverNewFilesAsync` creates one `Model` entity per local file. If two files belong to the same Civitai model, they become separate DB rows. `TileGroupingHelper` papers over this at the view layer by grouping on `CivitaiModelPageId` / `Name`, but the DB has redundant Model entities.

2. **`CivitaiId` is redundant with `CivitaiModelPageId`** on the Model table: both store the Civitai page ID. The distinction (unique vs non-unique) exists only because of gap #1. If the sync service consolidated into one Model per Civitai page, a single column would suffice.

3. **No version-level CivitaiId → model page ID lookup in FetchCivitaiDataAsync**: If a model has no `CivitaiId`/`CivitaiModelPageId` but its version has a `CivitaiId`, the detail panel could call `GET /api/v1/model-versions/{versionId}` to discover the `modelId` and then fetch the full model. Currently it shows "No Civitai ID" instead.

---

## 13. LoRA Sorter tab

The third tab in the LoRA Viewer reorganizes installed LoRA files on disk into a clean folder hierarchy by base model and optionally by category, with a live preview before any files are touched.

### What it does

The Sorter takes the LoRAs the app already knows about (the same set as the Installed tab), computes a target folder layout, displays it as an expandable tree preview, and — only after the user clicks **Start Sorting** — moves or copies each LoRA **together with its sidecar files** (`.civitai.info`, `.json`, `.preview.*`, `.txt`, video previews — the same set `StaticFileTypes.GeneralExtensions` counts as part of a model) into that layout. The database is updated in move mode so the library remains current; copy mode keeps the DB pointing at the originals.

### Options

| Option | Choices | Default | Notes |
|--------|---------|---------|-------|
| **Source folder** | One enabled LoRA source, or any folder via Browse | Favorite source, else first | Arbitrary folders trigger metadata resolution on the fly via hash lookup. |
| **Target folder** | "Same as source" or any picked folder | Same as source | If target lies in a different registered LoRA source, a warning alerts that colliding sources can lead to unpredictable outcomes. |
| **Folder structure** | Base model only · Base model + category | Base model + category | Categories inferred from tags by `SorterCategoryResolver`, the one helper the download pipeline also uses. Unknown base model → `Unknown\` folder; an unresolved **category** adds no segment at all, exactly as the downloader omits it — otherwise sorting and downloading would move the same files back and forth forever. |
| **Operation** | Move · Copy | Move | Move shows a warning that old folder structure cannot be restored automatically. Copy into the source root itself is blocked (would re-import on next scan). |
| **Delete empty source dirs** | on/off | off | Move mode only; triggered after the run completes. |

### Collision policy — automatic, no dialogs

Generic filenames like `V1.safetensors` from different models can collide once sorted into the same base model + category folder. The Sorter detects these collisions during preview and resolves them automatically:

1. **Different content, same target name → deterministic auto-rename**: files are suffixed with their Civitai version ID (the downloader's convention), or `_2`, `_3`, etc. for files without one. **Every** candidate name is content-compared before the next one is tried — the plain name, the version-suffixed one and each numeric fallback alike — so a copy-mode re-run recognises its own earlier copy wherever it landed and transfers nothing, instead of growing `_2`, `_3`, `_4`… on every run.
   A file that cannot be read (locked by a running backend) counts as different content: it is renamed around, never overwritten.
2. **Identical content, same target → skip the second copy** and report it. The summary points to the existing Find Duplicates tool for cleanup.

**No overwrite occurs.** Renamed files take their sidecars with them and their DB row (move mode) gets the new path. If two DB rows pointed at the same file (a historic deduplication edge), the file moves once and both rows are updated.

### Disk-space pre-flight

The tab computes required vs. available space before allowing the sort to begin:

- **Copy operation:** required = total size of all files and sidecars being copied.
- **Move, same drive:** required ≈ 0 (in-place renames).
- **Move, cross-drive:** required = total size of files whose source and target are on different drives (worst-case copy-then-delete).

If `free < required + 1 GB` safety margin, the available-space bar turns red and **Start Sorting** is disabled.

### Sort history manifest

Every run writes a full plan to `%LocalAppData%\DiffusionNexus\SortHistory\{timestamp}.json` — one record per file with old path → new path, operation, sizes, and each sidecar's source/target. Completion is journalled separately, one appended line per finished file, into `{timestamp}.completed.jsonl`: appending is O(1) and a killed run costs at most the last line, whereas rewriting the plan file per file was O(n²) and left truncated JSON. This enables a future "Restore previous structure" UI (not v1). If the history directory cannot be written the run continues without a restore point.

### Metadata cache

Files not yet in the database (when browsing arbitrary folders) are resolved via:

1. Local `.civitai.info` sidecar next to the file (also the source of the tags used for category inference).
2. Per-hash disk cache (below).
3. Civitai hash lookup API call (same as the sync pipeline), followed by a `/models/{id}` call for the owning model's **tags** — the by-hash response is a model *version* and carries none, so without this second call every sidecar-less file would sort without a category. A failed tag call is non-fatal: the base model and version id are kept and cached, and the tag list is left unresolved so a later pass retries it (an empty-but-resolved list is never re-fetched).

DB rows are matched by path earlier, when the cached library is loaded; there is no by-hash DB lookup (descoped from the spec). A file that cannot be hashed or an API shape change resolves as unknown rather than failing the pass, and the API key is read once per pass, not once per file.

Downloaded metadata is cached in `%LocalAppData%\DiffusionNexus\SorterCache\{sha256}.json` (file name always lower-cased, so the store survived the switch to the library-wide uppercase `FileHasher.Sha256Upper`) so a re-run or re-preview of the same file normally costs no network call at all. One exception is deliberate: an entry whose tag lookup never succeeded is stored as *unresolved* rather than as "this model has no tags", so the next pass retries it — that is what stops one transient Civitai failure from leaving a file category-less forever. Within a single pass the tag lookup is also memoized per model id, so a folder full of versions of the same model costs one `/models/{id}` call, not one per file. The cache is a lookup cache only — the DB is never polluted with unregistered folders.

### Execution

1. **Confirm step** summarizes: operation, file count, total size, target root, how many files will auto-rename or skip as duplicates.
2. **Per-file move/copy** (sequential, cancellable):
   - Gather the file set (model file + all same-directory sidecars).
   - Create target directories and move/copy via `IFileOperations`.
   - **Move mode:** update `ModelFile.LocalPath` in batched DB writes.
   - **Copy mode:** DB untouched.
3. **Logging:** every step logs to the Unified Console (`LogCategory.FileSystem`, source `"LoraSorter"`) **with elapsed timings**, so an exported log can tell "slow" from "hung" by which step last succeeded: candidate resolution (known/unknown/skipped counts, plus a heartbeat every 50 resolved files), plan summary, sort start, each DB batch, and the final tally. A run whose history file could not be written still completes and says so — there is simply no restore point for it.
4. **Cancellation / partial failure:** cancelling a *preview* keeps the tree on screen and marks it possibly stale (Start is disarmed until a Refresh rebuilds it) rather than blanking it. Cancelling a *run*: already-moved files stay (their DB rows are updated — the library remains consistent). The run stops at the current file and reports tally. A locked or inaccessible file is skipped and logged, not fatal.
5. **After completion:** result summary in status bar; the Installed tab's cached tiles refresh so paths shown are current.

### Known limitations (v1)

- No custom tag→folder mapping rules (legacy `CustomTagMapXmlService` could be revived in v2).
- Sorting is LoRA-family only, matching the Viewer scope.
- The Restore UI ships as a follow-up (manifest data is written from day one).
- Editing categories must be done in the model detail view (`UserCategory`), not within the Sorter itself.
- Directory junctions and symlinks under the source folder are **not** followed — the enumeration skips reparse points, which is the cycle guard (a junction pointing at itself or an ancestor would otherwise enumerate forever). A LoRA reachable only through one is not sorted. Reparse points are the *only* attribute the walk skips: hidden and system files are enumerated normally, since a model file can carry either bit after a restore, a NAS copy, or a sync client's folder marking.
- The tab does its first disk walk when the **Sorter tab is opened**, not when the LoRA Viewer is opened: initialization triggers from `OnAttachedToVisualTree` only.
- When the target has no `DriveInfo` but the folder exists (a UNC share), the free-space gate reports "Free space unknown" and lets the run proceed instead of blocking it with no stated reason. A target that cannot be *reached* — an unmapped or not-ready drive letter, a denied or missing folder — blocks the run instead, since failing open there only moves the failure into the run, where it costs every file.

### Detailed specification

For full design details, see `docs/superpowers/specs/2026-08-20-lora-sorter-design.md`.
