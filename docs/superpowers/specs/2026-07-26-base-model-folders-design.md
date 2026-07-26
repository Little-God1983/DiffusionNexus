# Base Model Folders — Design

**Date:** 2026-07-26
**Branch:** `feature/base-model-folders` (off `develop` — develop-only, no main backport)
**Status:** Approved by user (dialogue 2026-07-25/26)

## Problem

Diffusion Nexus Core workloads (pipelines like Anime-To-Real, and anything else installed
from Installer Manager → Diffusion Nexus Core → Workloads) download their models into the
*default ComfyUI installation's* models tree. A user with no ComfyUI installation registered
cannot install Core workloads at all (`InstallMissingAsync` throws "No ComfyUI installation
is registered"), and the download target silently shifts depending on which packages exist
and which is flagged default.

## Goal

A general **Base Model Folders** registry — the same system the app already uses for
Generation Galleries (auto-registered per installation) and the LoRA Viewer source list
(user-managed rows, one favorite) — applied to model storage:

1. Users manage a list of model storage folders in Settings, with one row marked **Default** (⭐).
2. Adding an installation in Installer Manager **auto-registers** its model folders
   (for ComfyUI: including every root declared in `extra_model_paths.yaml`).
3. The Diffusion Nexus Core Workloads window shows a **dropdown of all enabled folders**
   (Default preselected); the chosen folder is the download target for that install.
4. `%LOCALAPPDATA%\DiffusionNexus\Models` is the built-in fallback when no folders exist —
   a fresh user with zero installations can install Core workloads out of the box.
5. Fully compatible with the current approach: all existing detection keeps working;
   nothing already on disk is re-downloaded.

Out of scope: moving files when folders change; auto-registering LoRA Viewer sources on
package add (possible follow-up); captioning model storage (already app-managed);
the ComfyUI *workload* install flow (Workloads dialog → `WorkloadInstallService`), which
keeps targeting the selected ComfyUI installation per user decision.

## Data model (`Diffusion_Nexus-core.db`)

New entity `BaseModelFolder : BaseEntity`, collection on `AppSettings` (singleton Id=1):

| Column | Type | Notes |
|---|---|---|
| `AppSettingsId` | FK → AppSettings, cascade | same as `ImageGallery` |
| `FolderPath` | string, required, max 1000 | a models root (contains/receives `diffusion_models/`, `loras/`, `text_encoders/`, `vae/`, …) |
| `IsEnabled` | bool, default true | disabled rows are excluded from scanning and the dropdown |
| `Order` | int | display + scan order |
| `IsDefault` | bool, default false | at most one row true (service enforces on save) |
| `InstallerPackageId` | nullable FK → InstallerPackage, **SetNull** on delete | set for auto-registered rows; row survives package removal |

EF config mirrors `ImageGallery` (`HasMany` cascade, indexes on `AppSettingsId` + `FolderPath`).
One migration (`AddBaseModelFolders`). Per repo rule, run `publish.ps1` before entity/migration
changes. `AppSettingsService.SaveSettingsAsync` syncs the collection via the existing
`SyncChildCollection` helper and enforces the single-default invariant (last-set wins).
`SettingsExportData`/`SettingsExportService` round-trip the rows.

## Services

### `IAppSettingsService` additions (follow the LoraSources precedent)

- `GetEnabledBaseModelFoldersAsync()` → ordered enabled rows
- `AddBaseModelFolderAsync(folder)` — idempotent by path (case-insensitive); re-links
  `InstallerPackageId` when the path already exists (gallery `LinkOutputFolderAsync` behavior)
- `RemoveBaseModelFolderAsync(id)` / `UpdateBaseModelFolderAsync(folder)`

### `IModelFolderCatalog` (new, DiffusionNexus.UI/Services/Diffusion)

The single resolution authority consumed by installer + backend:

- `GetDownloadTargetsAsync()` → dropdown items, in order: enabled Base Model Folders
  (Default first); if none exist → exactly one item, the LocalAppData fallback
  (`%LOCALAPPDATA%\DiffusionNexus\Models`), labelled as default. Item = path + IsDefault flag.
- `GetDefaultDownloadRootAsync()` → first item of the above; directory created on demand.
- `GetSearchRootsAsync()` → deduped (OrdinalIgnoreCase), existing-dirs-only:
  enabled Base Model Folders → LocalAppData fallback → *nothing else* (ComfyUI roots are
  appended by the backend provider, below).

### Auto-registration

- **On package add** (`InstallerManagerViewModel.AddExistingInstallation` flow, next to
  `LinkOutputFolderAsync`): register model roots for the package —
  - ComfyUI: `ComfyUiPathDiscovery.EnumerateModelSearchPaths(installPath)` (own `models/`
    **+ every `extra_model_paths.yaml` root** + portable sibling `models/`), one row each;
  - other types (Forge/A1111/…): `{InstallationPath}\models` when the directory exists.
  All rows linked to the package, idempotent by path.
- **On startup**: idempotent backfill registrar (pattern: `OutputsFolderRegistrar`) runs the
  same logic for every already-registered package, so existing users see their folders appear
  without re-adding installations. No row is ever auto-marked Default.
- **On package removal**: FK SetNull — the folder row stays, now user-owned.

### Root resolution for detection/inference

`LocalDiffusionBackendProvider.ResolveModelsRootsAsync` prepends
`IModelFolderCatalog.GetSearchRootsAsync()` to the existing ComfyUI-derived roots (deduped).
Single choke point: pipeline readiness, `FindLoraPathBy*`, and local inference all see
Base Model Folders + LocalAppData fallback + all ComfyUI roots (incl. yaml extras).
The "returns empty when no ComfyUI" contract disappears — at minimum the fallback is returned.

## Download flow changes

- `PipelineAssetInstaller.InstallMissingAsync` gains a `downloadRoot` parameter
  (`InstallMissingAsync(manifest, vramGb, downloadRoot, ct)`); the old "first ComfyUI root"
  selection and the "No ComfyUI installation is registered" throw are removed. Callers pass
  the dropdown selection (Workloads window) or `GetDefaultDownloadRootAsync()` (run screens /
  tiles, which have no dropdown).
- Subfolders (`loras/`, `vae/`, …) are created inside the chosen root on demand, as today.

## UI

### Settings — new "Base Model Folders" expander

Built from the Generation Galleries template: description text, Add button, rows of
[enable checkbox] [path textbox] [⭐ default toggle] [Browse] [Remove]. The ⭐ behaves like
the LoRA source favorite star (selecting one clears the others, `HasChanges` set). Wired
into `LoadAsync`/`SaveAsync` like the other collections.

### Core Workloads window (`CoreWorkloadsDialog` / `CoreWorkloadsViewModel`)

ComboBox "Download to:" above the tabs, items from `GetDownloadTargetsAsync()`
(display: folder path, default item marked "(default)"), Default preselected.
Selection is **per-window only** — it does not write back to settings. The selected path
flows into the pipeline install callback → `InstallMissingAsync(..., downloadRoot, ...)`.

## Error handling

- Selected/download root uncreatable → the per-asset error isolation added in the sidecar
  fix surfaces it; no silent fallback to a different folder once one was explicitly chosen.
  The *automatic* default resolution (no dropdown involved) falls back to LocalAppData with
  a logged warning when a configured default cannot be created.
- Nonexistent registry folders are skipped by `GetSearchRootsAsync` (scan) but still listed
  in Settings (user may plug the drive back in). Dropdown lists them too; choosing one
  attempts creation.
- `extra_model_paths.yaml` parse failures already degrade gracefully (existing parser).

## Testing (TDD)

- Catalog: fallback-only when registry empty; Default-first ordering; dedupe; skip missing dirs.
- Auto-registration: ComfyUI package registers models/ + yaml roots + portable sibling;
  non-ComfyUI registers `models/`; idempotent re-run; re-link existing path to package.
- Settings service: collection sync round-trip; single-default invariant.
- Installer: `InstallMissingAsync` downloads into the passed root; readiness detects assets
  across catalog + ComfyUI roots (extend `PipelineAssetInstallerTests`).
- ViewModel: dropdown preselects default; empty registry shows fallback item.
- Export/import round-trip.
- Full existing suite stays green.

## Rollout

Single branch `feature/base-model-folders` → one PR to `develop`. No main backport.
