# Civitai Early-Access Waitlist — Design

**Date:** 2026-08-13
**Branch:** `feature/civitai-early-access-waitlist`
**Status:** Approved by user (chat), pending spec review

## Goal

Let users put early-access (temporarily paywalled) Civitai LoRAs on a waitlist
instead of downloading them. A Waitlist tab in the Civitai browser's queue side
panel shows each entry with a countdown to when it becomes free, an update
button to re-check all entries against the API, and a button that moves
now-free entries into the download queue. The multiselect/enqueue flow detects
early-access models and offers waitlisting (or opening the Civitai page to buy
now). Permanently paid models are never waitlisted and get their own
"Paywalled" card badge.

## Decisions (user-confirmed)

1. **Check once, not polling.** The deadline captured when an entry is added
   (or last updated) is the source of truth for the countdown. Countdown and
   counter are computed locally from stored `earlyAccessDeadline` vs
   `DateTimeOffset.UtcNow` — Civitai timestamps are UTC ISO-8601, so no
   offset/skew handling is needed. An explicit **Update all** button re-checks
   every entry via the API.
2. **Move ready to queue re-verifies.** The move button takes only
   deadline-passed entries, re-fetches each version via
   `ICivitaiClient.GetModelVersionAsync`, enqueues confirmed-free ones, and
   keeps still-paywalled ones on the waitlist with the corrected deadline
   (catches creators extending early access or switching to permanent).
3. **"Paywalled" badge = permanent paid only** (`paidAccess.permanent == true`,
   latest version). Time-limited early access keeps the existing purple
   "Early Access" badge; when permanent, "Paywalled" replaces it (no stacking).
4. **Placement:** the Download Queue side panel content is wrapped in a
   two-tab `TabControl`: **Queue** | **Waitlist**.
5. **Tab counter:** the Waitlist tab header shows a badge in its upper-right
   corner with the number of *available* (deadline-passed) entries; hidden
   when zero. Refreshed by a local 1-minute `DispatcherTimer` — no API calls.
6. **Permanently paid models are never waitlisted.** The early-access dialog
   lists them as "permanently paid — can't be waitlisted"; the only offered
   path for them is opening the Civitai page.

## Components

### 1. `CivitaiWaitlist` service (new)

`DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiWaitlist.cs`, modeled on
`CivitaiDownloadQueue` (`ObservableObject`, **not** DI-registered; constructed
in `LoraViewerViewModel`'s ctor alongside the queue and passed into
`CivitaiBrowserViewModel`).

- `ObservableCollection<CivitaiWaitlistEntry> Entries`.
- `bool TryAdd(...)` — dedup on `VersionId` (same rule as the queue's
  `Enqueue`). Rejects entries whose version is permanently paid. The initial
  `EarlyAccessDeadline` comes from the `CivitaiModelVersion` already loaded in
  the browse results (the picked version) — no API call on add.
- `AvailableCount` — count of entries with `IsAvailable == true`; drives the
  tab badge. Recomputed by the timer tick and on collection changes.
- **Persistence:** `civitai-waitlist.json` under
  `LocalApplicationData/DiffusionNexus`, mirroring the queue's
  `#region Persistence`: private `PersistedEntry` DTO decoupled from the VM,
  `Persist()` after every mutation, `TryRestore()` in the ctor, and a
  `persistPathOverride` ctor parameter as the test seam.

### 2. `CivitaiWaitlistEntry` (new)

Carries everything needed to later construct a `CivitaiDownloadJob` without
re-browsing, plus waitlist state:

- Identity/payload: `ModelId`, `VersionId`, `ModelName`, `VersionName`,
  `BaseModel`, `Category`, `FileName`, `DownloadUrl`, `SizeBytes`,
  `SizeDisplay`, `ExpectedSha256`, `PreviewImageUrl`, `IsNsfw` (for the
  civitai.red host swap when opening the page).
- Waitlist state: `EarlyAccessDeadline` (`DateTimeOffset?`), `AddedAt`,
  `LastCheckedAt`, and an entry-status enum:
  `Waiting | Available | PermanentlyPaid | Unavailable | CheckFailed`.
- `IsAvailable` — deadline is null/past **and** status is not
  `PermanentlyPaid`/`Unavailable`.

### 3. Waitlist tab UI

`CivitaiBrowserView.axaml` lines ~445–611 (the queue side panel `Border`):
wrap current content as TabItem "Queue", add TabItem "Waitlist".

- **Tab header badge:** header is a small `Grid`/`Panel` with the "Waitlist"
  text and a corner `Border` badge bound to `Waitlist.AvailableCount`
  (`IsVisible` when > 0).
- **Rows** (`ItemsControl`, `x:DataType` the entry type): preview thumbnail,
  model + version name, base model, status line — countdown text
  ("free in 2d 4h") while waiting, green "Available" when free, warning-color
  "Permanently paid — won't become free" / "No longer available" for dead
  entries, and a muted "check failed" note with old data kept on network
  errors. Row buttons via the existing
  `$parent[UserControl].((vm:CivitaiBrowserViewModel)DataContext)` idiom:
  open on Civitai (globe), remove (✕).
- **Footer:** `Update all` and `Move ready to queue` buttons + entry count.
- **Timer:** 1-minute `DispatcherTimer` in the browser VM (or the waitlist
  service) recomputes countdown strings, `IsAvailable`, and `AvailableCount`.
  Local only.

### 4. Re-check mechanics (`Update all` and move-time verification)

- Per entry: `GetModelVersionAsync(versionId, apiKey)` (impl
  `CivitaiClient.cs:118`); API key via the browser's existing
  `GetApiKeyAsync()` wrapper.
- Concurrency gated by `SemaphoreSlim(3)` — `CivitaiClient` has no
  client-side throttle (429 retry only), pattern precedent
  `CivitaiResultViewModel.s_videoExtractionGate`.
- Outcome matrix per entry:
  - Still early access → update `EarlyAccessDeadline`, status `Waiting`.
  - Free now (per `IsEarlyAccessActive` == false) → status `Available`.
  - `PaidAccess.Permanent == true` → status `PermanentlyPaid` (flagged, user
    removes; never auto-deleted).
  - 404/deleted → status `Unavailable`.
  - Network/API error → keep old data, status `CheckFailed`.
  - Every outcome sets `LastCheckedAt` (except `CheckFailed`, which keeps it).
- All EA determinations go through the existing
  `CivitaiEarlyAccessExtensions.IsEarlyAccessActive` predicate
  (`DiffusionNexus.Civitai\Models\CivitaiEarlyAccess.cs:26`) — never inline
  field checks.

### 5. Move ready to queue

1. Select entries with `IsAvailable`.
2. Re-verify each (section 4). Still-paywalled → stays on waitlist with
   corrected deadline; permanent/deleted → flagged as above.
3. Confirmed-free → enqueue into `CivitaiDownloadQueue` via a **new enqueue
   overload** that accepts the waitlist entry + the fresh
   `CivitaiModelVersion` (the current
   `Enqueue(CivitaiResultViewModel, CivitaiVersionPickItemViewModel)`
   signature isn't constructible from a waitlist entry). Dedup on `VersionId`
   as today; successfully enqueued entries are removed from the waitlist.

### 6. Early-access dialog extension

`Views\Dialogs\EarlyAccessConfirmDialog` — enum `EarlyAccessConfirmResult`
grows `AddToWaitlist` and `OpenWebsite`; two new buttons in the button row.
Dialog body gains a short explanation: early access means the creator has
temporarily paywalled the model; the waitlist tracks when it becomes free;
paying on civitai.com gets it now. If the selection contains permanently paid
models, they are listed by name as "permanently paid — can't be waitlisted".

`CivitaiBrowserViewModel.EnqueueWithEarlyAccessPromptAsync`
(`CivitaiBrowserViewModel.cs:872`) handles the new results — this covers both
multiselect (`AddSelectionToQueueAsync`) and per-card
(`EnqueueAllVersionsForCard`) since both funnel through it:

- `AddToWaitlist`: non-EA pairs → queue as normal; temporary-EA pairs →
  waitlist; permanent-paid pairs → skipped (they were flagged in the dialog).
- `OpenWebsite`: non-EA pairs → queue as normal; each distinct EA model's
  Civitai page opened via the `OpenOnCivitai` pattern
  (`CivitaiResultViewModel.cs:213` — NSFW → `civitai.red` host swap,
  try/catch + `IUnifiedLogger.Warn`).
- No-`Window`-owner fallback (line 898) keeps its current "add all" behavior.

### 7. "Paywalled" card badge

- `CivitaiResultViewModel`: new `{ get; private init; }` bool
  `IsPermanentlyPaid`, computed in the ctor from the **latest version's**
  `PaidAccess?.Permanent == true` (same deliberate latest-version-only
  semantic as `IsEarlyAccess`, see comment at `CivitaiResultViewModel.cs:49`).
  `CivitaiPaidAccess.Permanent` already exists (`CivitaiModelVersion.cs:108`)
  — zero DTO work.
- View: one more `Border` in the top-right badge stack
  (`CivitaiBrowserView.axaml:275–292`), red-toned (e.g. `#AADC2626`),
  text "Paywalled". The "Early Access" badge's `IsVisible` becomes
  "EA **and not** permanently paid" so the badges never stack.

## Logging (standing rule)

Unified Console trace/debug logging of every feature step so a hang shows the
last successful step: entry added (model/version id), each re-check started +
its outcome, each move-to-queue handoff, persistence restore count, dialog
choice taken. Category: existing download/browser category used by the queue.

## Testing (TDD)

- **Waitlist service:** add/dedup, permanent-paid rejection, persist →
  restore round-trip via `persistPathOverride`, availability + `AvailableCount`
  with an injectable clock (`utcNow` parameter pattern, as in
  `IsEarlyAccessActive`).
- **Re-check outcome matrix:** fake `ICivitaiClient` returning each outcome
  (deadline extended, free, permanent, 404, throw) → assert entry status,
  deadline, `LastCheckedAt`, and that errors keep old data.
- **Move ready to queue:** ready entries enqueue into a real
  `CivitaiDownloadQueue` (with its own path override) and leave the waitlist;
  still-paywalled ones stay with updated deadline.
- **Dialog flow:** VM-level tests through the `DialogService`/result seam —
  no Avalonia init in tests (standing gotcha); the dialog window stays thin.
- **Badge:** `IsPermanentlyPaid` computation from version payloads (permanent,
  temporary EA, free), including "permanent suppresses EA badge" visibility
  logic if it lives in the VM.
- Existing `CivitaiEarlyAccessDetectionTests` already cover the predicate —
  extend only if the predicate changes (not planned).

## Out of scope (YAGNI)

Auto-download when available; notifications beyond the tab counter; special
handling of non-LoRA model types; DB-table persistence; client-wide API
throttling rework; enriching the legacy sidecar DTO family with paid-access
fields.

## Implementation anchors

| Concern | File |
|---|---|
| Queue pattern + persistence template | `DiffusionNexus.UI\Services\CivitaiBrowser\CivitaiDownloadQueue.cs` (persistence region ~670–808, job model ~827) |
| Queue side panel to wrap in tabs | `DiffusionNexus.UI\Views\CivitaiBrowser\CivitaiBrowserView.axaml` ~445–611 |
| Card badge stack | `CivitaiBrowserView.axaml` ~275–292 |
| EA prompt funnel | `DiffusionNexus.UI\ViewModels\CivitaiBrowser\CivitaiBrowserViewModel.cs` `EnqueueWithEarlyAccessPromptAsync` ~872 |
| Dialog to extend | `DiffusionNexus.UI\Views\Dialogs\EarlyAccessConfirmDialog.axaml{,.cs}` |
| EA predicate | `DiffusionNexus.Civitai\Models\CivitaiEarlyAccess.cs:26` |
| DTOs (already sufficient) | `DiffusionNexus.Civitai\Models\CivitaiModelVersion.cs` (`PaidAccess` 76, `Permanent` 108, `EndsAt` 111) |
| Re-check API | `ICivitaiClient.GetModelVersionAsync` (`CivitaiClient.cs:118`) |
| Open-page pattern | `CivitaiResultViewModel.OpenOnCivitai` (~213, civitai.red host swap) |
| Wiring point | `LoraViewerViewModel.cs` ctor ~324–334 |
