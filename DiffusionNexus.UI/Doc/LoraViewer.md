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
├── RebuildTilesFromDatabaseAsync()   ← exactly once, after the run — including after a
│                                       CANCELLED one: those models are already committed,
│                                       and the run's (signalled) token is deliberately not
│                                       passed here, or the rebuild would be skipped and the
│                                       report thrown away with it
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
| `IdentifyModel` | The identity chain for one file: full-file SHA256 → Civitai `GET /model-versions/by-hash/{sha}` → on 404, the local `.civitai.info` / `.json` sidecar → the file's own safetensors header → a guess from the filename. Writes name, base model, trigger words, Civitai ids, image records — see *user edits*, *what counts as applied* and **The identity chain** below. |
| `FetchTags` | For a model that has a Civitai id but no tags yet: `GET /models/{id}` and replace the tag set (reusing existing `Tag` rows by normalized name). |
| `FetchImages` | For a version with a Civitai version id but no image records: `GET /model-versions/{id}` and persist the returned images. |
| `Thumbnails` | For each version's due primary image: one CDN GET through `IThumbnailProvider`, never a full video — see §9 for the ladder, the failure/retry table, and the 0-video-bytes-in-bulk guarantee. |

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
editor uses), so the viewer's base-model filter and the label the detail view shows never disagree —
and a *blank* upstream base model is treated as a missing answer rather than an instruction to
forget the stored one (Civitai's `baseModel` is a non-nullable string that defaults to `""`, so an
omitted field and an empty one are the same value).
Facts nobody authored locally — Civitai ids, download URL, file hashes, images, NSFW — are applied
either way.

**One bug does not cost the run.** An exception no step claimed used to escape to the caller: the
tally of everything already synced was thrown away and the user saw a raw exception message where
the report should have been. Such an item is now failed, logged at Error with the exception, and
counted in `SyncReport.UnexpectedFailures` / `FirstUnexpectedError` so the status line says so out
loud. A real cancellation — `OperationCanceledException` while the run's own token is cancelled —
still unwinds; a `TaskCanceledException` carrying somebody else's token is a timeout, not the user.

**Hashes are stored uppercase, by whoever writes them.** Civitai and the sidecars answer in
lowercase; both appliers put `ModelFile.HashSHA256` through `FileHasher.NormalizeSha256` on the way
in, because a single lowercase row breaks SQL equality against every digest the app computes itself
and gives the startup repair pass real work to do forever. That pass
(`DatabaseRecoveryService.NormalizeModelFileHashCasing`) now runs only on a start that applied or
stamped migrations — the only kind on which a migration body can have been skipped — and asks a
read-only `EXISTS` before it writes anything. Only SHA256: the other hash columns carry no such
invariant.

**What counts as applied.** A sidecar is `Sidecar` only when metadata actually came out of it. A
`.json` next to a LoRA is as often a kohya training config as it is metadata, and a `.civitai.info`
can be truncated; either way the outcome is `NotIdentified`, `Source`/`LastSyncedAt` are left
alone, and the file's real signature is still stored — so it is not read again until it changes (or
the 30-day window comes round), rather than costing a re-hash and a Civitai request on every run.

### The identity chain

`IdentifyModel` is four rungs, tried in order — the first one that answers wins, and nothing below
it runs:

1. **Civitai by hash** — `GetModelVersionByHashAsync` on the file's SHA256. A hit applies the full
   Civitai response (above) and stamps `Matched`.
2. **Sidecar** — on a 404, the local `.civitai.info` / `.json` next to the file
   (`SidecarMetadataApplier`). Stamps `Sidecar` only when metadata actually came out of it; a kohya
   training config or a truncated `.civitai.info` falls through to the next rung instead (*what
   counts as applied*, above).
3. **Safetensors header** (`SafetensorsHeaderReader` + `BaseModelHeaderMap`) — reads the file's own
   length-prefixed JSON header (capped at 16 MB, the tensor payload itself is never touched) and
   maps its `__metadata__` fields to a Civitai display label: the `ss_sd_model_name` hint is checked
   *first*, because Pony / Illustrious / NoobAI checkpoints are all SDXL-architecture and would
   otherwise all collapse into plain "SDXL 1.0"; `modelspec.architecture` and the coarser
   `ss_base_model_version` (kohya only ever writes `sd_v1`/`sd_v2`, so both 1.x and both 2.x minor
   versions collapse to one label each) are checked after. Any file that isn't a readable safetensors
   header — wrong extension, corrupt, truncated, an oversized header — yields nothing here and falls
   through.
4. **Filename heuristic** (`FilenameBaseModelHeuristic`) — last resort: distinctive whole-name
   substrings, then exact tokens (`sd15`, `pdxl`, `wan`, …), then distinctive token prefixes, run
   against the filename with the directory and a known model extension stripped. The lowest-confidence
   rung of the four, which is why the UI calls it a guess rather than stating it as fact.

Nothing answering at all is `NotIdentified`. A fault anywhere in the attempt (timeout, a Civitai
response shape change, a rejected database save) is `Error` instead and does not fall through to the
rungs below it — the failure itself is the outcome.

| Outcome | Meaning |
|---|---|
| `Matched` | Civitai recognised the file's hash |
| `Sidecar` | No Civitai hit; a local `.civitai.info` / `.json` supplied metadata |
| `Header` | No Civitai hit, no usable sidecar; the safetensors header named a base model |
| `Heuristic` | Nothing above answered; the filename suggested one |
| `NotIdentified` | None of the four rungs produced anything |
| `Error` | The attempt itself failed (network, parsing, a rejected save) |

**Only the placeholder gets filled, never a user's edit.** The header and filename rungs write
*nothing but the base model* — no name, no trigger words, no description — so they use a narrower
gate than the sidecar formats' own guard (`CanWriteVersionText`, which is simply "not user-edited"):
`BaseModelWriter.CanFill` additionally requires the field to still be blank (`BaseModelRaw` is `null`
or the discovery placeholder `"???"`). A version whose base model already says something real —
Civitai-sourced, sidecar-sourced, or hand-typed — is left alone even when the rest of the version is
untouched.

**The outcome names the rung that wrote the value, never one that merely echoed it.** A rung is
credited (`Header`/`Heuristic`) only when its label actually lands on the row — i.e. `CanFill` said
yes. When `CanFill` says no (the value is already real, or the version is user-edited), stamping the
rung anyway would misreport provenance: the detail panel's "Identity source: file header" row would
describe a Base Model the header had nothing to do with. So a skipped write instead *preserves*
whatever settled identity the model already carried — `Matched`, `Sidecar`, `Header` or `Heuristic`,
read off the row **before** this run touched it — and falls back to `NotIdentified` only when there
was no settled identity to preserve (a fresh row, or one that last recorded `Error`). Concretely: a
sidecar identifies a model (`Sidecar`), the user deletes the `.civitai.info`, and the next run's
header rereads the same file as plain SDXL — the outcome stays `Sidecar`, not `Header`, because
nothing new was actually written. Preserving rather than re-deriving also keeps the retry window and
the sidecar-evidence bypass (below) tracking whatever they already were, instead of resetting a
model's due-ness every time a rung merely reconfirms an existing answer.

When the write *does* land, `Model.Source` and `Model.LastSyncedAt` are stamped to `LocalFile` and
the run's own clock — mirroring the sidecar branch's stamp (*What counts as applied*, above) — so a
model whose base model was just filled from its own file no longer reads as "never synced" to
anything that orders or filters on `LastSyncedAt` (`TileGroupingHelper`'s tile ordering among them).
Nothing is stamped when the write is skipped, same as the sidecar branch.

`HeaderCheckedAt` is stamped whenever the header could be parsed at all, even when it carried no
usable `__metadata__` fields and `BaseModelHeaderMap.Map` returned nothing, independently of whether
anything above ended up written. It stays `null` for anything that never reached that point — a
non-safetensors file, a hash match, or a sidecar hit — because the header rung only runs on the
shared miss branch, after both of those have already failed.

**Identity source is per model; base model is per version.** The detail panel's "Identity source:"
row (`ModelDetailViewModel.DescribeIdentitySource`) reads the single `ModelSyncState.MetadataOutcome`
for the whole model — "Civitai", "sidecar file", "file header", "guessed from filename" — while the
**Base Model** field shown just above it (§10) is per *version*. A model with several versions shows
one identity-source verdict describing how the model as a whole was last resolved, which is not
necessarily how any one version's base model value came to be. `None`, `NotIdentified` and `Error`
show nothing in that row rather than a discouraging label.

**Correcting a guess sticks.** Picking a value from the detail panel's Base Model dropdown sets
`ModelVersion.IsUserEdited` — exactly the flag `BaseModelWriter.CanFill` checks — so a future sync,
however it identifies the model, never overwrites that choice again. That is what the row's tooltip
means by "'Guessed from filename' is the lowest-confidence source — correct it via the Base Model
dropdown and your choice is kept."

**Retry.** All four non-`Matched`, non-`Error` outcomes — `Sidecar`, `Header`, `Heuristic`,
`NotIdentified` — share the 30-day catch-all in `SyncRetryPolicy.IsIdentifyDue` (a better source may
have appeared since). Independent of that window, a sidecar that appears or changes next to the file
makes any of those four due *immediately*: `IdentifyModelStep.IsDue` compares the file's current
sidecar signature against the one stored on the last check, and a mismatch wins over the 30-day
clock — dropping a `.civitai.info` next to a `NotIdentified` file is picked up on the very next run,
not a month from now.

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
| `HeaderCheckedAt` | Safetensors header read (see **The identity chain** above) |

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

**The scope predicate is the viewer's predicate.** "In the library" means "owns a local file
the user can still see, under an enabled LoRA source" — and that is decided by one shared
function, `LocalPathRoots.IsUnder` in Domain, which `ModelFileSyncService.MatchEnabledRoot`
(the grid) and `SyncStateRepository` (the sync) both call. They used to answer it separately:
the viewer accepted `\` or `/` at the root boundary and compared `OrdinalIgnoreCase`, while the
repository baked in `Path.DirectorySeparatorChar` and folded ASCII only, so a grid full of
models could produce a plan with nothing in it. SQL still does the narrowing — a `lower(LocalPath)`
prefix comparison per root, now emitting both separator spellings — and a root containing non-ASCII
characters, which SQLite's ICU-less `lower()` cannot fold, is resolved by an in-memory id set
instead, narrowed first by that root's leading run of ASCII characters (`E:\ÖFFENTLICH\Loras` → `e:\`).

Two things that comment used to get wrong. None of this is an indexed lookup: `lower(LocalPath)` is
a function of the column, so SQLite scans the file rows either way — what the SQL buys is that they
are discarded inside the engine rather than materialised into the process. And the `LIKE` it emits
is safe by escaping, not by absence: EF renders a captured-variable `StartsWith` as
`LIKE @p ESCAPE '\'` with `%`, `_` and `\` escaped into the parameter at runtime, so those in a
source folder's name are ordinary characters here, not wildcards.

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

**One run at a time, and the buttons say so.** The service is single-flight and *throws* on a
second run rather than queueing it, so both ways in are switched off while one is going:
`LoraViewerViewModel.IsSyncRunning` (its own bulk run, its own per-tile run, or
`ILibrarySyncService.IsRunning` — someone else's) gates the toolbar command's `CanExecute` and is
mirrored onto `ModelDetailViewModel.IsLibrarySyncRunning`, which gates the detail panel's button.
Both entry points still check before they start, for the routes that do not go through a button,
and both ask the same composite (`SyncInFlight`) — the service raises `IsRunning` only once
`ExecuteAsync` is reached, so a bulk press landing while a per-tile fetch was still *planning* would
otherwise pass a guard that only knew about that flag. The refusal answers "A metadata sync is
already running." The bulk run's `finally` disposes only the
`CancellationTokenSource` that call created — clearing the field blind let a late-finishing run
dispose the *next* run's token source, after which Cancel cancelled nothing.

### Fallbacks

| Situation | Fallback |
|-----------|----------|
| No API key configured | Requests still work (public models), but at a lower rate limit |
| Hash lookup returns 404 | Sidecar is tried; outcome recorded as `Sidecar` when metadata came out of it, `NotIdentified` otherwise (unreadable, or not metadata at all), re-checked after 30 days |
| Hash returns a version but no images | The `FetchImages` step covers it via the version endpoint |
| Network/disk failure on one item | Recorded as a `SyncFailure` (step, model, reason) and counted in the report; the run continues |
| `CivitaiId` already owned by another DB row | Only `CivitaiModelPageId` is set (grouping still works), warning logged |
| A second run started while one is going | Refused before it starts: "A metadata sync is already running." (`ExecuteAsync` would throw — the service is single-flight process-wide) |
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

Thumbnails have one producer for every caller — bulk sync, a tile scrolling into view, the sidecar
applier's local-preview probe, and the user's own "download the missing thumbnail" button all
resolve bytes through `IThumbnailProvider` and record the outcome through `ThumbnailWriter`, the one
place the six `ModelImage` thumbnail columns are written. What differs between callers is
*permission* — whether a video may be downloaded in full — and *gating* — whether a due-ness check
runs at all before the request goes out.

```
IThumbnailProvider.ProduceAsync (the ladder — DiffusionNexus.Service/Services/Sync/Thumbnails)
│
├── Rung 1: user-thumbnail:// url          → UnsupportedScheme (defensive; selection excludes these)
├── Rung 2: file:// url, or a recorded      → decode from disk (sibling probe if the path moved)
│           path has gone but a sibling
│           preview sits next to the model
├── Rung 3: IsVideoLike (type or .mp4 url) → CDN poster transform (width=450,anim=false,transcode=true
│                                             + .jpeg extension); a 404 here is VideoNoPoster (soft),
│                                             never Http404 — the transform may simply not exist yet
├── Rung 4: an ordinary still image        → CDN transform (width=450); a response that turns out to
│                                             BE a video (ThumbnailCodec.LooksLikeVideo) falls through
│                                             to rung 3, once
└── Rung 5: AllowVideoDownload only        → stream the original video, FFmpeg mid-frame, re-encode
                                              (the one rung bulk and scroll never reach)

ThumbnailCodec.Encode: SkiaSharp decode → resize to 450px wide if larger → JPEG q85. One codec,
every producer — a thumbnail from the sync step, the tile, and the sidecar applier are byte-identical.
```

### Bulk sync — `ThumbnailsStep`

- **Selection is per *version*, not per image.** `SelectThumbnailCandidatesAsync` ranks each
  version's images by the same preference `ModelVersion.PrimaryImage` uses (clean still = 0, any
  still = 1, clean video = 2, anything else = 3) and keeps only the winner — a version whose winner
  already carries bytes contributes nothing at all, and the loop never reaches the runner-up. Rows
  with a blank URL or a `user-thumbnail://` one are dropped from the ranking itself, not merely from
  the result — left in, either could win its version and hide the real image behind it.
- **One item per due image, no pacer.** Unlike the other four steps this one never awaits
  `ICivitaiRequestPacer` — the CDN is a static-asset host, not the rate-limited API, and pacing a
  library's worth of ~65 KB GETs at API speed would turn a minute into an hour. The record of an
  attempt lives on `ModelImage` itself, not on `ModelSyncStates`, because the unit of work is the
  image: two versions of one model are two independent thumbnails, two requests, two outcomes.
- **`AllowVideoDownload` is always `false`.** A video-primary row costs exactly one small poster GET
  in bulk; if the CDN has no poster for it yet, the row fails soft (`VideoNoPoster`) and tries again
  tomorrow — it never falls back to pulling the clip. That is the **0-video-bytes-in-bulk guarantee**:
  watching the network (or the log — every video URL fetched carries `transcode=true`) during a bulk
  run should show no MP4/WebM bytes at all. It covers more than rows typed `video`:
  `ModelImage.IsVideoLike` — the predicate rung 3 is gated on — also reads a video *extension* off
  the URL when `MediaType` is null, so the legacy sidecar rows that carry no `type` field reach the
  poster rung too. What falls outside it is the genuinely undetectable case: a URL with no video
  extension, on a row with no media type. See the 64 MB cap's tail below.

### Failure reasons and retry windows (`SyncRetryPolicy.IsThumbnailDue`)

| `ThumbnailFailure` | Kind | Re-checked |
|---|---|---|
| *(never attempted)* | — | Immediately |
| `Corrupt` | — | Immediately — an existing BLOB was found unreadable and nulled; the row is thumbnail-less through no fault of the source |
| `HttpError`, `VideoNoPoster` | Soft | After `ErrorRetryAfter` (1 day) — the CDN coming back is exactly the case this has to catch |
| `Http404`, `NotDecodable`, `LocalFileMissing`, `UnsupportedScheme` | Hard | Force only — a final answer; asking again costs a request to learn nothing |

Unlike the identify step there is no attempt counter to exhaust: a soft failure keeps re-attempting
on the fixed 1-day cadence for as long as it keeps failing.

**The 64 MB cap's tail.** The thumbnail `HttpClient` sets `MaxResponseContentBufferSize = 64 * 1024 *
1024` (`SyncServiceCollectionExtensions`) — large enough for any legitimate poster or still, and a
backstop against a "video in disguise": a row whose URL actually serves an MP4 while nothing about
the record says so must not be buffered as an unbounded clip. Two shapes reach rung 4 that way — a
row typed `image` whose type is simply wrong, and a row with no type whose URL carries no video
extension either (a null-`MediaType` row with an `.mp4` URL is *not* one of them; `IsVideoLike`
sends it to rung 3). Tripping that cap throws `HttpRequestException` inside
`ThumbnailProvider.FetchAsync`, which is caught in the same place as every other transport failure
and mapped to `HttpError` — **soft**, deliberately: it is re-asked after the ordinary 1-day window
rather than being written off as a permanent verdict, because nothing about an oversized response
proves the asset is unfetchable, only that this attempt's bytes were too many.

### `user-thumbnail://` ownership

`ModelImage.UserThumbnailScheme` (`"user-thumbnail://"`) marks a row whose bytes the user uploaded
directly — there is nothing behind the URL to fetch, and nothing may overwrite it. Selection excludes
these rows at the SQL level; the provider's rung 1 check is a defensive backstop in case one ever
reaches it anyway (`UnsupportedScheme`). One accepted edge falls outside that clean split: an upload
can also land on the version's *ordinary* primary-image row when one already existed, reusing its
slot rather than creating a new `user-thumbnail://` one — so its `Url` still reads as a CDN address.
If that BLOB turns out to be undecodable, it is marked `Corrupt` like any other row and the CDN's own
image is fetched to replace it on the next pass. Nothing decodable is lost — the upload was
unreadable to begin with — and the alternative is a tile that stays permanently blank with no way to
explain why.

### The tile — three on-demand paths, one gate

`ModelTileViewModel.LoadThumbnailFromVersion` (fires on every `SelectedVersion` change):

1. **BLOB cached** → decode off the UI thread. A BLOB larger than `MaxThumbnailBytes` (1 MB) is
   legacy bloat from the old `width=300` fetch (which stored whatever the CDN returned, up to 25 MB)
   — nothing written today can be that large, since every producer goes through the 450px/q85 codec.
   Such a row **self-heals on this read**: re-encoded through `ThumbnailCodec`, and the result is
   persisted only when `ShouldPersistSelfHeal` finds it actually smaller — an already-narrow BLOB
   that simply can't shrink, or a photographic source whose JPEG is no smaller than its PNG, is left
   alone rather than re-decoded and re-diffed on every future activation for no gain.
2. **Deferred sentinel** (lightweight query didn't load the BLOB) → lazy-loads it, applying the same
   self-heal on arrival.
3. **No BLOB, fetchable URL** → gated by `IsScrollFetchDue`, which is `SyncRetryPolicy.IsThumbnailDue`
   called with `force: false` — **the scroll path honors exactly the same retry windows as the sync
   step.** A row stamped `Http404` yesterday is not re-asked on every pass through the viewport; a
   soft failure waits out its 1-day window like it would in a bulk run. `AllowVideoDownload` is
   `false` here too. Without this gate, flinging through a video-heavy library would cost one GET,
   one DI scope and one `SaveChanges` per tile per scroll, forever.
4. **Everything else** (no URL, a `file://` row, or a `user-thumbnail://` row that lost its BLOB) →
   probes the model's own directory for a sibling preview file (`LocalPreviewFiles.FindSibling`),
   same ladder and codec the sidecar applier uses.

Any path that decodes a stored BLOB and gets nothing back marks the row `Corrupt` — bytes nulled,
failure stamped immediately-due — rather than leaving a placeholder with no explanation and no way
back in.

**The user's per-tile button overrides the gate with force.** `TryDownloadMissingThumbnailAsync` —
the one thumbnail path a person starts by clicking — does **not** consult `IsScrollFetchDue`: a hard
failure recorded yesterday is not an answer to a request made today. It is also the only caller that
passes `AllowVideoDownload: true`, so it is the only path ever allowed to reach rung 5 (the full
video download + FFmpeg frame) — bulk and scroll never do. When the primary image is a video it
first looks for a still sibling in the same version (`PickStaticSibling` — cheaper and more reliable
than a video frame) and downloads that instead; a sibling that merely has its BLOB *deferred* (bytes
real, unloaded) is correctly treated as already-has-a-thumbnail and skipped, not overwritten.

### The sidecar rule

`SidecarMetadataApplier.TryApplyLocalThumbnailAsync` produces thumbnails too — a `.preview.png`
(or similar) sibling discovered next to a model file, same codec and writer as everywhere else. When
that sibling exists but cannot be decoded, **the verdict belongs to that file, never to the version's
Civitai image**: the applier stamps `NotDecodable` (hard) only when the first image row in the
version's `Images` collection is itself a `file://` row
(`LocalPreviewFiles.TryGetLocalPath(firstImage.Url, …)` succeeds — note this is the collection's
first element, not the NSFW/video-aware `PrimaryImage` selector used elsewhere); a version whose
first row is an ordinary CDN image is left completely untouched, and the sync step still fetches
the real thumbnail for it on the next run. Nothing synthetic is invented to carry the failure
either — a fake `file://` row created purely to hold a stamp would become the version's permanent
primary image, pointing at something unreadable forever.

### What this replaced

The old per-caller `width=300` CDN fetch and the unbounded BLOB storage it produced are gone,
replaced everywhere by the single `ThumbnailCodec` (450px, JPEG q85) reached through
`IThumbnailProvider`. `MaxThumbnailBytes` (1 MB, above) is the only surviving trace of that regime —
a threshold nothing produced today can cross, kept purely so a database upgraded from before this
change repairs its old rows the first time each is read rather than needing a one-off migration.

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

        1a. LoadIdentitySourceAsync(tile)  — fire-and-forget, from ModelSyncState
            └── "Identity source:" row — see §4 "The identity chain" for what it shows
                and why it is per model, not per version

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

### Release note

- **A model you have hand-edited is left out of the bulk run.** `Model.IsUserEdited` takes it out of the identify selection entirely, so a model the user renamed *before* it was ever matched never picks up a Civitai id from "Download Metadata" — and without an id the tags and images steps have nothing to ask about either. That is deliberate: a bulk pass has no way to tell "I renamed this" from "this is what it is called". The detail panel's per-LoRA button is the way in — it is a forced, single-model run, which is the user asking, and the appliers still protect every field they authored.

- **The first sync after this update fetches thumbnails for every image that never had one.** The `Thumbnails` step (§9) is new: a library that previously relied on tiles fetching their own preview on scroll now gets a bulk pass too, and every version whose primary image has no BLOB yet is due. Expect a one-time cost of roughly *N* × 0.4 s, where *N* is the number of such versions (`ThumbnailsStep.EstimatedPerItem`) — a second run plans zero, because by then every reachable image has either succeeded or recorded a reason not to.

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
