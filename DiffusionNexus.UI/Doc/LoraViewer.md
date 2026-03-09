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

## 4. Data Flow — "Download Metadata" (Civitai Enrichment)

`LoraViewerViewModel.DownloadMissingMetadataAsync` runs in 3 phases:

### Phase 1: Sync metadata for unsynced models

```
For each tile where Model.CivitaiId == null AND Model.LastSyncedAt == null:
│
├── Get primary file's LocalPath
├── Compute full-file SHA256 hash (entire file, not partial)
├── Call CivitaiClient.GetModelVersionByHashAsync(hash)
│   │   Endpoint: GET /api/v1/model-versions/by-hash/{sha256}
│   │   Returns: CivitaiModelVersion (includes modelId, versionId, files, etc.)
│   │
│   ├── NOT FOUND (404):
│   │   Model is not on Civitai (custom LoRA, private, etc.)
│   │   → Mark LastSyncedAt = now (so it's not retried)
│   │
│   └── FOUND:
│       ├── Fetch full model: GET /api/v1/models/{modelId}
│       │   Returns: CivitaiModel (all versions, images, tags, creator, description)
│       │
│       └── UpdateModelFromCivitaiAsync:
│           ├── Set CivitaiModelPageId = civitaiModel.Id (grouping key)
│           ├── Set CivitaiId (only if no other DB row owns it — UNIQUE constraint)
│           ├── Update: Name, Description, IsNsfw, Creator, Tags, License fields
│           ├── Update matched version: CivitaiId, BaseModelRaw, TriggerWords, Images, Hashes
│           └── SaveChangesAsync → refresh tile on UI thread
│
├── Rate limit: 1.5s delay between requests
```

### Phase 2: Re-fetch missing images

```
For tiles where CivitaiId is set but Images array is empty:
│   (This happens when the hash-lookup returned no images in the response)
│
├── Call GetModelVersionAsync(versionCivitaiId)
│   Endpoint: GET /api/v1/model-versions/{id}
│   Usually returns the images that were missing from the hash response
│
└── UpdateModelFromCivitaiAsync (same as Phase 1)
```

### Phase 3: Download missing thumbnails

```
For tiles where ThumbnailImage is null but an image URL exists:
│
├── Image preview:
│   ├── Append /width=300 to Civitai URL (server-side resize)
│   ├── Download bytes → store in ModelImage.ThumbnailData (BLOB)
│   └── Persist BLOB to DB for instant display on next startup
│
└── Video preview (.mp4, .webm):
    ├── Download full video to temp file
    ├── Extract mid-frame using FFmpeg (via IVideoThumbnailService)
    ├── Store extracted frame as WebP thumbnail BLOB
    └── Clean up temp files
```

### Fallbacks

| Situation | Fallback |
|-----------|----------|
| No API key configured | Requests still work (public models), but lower rate limit |
| Hash lookup returns 404 | Model marked as synced (not retried), shows filename only |
| Hash lookup returns version but no images | Phase 2 re-fetches via version endpoint |
| Image download fails | Tile shows "No Preview" placeholder |
| Video preview but no FFmpeg | Warning logged, no thumbnail generated |
| CivitaiId already owned by another DB row | Only CivitaiModelPageId set (grouping still works), warning logged |

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
| **`LoraViewerViewModel`** | Top-level orchestrator. Owns `AllTiles` and `FilteredTiles` collections. Coordinates refresh (discover → backfill → load → group → display). Drives "Download Metadata" (3-phase Civitai sync). Manages detail panel lifecycle. Handles filtering (search text, NSFW toggle, base model multi-select). |
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
