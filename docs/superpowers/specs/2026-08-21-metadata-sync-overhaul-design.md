# Metadata sync & Civitai download pipeline overhaul — design

Source of truth: https://github.com/Little-God1983/DiffusionNexus/issues/521 (this file is the committed copy).

**Decisions resolved 2026-08-21 (all recommendations accepted):** D1 retry windows 30 d (NotOnCivitai/NotIdentified) · 1 d, max 3 per run (transient errors) · never (hard failures, Force only); D2 uppercase hash normalization data migration; D3 queue MaxConcurrency = submit parallelism, DownloadCoordinator = single global gate; D4 plan dialog; D5 filename heuristic on, marked "guessed"; D6 single PR, per-WP review gates.

---

## TL;DR

"Download Metadata" re-processes the whole library on every run because the sync **cannot distinguish "we already have this" from "we never tried"**. Measured on the real library today (2,577 models, log `log-20260821.txt`, run 11:14–11:35):

| | Selected | Actually useful | Cost |
|---|---|---|---|
| Phase 1 metadata by hash | 3 | 1 | 2 erroring files retried forever |
| Phase 1b sidecar re-process | 91 | 1 | 90 have no sidecar, re-read every run |
| Phase 3 tag backfill | 68 | 6 | 62 have zero tags on Civitai → asked again every run (68 × 1.5 s pacing ≈ 100 s of nothing) |
| Phase 4 thumbnails | **1,046** | ~0 | **19 of the 21 minutes**; **2 GB of preview videos re-downloaded** (largest 331 MB) to regenerate 440 thumbnails that were already in the DB; 497 × `The 'file' scheme is not supported` stack traces |

The second run, minutes later, selected exactly the same 68 / 1,046 again.

This issue is the plan for **one clean refactor** of the metadata/thumbnail sync *and* the three Civitai download paths that feed it, designed to be **safe for existing users** (additive schema only, no mass re-sync after upgrade, no overwrites, downgrade-tolerant). Target outcome: first run after upgrade does only the genuinely missing work; every following run is *"Nothing to do"* in seconds; bulk sync never downloads a video again.

---

## 1. Root causes

### RC1 — Phase 4 asks the *screen*, not the database
[LoraViewerViewModel.cs:1271](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs#L1271) selects tiles by `IsThumbnailMissing`, defined at [ModelTileViewModel.cs:1615](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs#L1615) as **`ThumbnailImage is null`** — the decoded *visual* bitmap. Tiles are rebuilt from the DB one line earlier (`RebuildTilesFromDatabaseAsync`), and thumbnails are deliberately lazy: the lightweight query returns the `ThumbnailNotLoadedSentinel` (`IsThumbnailDeferred`) and the bitmap is only decoded when a tile scrolls on screen. So every off-screen tile is "missing" by definition. `TryDownloadMissingThumbnailAsync` then never checks whether a BLOB exists — it goes to the CDN (full video + FFmpeg frame extraction for 262 of them) and **overwrites** the BLOB that was already there.

### RC2 — `file://` previews are handed to HttpClient
`LoadThumbnailFromVersion` ([ModelTileViewModel.cs:1326](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/ModelTileViewModel.cs#L1326)) knows to skip `file://` URLs; the bulk phase doesn't. 1,259 image rows carry `file://` URLs (local previews imported by `TryApplyLocalThumbnailAsync`, [LoraViewerViewModel.cs:1977](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs#L1977)) → 497 exceptions with full stack traces per run.

### RC3 — "absent" ≠ "checked and empty" (the design flaw under everything)
Every phase predicate is *"is the data absent?"*: no tags, no images, placeholder base model, no sidecar. Absence has two causes — never fetched, or fetched and genuinely empty — and nothing persists the second. A tag-less / image-less / sidecar-less / not-on-Civitai model becomes a **permanent** member of every run, and the set only grows. (Phase 1 at least has `LastSyncedAt`; phases 1b/2/3/4 have nothing.)

### RC4 — Sync phases iterate `AllTiles` (ViewModel state), not the DB
All five phases live in a 3,500-line ViewModel and read lazily-populated VM state — which is exactly how RC1 was born. Counts shown to the user are tile counts, not library facts; the per-tile "Download Metadata" button ([`DownloadMetadataForTileAsync`](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs#L2513)) is a second copy of Phase 1.

### RC5 — The download paths don't leave a model "complete", so bulk sync inherits their gaps
A mapped audit of every Civitai download path (details in §4.4):
- **Download LoRA dialog** (toolbar) and **Detail panel → Download this version** have **no collision protection** — [LoraDownloadService.cs:211-213](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/Services/LoraDownloadService.cs#L211-L213) unconditionally deletes + overwrites an existing file. Only the Browse queue has `ResolveCollisionFreeTargetPathAsync`.
- No download path stores a thumbnail; `ModelImage.ThumbnailData` stays null until the tile happens to scroll into view (or Phase 4 runs).
- The Browse queue **never notifies the Installed tab** (`RunJobAsync` fires no event); the detail panel does (`DownloadCompleted`).
- `ModelDetailViewModel` carries a ~500-line inline clone of the HTTP downloader + DB persister (fallback branch, [ModelDetailViewModel.cs:462-975](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs#L462-L975)) that diverges from `LoraDownloadService` (naive path dedup, unguarded `CivitaiId ??=`, duplicate versions dropped instead of merged).
- 17 duplicated method families across the paths (3 × destination-folder builder, 8+ × "pick primary file", 5 × `GetApiKeyAsync`, 3 × SHA256 with **inconsistent casing** (`ComputeFullSha256` lower, the others upper — `CivitaiInstalledIndex` compensates with `OrdinalIgnoreCase`, `FindByFileHashAsync` with `ToLower()` in SQL), 3 × tag upsert, 3 × thumbnail BLOB writer, 2 × `CleanupTempFile` byte-identical, …).
- Bug found on the way: cards from the primary Browse search only get `EnqueueAllVersionsHandler` wired ([CivitaiBrowserViewModel.cs:601-604](https://github.com/Little-God1983/DiffusionNexus/blob/develop/DiffusionNexus.UI/ViewModels/CivitaiBrowser/CivitaiBrowserViewModel.cs#L601-L604)); `EnqueueSelectedVersionsHandler` is null → "enqueue selected versions" silently no-ops on those cards (tag-fallback cards at `:739-743` wire both).

### RC6 — Base-model discovery stops at "not on Civitai"
Self-trained and delisted LoRAs have no sidecar and no Civitai hit, so `BaseModelRaw` stays `???` forever — which is the whole reason the LoRA Sorter (#520) showed a huge `Unknown\`. Nothing in the app reads the safetensors header, although trainers write `ss_base_model_version` / `modelspec.architecture` into it.

### Live DB facts (read-only query, 2026-08-21)
2,577 models (2,494 `LastSyncedAt` set, 1,583 with `CivitaiId`) · 20,268 `ModelImages` rows, of which 2,253 have a thumbnail BLOB, **9,535 are videos**, 1,259 are `file://`. Only the per-version *primary display image* ever needs a thumbnail (≈ one per version), not 20k rows — any plan that counts images instead of versions is wrong by an order of magnitude.

---

## 2. Goals / non-goals

**Goals**
1. Sync work is selected from **persisted DB state** that records *attempt outcomes*, never from VM/visual state or from data absence alone.
2. Re-running a sync on an unchanged library is a no-op and says so, in seconds.
3. Bulk thumbnailing never downloads a video; `file://` previews are handled as local files.
4. Every Civitai download path (Download LoRA dialog, Browse queue, Detail panel, waitlist→queue, Pipelines installer) goes through **one** downloader with **one** target-path builder, **one** collision policy, **one** persister, and leaves the model *complete* (metadata + tags + poster thumbnail) and the Installed tab notified.
5. Base-model discovery chain: Civitai by hash → sidecar → **safetensors header** → filename heuristic, outcome recorded, shared by sync and (later) the Sorter.
6. The user sees the plan (counts + estimate) **before** the run and a faithful report after it.
7. Unified Console logging of every step (standing rule), without stack-trace spam.

**Non-goals (separate issues)**
- LoRA Sorter preview UI rework (tree, before/after, gain summary) — follows this, on a rebased #520.
- Restore-from-sort-history UI.
- `LoraUpdateChecker` (new-version checks) — untouched except for sharing the API-key provider.
- Gallery / dataset image pipelines.

---

## 3. Safety contract for existing users

These are requirements, verified by tests, not intentions:

| # | Rule |
|---|---|
| S1 | **Additive schema only.** New table `ModelSyncStates` (1:1 with `Models`) + two nullable columns on `ModelImages`. No column drops, renames, type changes, or data rewrites of existing columns — except the opt-in hash-case normalization (decision D2). |
| S2 | **Automatic pre-migration backup.** `DatabaseRecoveryService` currently applies pending migrations at startup with *no* backup (the `.pre-*-backup` files in `Data\` were manual). Add: when pending migrations exist, `VACUUM INTO Data\Diffusion_Nexus-core.pre-<MigrationName>-<yyyyMMdd-HHmmss>.db` first (reuse the existing VACUUM INTO helper from the General-backup feature), keep the newest 3. Applies to every future migration, not just this one. |
| S3 | **No network on upgrade.** Sync-state rows are *derived* from existing data on the first `PlanAsync` after upgrade (idempotent, one transaction, no HTTP): `MetadataOutcome = Matched` if `CivitaiId` set, else `NotOnCivitai` if `LastSyncedAt` set (sidecar/header chain gets **one** pass over these); `TagsCheckedAt = LastSyncedAt` if the model already has tags; `ImagesCheckedAt = LastSyncedAt` if it has images; existing thumbnail BLOBs are treated as attempted-and-succeeded. Result on the reference library: first plan ≈ *3 metadata · 68 tags (one final time) · 0 images · N primaries without BLOB*; second plan = nothing. |
| S4 | **Never overwrite.** No existing thumbnail BLOB is replaced unless it fails to decode (then it is marked corrupt and re-fetched once) or the user presses *Force*. No existing model file is overwritten by any download path — collision policy everywhere (`{stem}_{versionId}{ext}`, identical content → reuse). |
| S5 | **`IsUserEdited` is honored** by every writer (tags, images, base model, category), as `UpdateModelFromCivitaiAsync` already does and `LoraDownloadService.PersistDownloadedModelAsync` / the detail-panel clone currently do not. |
| S6 | **Downgrade-tolerant.** An older build opening the migrated DB ignores the extra table/columns; `CleanStaleMigrationHistory` already drops unknown history rows, so nothing re-applies. Verified by a test that opens the migrated schema with the previous model snapshot. |
| S7 | **Nothing new runs at startup.** Startup stays: load tiles → discover new files → background file verify. Sync runs only on the button / per-tile / post-download. |
| S8 | **Per-tile and bulk are the same code** (`SyncScope.Models(ids)` vs `SyncScope.Library`), so behavior cannot diverge again. |

---

## 4. Design

### 4.1 Persisted sync state

**New entity `ModelSyncState`** (Domain) / table `ModelSyncStates`, PK = FK `ModelId`, cascade delete:

| Column | Type | Meaning |
|---|---|---|
| `MetadataCheckedAt` | datetime? | last identity attempt (hash lookup + fallback chain) |
| `MetadataOutcome` | enum `SyncOutcome` (`None, Matched, Sidecar, Header, Heuristic, NotIdentified, Error`) | how the base model/identity was obtained |
| `MetadataAttempts` | int | bounded retry |
| `LastError` | string? | one line, no stack trace |
| `TagsCheckedAt` | datetime? | tags fetched (even if the result was empty) |
| `ImagesCheckedAt` | datetime? | image records fetched (even if empty) |
| `SidecarSignature` | string? | `{path}|{mtime}|{length}` of the sidecar last parsed — re-parse only when it changes |
| `HeaderCheckedAt` | datetime? | safetensors header read |
| `UpdatedAt` | datetime | |

**`ModelImage` additive columns:** `ThumbnailAttemptedAt` (datetime?), `ThumbnailFailure` (string? code: `Http404`, `HttpError`, `NotDecodable`, `Corrupt`, `LocalFileMissing`, `VideoNoPoster`).

**Retry policy** (settings, defaults): `NotOnCivitai` / `NotIdentified` re-checked after **30 days**; `Error` / `HttpError` after **1 day**, max 3 attempts per run window; `Http404`, `NotDecodable`, `LocalFileMissing` never auto-retried — only *Force*. Precedent in the codebase: `LastCheckedForUpdatesUtc` + `LoraUpdateCheckStalenessDays` in `LoraUpdateChecker`.

Why a side table instead of more columns on `Models`: `Models` is already wide and read by every tile query; sync state is only read by the planner; absence of a row has a defined meaning ("legacy — derive"), which makes S3 a pure function with table-driven tests.

### 4.2 `LibrarySyncService` (Service project, no Avalonia)

```csharp
Task<SyncPlan>   PlanAsync(SyncScope scope, SyncOptions options, CancellationToken ct);      // DB queries only, no network, < 1 s on 2.5k models
Task<SyncReport> ExecuteAsync(SyncPlan plan, IProgress<SyncProgress> progress, CancellationToken ct);
```
- `SyncScope`: `Library` | `SourceFolder(path)` | `Models(ids)`.
- `SyncOptions`: per-step include flags, `Force` flags (tags / images / thumbnails / not-on-Civitai), retry windows.
- Steps, each an `ISyncStep` with `SelectAsync(scope) → IReadOnlyList<SyncItem>` (a DB query) and `ExecuteAsync(item)` that **always** records an outcome:
  0. **DiscoverFiles** — existing `ModelFileSyncService.DiscoverNewFilesAsync`, unchanged.
  1. **IdentifyModel** — replaces Phase 1 + 1b + per-tile copy: stored hash (or compute once) → `GetModelVersionByHashAsync` → on 404: sidecar (`LocalFileMetadataProvider`, skipped when `SidecarSignature` unchanged) → safetensors header → filename heuristic. Records `MetadataOutcome`.
  2. **FetchTags** — `CivitaiId` set ∧ `TagsCheckedAt` null/stale. Stamps `TagsCheckedAt` even when Civitai returns zero tags.
  3. **FetchImages** — `CivitaiId` set ∧ no images ∧ `ImagesCheckedAt` null/stale. Stamps even when empty.
  4. **Thumbnails** — per *version*: the display image (`ModelVersion.PrimaryImage` rule, static-sibling preference for videos) with `ThumbnailData IS NULL` ∧ retryable. Uses `IThumbnailProvider` (§4.3). Bounded parallelism 4 (CDN, not API-rate-limited); API steps keep the proven 1.5 s pacing — it is the *redundant* calls that vanish, not the pacing.
- Every step: Unified Console `Info` at start/end with counts, `Debug` per item, failures as one `Warn` line each (stack trace only at `Debug`). Progress via `IProgress<SyncProgress>` (step, i/n, item name), marshalled by the VM.
- Fresh `IServiceScope`/`IUnitOfWork` per batch (existing pattern), `SaveChanges` per item or small batch, cancellation checked between items, terminal flush in `finally`.
- Concurrency guard: one sync at a time per process (`SemaphoreSlim(1,1)`); a second request while running returns the running plan's progress instead of starting another.

The ViewModel's `DownloadMissingMetadataAsync` shrinks to: `PlanAsync` → `SyncPlanDialog` → `ExecuteAsync` → `RebuildTilesFromDatabaseAsync()` once. Phases 1/1b/2/3/4, `DownloadMetadataForTileAsync`'s copy and `MarkModelSyncedAsync` are deleted from the VM (~900 lines).

### 4.3 `IThumbnailProvider`

Resolution order for a display image, each step recorded:
1. Existing BLOB (deferred sentinel counts as present — **never** `ThumbnailImage is null`).
2. `file://` URL or sibling preview file (`.preview.png`, `.jpg`, …) → read + resize locally; file gone → `LocalFileMissing`.
3. Image URL → CDN `width=450` (existing).
4. **Video URL → CDN poster frame**: rewrite the transform segment to `width=450,anim=false,transcode=true` and the extension to `.jpeg`. **Verified live today**: returns `image/jpeg`, 65 KB, for an asset whose MP4 is 1 MB; without `transcode=true` the CDN returns the MP4 regardless of extension. Opt-in online canary test guards this.
5. Full video download + FFmpeg mid-frame: **only** on the single-tile, user-initiated path, never in bulk.

Corrupt-BLOB handling moves to where decoding actually happens: `LazyLoadThumbnailFromDbAsync` / `CreateTileBitmap` failure → `ThumbnailFailure = Corrupt`, BLOB nulled → next sync re-fetches that one image. Byte-level work (HTTP, URL rewrite, SkiaSharp resize/transcode) lives in Service; `Bitmap` creation stays in UI. The three existing BLOB writers collapse into one `PersistThumbnailAsync`.

### 4.4 One Civitai download path

New `ICivitaiModelDownloader` (UI Services, wraps the existing `LoraDownloadService.DownloadFileAsync` + `PersistDownloadedModelAsync`):

```csharp
Task<DownloadOutcome> DownloadAsync(DownloadRequest request, IProgress<DownloadProgress>? progress, CancellationToken ct);
// DownloadRequest: CivitaiVersion, CivitaiFile, TargetRoot, Category?, ExistingModelId?, Trigger (Dialog | BrowseQueue | DetailPanel | Waitlist | Pipeline)
```
Inside, in order: target dir via shared **`LoraPathBuilder`** (today's `SorterPathBuilder.BuildTargetDirectory` + `SanitizeFolderName`, moved to Service — the three dialog copies and `DownloadDestinationViewModel.BuildTargetDirectory` delegate to it; Unknown-category segment omitted, placeholder base model → `Unknown`, names sanitized) → collision via shared **`CollisionPolicy`** (today's `CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync` + `SorterPathBuilder.EnumerateCandidateNames`, same `{stem}_{versionId}` convention, identical-content reuse) → stream to `.tmp` + move (existing) → persist (existing, made `IsUserEdited`-aware) → **completion**: `LibrarySyncService.ExecuteAsync(Models(id))` with steps Tags + Thumbnails (poster, ~65 KB) so the tile shows a preview immediately → `ILibraryChangeNotifier.ModelDownloaded(modelId)` which `LoraViewerViewModel` subscribes to (fixes the Browse-queue silence; replaces the detail panel's ad-hoc `DownloadCompleted`).

Callers after the refactor: Download LoRA dialog, Browse queue `RunJobAsync`, Detail panel, waitlist→queue (already Path B), `PipelineAssetInstaller` (keeps writing its `.civitai.info`, which stays useful for other tools). 

Deleted/merged: `ModelDetailViewModel` inline downloader + persister (~500 lines) + its `InferCategoryFromTags` (lacks the `LooksLikeCategoryName` guard — tag `"2000"` becomes a category today) + `CleanupTempFile`; three `GetTargetFolder`/`PreviewPath` copies; `GetApiKeyAsync` ×5 → `ICivitaiApiKeyProvider`; `FormatFileSize` clones → existing `FileSizeFormatter`; SHA256 ×3 → one `FileHasher` (uppercase; comparisons `OrdinalIgnoreCase`); "pick primary file" ×8 → `CivitaiVersionFiles.PickPrimary` (keeps `LoraDownloadService`'s 4-level fallback); `ParseBaseModel`/`GetFileFormat` wrappers → the single implementations. Card handler bug (RC5 last bullet) fixed. Queue keeps its own `MaxConcurrency` as the number of jobs it *submits*; the `DownloadCoordinator` remains the global gate (decision D3).

### 4.5 Base-model discovery chain (`IModelIdentityResolver`, Service)

Input: file path + SHA256. Output: `ModelIdentity(BaseModelRaw?, CivitaiVersionId?, Tags, Source: Civitai|Sidecar|Header|Heuristic|None, Confidence)`.
- **Civitai by hash** — existing.
- **Sidecar** — existing `LocalFileMetadataProvider` (`.civitai.info` / `.json`), keyed by `SidecarSignature`.
- **Safetensors header (new `SafetensorsHeaderReader`)** — read 8-byte LE length + JSON header only (cap 16 MB, never the tensors); map `__metadata__.ss_base_model_version` (`sdxl_base_v1-0`, `sd_v1`, `sd_v2`, …), `modelspec.architecture` (`stable-diffusion-xl-v1-base/lora`, `flux-1-dev/lora`, `stable-diffusion-v1/lora`, …), and `ss_sd_model_name` hints to Civitai base-model names via a small table with tests from real headers.
- **Filename heuristic** — `sdxl`, `pony`, `il`/`illustrious`, `flux`, `wan`, `sd15`, … lowest confidence, recorded as `Heuristic` so the UI can show "guessed" and the user can correct it (write-back sets `IsUserEdited`).
Result is written to `ModelVersion.BaseModelRaw` + `ModelSyncState.MetadataOutcome`. The Sorter then reads the DB only (its private resolver chain goes away in the #520 rebase) and its `Unknown\` bucket shrinks accordingly.

### 4.6 UI

- **"Download Metadata" → `SyncPlanDialog`**: one row per step — count, what it will do, estimated time (count × pacing); checkboxes per step; *Force re-check…* options; **Start** / Cancel. All-zero plan shows *"Library is up to date — nothing to do"* with the last-run timestamp and no Start.
- Progress: existing busy overlay + `SyncStatus` line (`Thumbnails [212/389] name…`), working Cancel between items.
- **Report**: per-step counts + an expandable failure list with reasons (`HashLookupError`, `VideoNoPoster`, …) — not "535 failed".
- Per-tile "Download Metadata" and post-download completion use the same service; the detail view shows the identity source (`Civitai` / `sidecar` / `header` / `guessed`).
- Settings: retry windows, thumbnail concurrency (default 4).

---

## 5. Work breakdown (one branch `feature/metadata-sync-overhaul`, ordered, each package reviewed and green before the next)

- [ ] **WP1 — Schema + sync state + derivation** · `ModelSyncState` entity/table, `ModelImage` columns, EF migration, pre-migration VACUUM INTO backup (S2), `SyncStateDeriver` (S3) with table-driven tests, downgrade test (S6).
- [ ] **WP2 — `LibrarySyncService`** · plan/execute, steps 0–3, retry windows, single-flight guard, logging; `LoraViewerViewModel` wired to it; tile-based phases + per-tile copy deleted; tests with in-memory SQLite per test conventions.
- [x] **WP3 — Thumbnails** · `IThumbnailProvider`, CDN poster rewrite (+ online canary), `file://`/sibling handling, attempt/failure recording, corrupt-on-decode marking, one BLOB writer, bounded parallelism; step 4.
- [ ] **WP4 — Identity chain** · `SafetensorsHeaderReader` + mapping table (fixture headers), filename heuristic, `IModelIdentityResolver`, sidecar signature; step 1 uses it; detail view shows the source.
- [ ] **WP5 — One download path** · `ICivitaiModelDownloader`, `LoraPathBuilder` + `CollisionPolicy` moved to Service, all five callers migrated, post-download completion, `ILibraryChangeNotifier`, clones deleted (§4.4 list), `IsUserEdited` in the persister, card-handler bug, `FileHasher` casing (+ D2 data migration if approved).
- [ ] **WP6 — UI** · `SyncPlanDialog`, report view, settings, Unified Console polish, docs (`Doc/LoraViewer.md`).
- [ ] **WP7 — Acceptance on the reference library** (§6) + PR.
- [ ] **Follow-up (separate PR)** — rebase #520's Sorter onto this: drop its private resolver chain, consume DB identity, then the preview-UI rework.

Process: same as #520 — spec from this issue, subagent-driven TDD per WP, review gate per WP, final whole-branch review. Tests run with `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release`, never solution-level.

---

## 6. Verification / acceptance

**Automated**
- Selection queries per step against in-memory SQLite fixtures: never-checked vs checked-and-empty vs stale vs forced.
- `SyncStateDeriver` table: every combination of (`CivitaiId`, `LastSyncedAt`, has tags, has images, has BLOB) → expected row; idempotent on re-run.
- Thumbnail provider: deferred sentinel = present; `file://` never hits HTTP; video → poster URL (unit) + live canary (opt-in an opt-in env var, same pattern as the SDK canaries); decode failure marks `Corrupt` exactly once.
- Header reader: synthetic safetensors fixtures (SDXL, SD1.5, Flux, Pony, no metadata, truncated file, oversized header → rejected).
- Collision policy + path builder: identical content reuse, `_versionId` rename, sanitization, Unknown segment rules — shared tests for downloads and Sorter.
- Downgrade: open migrated schema with the previous model snapshot → no exception.
- Migration: backup file created before apply; additive-only asserted by diffing `PRAGMA table_info` before/after for existing tables.

**Manual acceptance on the reference library (2,577 models)** — numbers recorded in the PR:
1. Upgrade → first plan shows ≈ 3 metadata · 68 tags · 0 images · *N* primaries without BLOB; run completes; **0 video bytes** downloaded; no stack traces in the log.
2. Second plan immediately after: *"Nothing to do"*, planned in < 5 s.
3. Restart app → same as 2 (the original complaint).
4. Browse queue download → tile appears in Installed tab with poster thumbnail, no manual refresh.
5. Download LoRA dialog into a folder that already holds a same-named different file → `_versionId` rename, original untouched; same-named identical file → reused, no second copy.
6. Per-tile Download Metadata on a self-trained LoRA with header metadata → base model filled, source shown as `header`.
7. Cancel mid-run → completed items stamped, report says partial, next plan resumes from the remainder.

---

## 7. Decisions needed before WP1 starts

- **D1 — Retry windows**: `NotOnCivitai`/`NotIdentified` 30 d, transient errors 1 d (max 3/run), hard failures never (Force only). *Recommended as stated.*
- **D2 — Hash casing**: normalize `ModelFiles.HashSHA256` to uppercase with a one-time `UPDATE … SET HashSHA256 = upper(HashSHA256)` (idempotent, covered by S2 backup), so SQL equality works without `ToLower()` scans. *Recommended yes.*
- **D3 — Download gating**: keep the queue's `MaxConcurrency` as submit-parallelism and `DownloadCoordinator` (3 slots) as the single global gate. *Recommended yes.*
- **D4 — Plan dialog vs. direct start**: "Download Metadata" opens the plan dialog (one extra click, always shows what will happen) vs. auto-starts and only shows the plan in the status line. *Recommended dialog.*
- **D5 — Filename heuristic**: on by default at lowest priority, results marked "guessed" in the UI. *Recommended on.*
- **D6 — Delivery**: one PR at the end of the branch, or stacked PRs per WP (WP1–2, WP3–4, WP5, WP6). *Recommended single PR with the per-WP review gates, as asked ("one big clean refactor").*

---

## 8. References
- Log evidence: `%LocalAppData%\DiffusionNexus\Logs\log-20260821.txt` 11:14:38–11:35:31 and 11:36:09–11:41:04.
- CDN poster probe (2026-08-21): `…/width=450,anim=false,transcode=true/92791582.jpeg` → `200 image/jpeg 65,093 B`; `…/width=450/92791582.jpeg` → `200 video/mp4 1,042,872 B`.
- Related: #520 (LoRA Sorter — closed, to be rebased on this), `docs/superpowers/specs/2026-08-20-lora-sorter-design.md`.
