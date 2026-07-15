# General Backup + Database Backup — Design

**Date:** 2026-07-15
**Branch:** `feature/general-backup-database`
**Status:** Approved (implementation in progress)

## Goal

Rework the backup feature so it protects two independent payloads — **dataset images**
and the **core user database** (`Diffusion_Nexus-core.db`) — driven by an **app-level
scheduler**, with **every step surfaced in the Unified Console** (async, non-blocking).
Move the backup configuration from the *LoRA Dataset Helper* settings section into the
*General* section.

## Decisions (confirmed with user)

1. **Which DB:** core user DB only (`Diffusion_Nexus-core.db`). The catalog DB
   (`diffusion_nexus.db`) is redeployed from the embedded copy, so it is not user data.
2. **Scheduler:** app-level background scheduler (lifted out of `DatasetManagementViewModel`),
   runs at startup + on a timer regardless of which module is open.
3. **DB restore:** out of scope. UI shows an **info box** explaining the DB can only be
   restored manually (copy the `DatabaseBackup_*.db` over the live DB **while the app is
   closed**) because replacing a live WAL DB is risky.
4. **DB location:** display the exact current DB path in Settings, reusing
   `DiffusionNexusCoreDbContext.GetDatabaseDirectory()` (same source the startup Unified
   Console line "Database loaded from: …" uses).
5. **Shared schedule/location:** one interval, one backup location, `MaxBackups` applied
   **per type** (N dataset zips + N db copies).

## Settings model (`AppSettings`) + migration

- Rename `AutoBackupEnabled` → **`BackupDatasetImagesEnabled`** (checkbox: "Back up dataset images").
- Add **`BackupDatabaseEnabled`** (checkbox: "Back up database"); entity default `true`.
- Shared, unchanged: `AutoBackupIntervalDays/Hours`, `AutoBackupLocation`, `LastBackupAt`, `MaxBackups`.
- Move fields into a new `#region Backup Settings`.
- **EF migration:** `RenameColumn AutoBackupEnabled → BackupDatasetImagesEnabled` (preserves each
  user's value) + `AddColumn BackupDatabaseEnabled`. Seed `BackupDatabaseEnabled = 1` where the old
  `AutoBackupEnabled = 1` (existing backup users start protecting their DB too).
- Ripples: `AppSettingsConfiguration`, model snapshot, `SettingsExportData` (rename + add,
  bump `SettingsExportSchema.CurrentVersion` → 2, map legacy v1 field on import),
  `SettingsExportService`, and **`publish.ps1`** clean-DB seed SQL (line ~280 inserts the column literally).

## Services (clean separation)

- **`DatasetBackupService`** (dataset images only): unchanged zip/restore/analyze/stats. Remove the
  internal `AutoBackupEnabled` gate; relocate `IsBackupDueAsync`/`GetNextBackupTimeAsync` to the
  scheduler (mark the old members `[Obsolete]` per repo convention). Still writes `DatasetBackup_<ts>.zip`.
- **New `IDatabaseBackupService` / `DatabaseBackupService`:** safe consistent snapshot of the core DB
  via `VACUUM INTO 'DatabaseBackup_<ts>.db'` (correct for a live WAL DB; a plain copy can be torn).
  Own retention cleanup for `DatabaseBackup_*.db`. Reports phases via `IProgress<BackupProgress>` +
  `IUnifiedLogger`.
- **New singleton `IBackupScheduler` / `BackupScheduler`:** owns timer + due-check + `RunBackupAsync(manual)`
  orchestration. Runs whichever payloads are enabled, narrating each step to the Unified Console
  (`LogCategory.Backup`), updates `LastBackupAt` once, drives the status-bar progress bar via
  `IActivityLogService`. Started from `App.CompleteStartupAsync`. Runs on a background thread w/ its
  own DI scope (matches current pattern) — async, never blocks the UI.

## Unified Console transparency

Orchestrator emits Info lines per step, e.g.: "Automatic backup started (datasets + database)" →
"Zipping datasets (N files)…" → "Dataset backup written: … (X MB)" → "Backing up database…" →
"Database copied: … (Y MB)" → "Cleaning up old backups (kept N)…" → "Backup complete" / clear failure line.

## Settings UI (`SettingsView.axaml` + `SettingsViewModel`)

- Move the "Auto Backup" block from the *LoRA Dataset Helper* expander into the *General* expander.
- Two checkboxes replace the one; sub-controls enable when **either** toggle is on; validation
  requires a location + valid interval when either is on.
- Add: **current DB location** (read-only path + Open Folder button) and the **restore info box**.
- "Backup Now" calls `BackupScheduler.RunBackupAsync(manual:true)`. "Load Backup" (restore) stays dataset-only.
- Reuse existing styled-Border patterns; look for reusable components first (repo rule).

## Dataset Helper cleanup

Remove the timer/countdown/due-check/execute code from `DatasetManagementViewModel`; read scheduler
state for any status it still displays.

## Out of scope (YAGNI)

DB restore (manual only); backing up the catalog DB; separate schedules for DB vs datasets.

## Testing

`DatabaseBackupService` (VACUUM INTO yields an openable copy; retention); scheduler due-logic;
orchestrator include/exclude per toggles; migration round-trip (old value preserved). Existing
dataset-backup tests stay green.

## Pre-change safety

Live core DB copied to `%LOCALAPPDATA%/DiffusionNexus/SafetyBackups/pre-general-backup-db_<ts>/`
before any entity/migration change (per repo rule; user chose quick-copy over `publish.ps1`).
