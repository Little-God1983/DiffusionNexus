# Metadata Sync Overhaul — Plan C: Identity Chain Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A model that Civitai doesn't know and no sidecar describes still gets its base model — read from the safetensors header, or guessed from the filename and marked as a guess — so the Sorter's `Unknown` bucket shrinks and the detail view says where every identity came from.

**Architecture:** Two new pure-static components in `DiffusionNexus.Service.Services.Sync.Identity` — `SafetensorsHeaderReader` (8-byte LE length + JSON header only, 16 MB cap, never throws) and `BaseModelHeaderMap`/`FilenameBaseModelHeuristic` (header fields / filename tokens → Civitai display labels) — inserted into `IdentifyModelStep`'s existing miss-branch: Civitai → sidecar → **header** → **heuristic** → NotIdentified. Outcomes `SyncOutcome.Header`/`Heuristic` (already in the enum, unused) are stamped along with the already-existing `ModelSyncState.HeaderCheckedAt` column; the detail view gains an "Identity source" row read from the state.

**Tech Stack:** .NET 10, `System.Text.Json` + `System.Buffers.Binary.BinaryPrimitives`, EF Core 10 SQLite, xUnit + FluentAssertions + Moq.

**Spec:** `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` §4.5, acceptance §6 (`:202` fixture list, `:213` per-tile header case), decision D5 (heuristic on by default, "guessed"). Supplementary inventory with exact anchors: `.superpowers/sdd/2026-08-23-metadata-sync-overhaul-C-identity/planC-inventory.md` (git-ignored; consult if an anchor drifted).

**Deviation from the spec, decided up front:** no standalone `IModelIdentityResolver`/`ModelIdentity` type. `IdentifyModelStep` already orchestrates rungs 1–2 with the applier/stamping wiring the chain needs; a wrapper interface would have exactly one consumer (the Sorter reads the *database* after this plan, not the resolver). The spec's §4.5 *outputs* — outcome per source, base model written, "guessed" shown, Sorter able to go DB-only — are all delivered.

## Global Constraints

- **Zero schema churn.** `SyncOutcome.Header`/`Heuristic` and `ModelSyncState.HeaderCheckedAt` shipped in Plan A. No migrations; `DatabaseRecoveryService.cs:418`'s recovery DDL stays untouched.
- **Label spellings come from `CivitaiBaseModelCatalog.BundledSnapshot`** (`DiffusionNexus.Civitai\CivitaiBaseModelCatalog.cs:61-149`) — the mapping tables' outputs are Civitai display strings (`"SDXL 1.0"`, `"SD 1.5"`, `"Flux.1 D"`, `"Pony"`, `"Illustrious"`, `"NoobAI"`, `"Wan Video"`, …), never enum names, never invented labels. A wrong spelling parses to `BaseModelType.Other` and leaks an unfilterable folder.
- **Provenance never goes into `BaseModelRaw`** — it feeds the viewer filter (mirrored into Civitai API queries) and the Sorter's folder names. Provenance lives only in `ModelSyncState.MetadataOutcome`.
- **Write guards:** header/heuristic write the base model only when `!dbVersion.IsUserEdited` AND the stored value is a placeholder (`BaseModelRaw is null or "???"` — the `SidecarMetadataApplier.cs:469` precedent). A lower-confidence source never clobbers a higher-confidence value; every write goes through the shared two-line rule (raw string + `ParseCivitai` enum together — never `BaseModelRaw` alone).
- **Readers never throw and never enumerate directories.** `SafetensorsHeaderReader.TryRead` returns null on any failure (locked file, truncated, bad JSON, oversized header) — `LoraSorterViewModel.cs:820-835` documents that in-use `.safetensors` files throw `IOException`/`UnauthorizedAccessException` in the field. Exact-path probes only (the `SidecarMetadataApplier.Find` lesson: enumeration cost 16 s of planning on the live library).
- **Cancellation discipline:** a cancelled item is never stamped (`ct.ThrowIfCancellationRequested()` between rungs, preserving `IdentifyModelStep.cs:181`'s pattern).
- `SyncOutcome` is persisted as a string, max 20 — append-only, never reorder.
- Tests: real in-memory SQLite fixtures (the `IdentifyModelStepTests` pattern), real files in temp dirs, no EF InMemory, no Avalonia init, no culture-sensitive asserts (German-locale machine). Test command: `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release` (never solution-level; Debug only if Release file-locked — MSB3021/3027).
- Files: UTF-8 no BOM; match each file's existing CRLF/LF byte-for-byte (repo is mixed per file — check before editing). Commits end `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Never push mid-plan; never commit to develop/main.
- **The Sorter is NOT touched.** Its resolver-chain deletion is the separate #520-rebase follow-up PR; this plan only makes it possible (inventory §4 holds the deletion map).

---

### Task 1: SafetensorsHeaderReader

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Identity/SafetensorsHeaderReader.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Identity/SafetensorsHeaderReaderTests.cs`

**Interfaces (Produces):**
```csharp
namespace DiffusionNexus.Service.Services.Sync.Identity;

/// <summary>The identity-relevant fields of a safetensors JSON header's __metadata__ block.</summary>
public sealed record SafetensorsHeaderInfo(
    string? BaseModelVersion,   // __metadata__["ss_base_model_version"]
    string? Architecture,       // __metadata__["modelspec.architecture"]
    string? ModelNameHint);     // __metadata__["ss_sd_model_name"]

public static class SafetensorsHeaderReader
{
    public const long MaxHeaderBytes = 16 * 1024 * 1024;   // spec §4.5: cap 16 MB, never the tensors
    public static SafetensorsHeaderInfo? TryRead(string filePath);   // null on ANY failure; never throws
}
```

**Behavior (normative):**
1. Extension not `.safetensors` (OrdinalIgnoreCase) → null.
2. Open `new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920)` — **ReadWrite** deliberately (a trainer mid-checkpoint holds the file write-shared; the house `FileShare.Read` in `FileHasher.cs:23` would fail there; state this in a comment).
3. Read exactly 8 bytes → `BinaryPrimitives.ReadUInt64LittleEndian`. Length 0, > `MaxHeaderBytes`, or `8 + length > stream.Length` → null (truncated/oversized → rejected, per acceptance §6).
4. Read exactly `length` bytes; `JsonDocument.Parse`. Root object's `"__metadata__"` property (object of string values) → extract the three keys; a missing `__metadata__` or missing keys yield a `SafetensorsHeaderInfo` with nulls — **a parsed header with no metadata is still a successful read** (the caller stamps `HeaderCheckedAt` on non-null).
5. Any exception anywhere (IO, access, JSON, argument) → null. No logging inside the reader (the step logs).

- [ ] **Step 1: Write the failing tests** — with this fixture builder (the repo has no binary fixtures; everything is built in code, per the `ThumbnailCodecTests.Png()` precedent):
```csharp
private static byte[] Safetensors(string headerJson, int trailingTensorBytes = 16)
{
    var json = Encoding.UTF8.GetBytes(headerJson);
    var buffer = new byte[8 + json.Length + trailingTensorBytes];
    BinaryPrimitives.WriteUInt64LittleEndian(buffer, (ulong)json.Length);
    json.CopyTo(buffer, 8);
    return buffer;   // trailing zeros stand in for tensor data
}
private static string Meta(params (string Key, string Value)[] pairs) =>
    $$"""{"__metadata__":{{{string.Join(",", pairs.Select(p => $"\"{p.Key}\":\"{p.Value}\""))}}},"tensor.weight":{"dtype":"F16","shape":[4],"data_offsets":[0,8]}}""";
```
Tests (temp-dir per the `LocalPreviewFilesTests` pattern; every file written for real):
  - `TryRead_ExtractsTheThreeMetadataKeys` — `Meta(("ss_base_model_version","sdxl_base_v1-0"),("modelspec.architecture","stable-diffusion-xl-v1-base/lora"),("ss_sd_model_name","ponyDiffusionV6XL"))` → all three populated.
  - `TryRead_HeaderWithoutMetadataBlockIsASuccessfulEmptyRead` — plain `{"tensor.weight":{...}}` → non-null info, all fields null.
  - `TryRead_TruncatedFileReturnsNull` — declared length 500, only 40 bytes present.
  - `TryRead_OversizedHeaderReturnsNull` — declared length `MaxHeaderBytes + 1` (file itself small — the length prefix alone must reject it BEFORE any large allocation; assert with a tight file).
  - `TryRead_GarbageJsonReturnsNull` — valid length prefix, body `not json{{`.
  - `TryRead_WrongExtensionReturnsNull` — same valid bytes as the first test but named `.ckpt`.
  - `TryRead_MissingFileReturnsNull`.
  - `TryRead_EmptyLengthReturnsNull` — 8 zero bytes only.
- [ ] **Step 2: Run to verify RED** (compile failure): `dotnet test DiffusionNexus.Tests/DiffusionNexus.Tests.csproj -c Release --filter "FullyQualifiedName~SafetensorsHeaderReader"`
- [ ] **Step 3: Implement** per the behavior list. Read the length prefix with `stream.ReadExactly` (or a loop — partial reads on network shares are real); check bounds before allocating the header buffer.
- [ ] **Step 4: GREEN, commit**
```bash
git add -A && git commit -m "feat(sync): read the safetensors header — sixteen megabytes of JSON, never the tensors"
```

### Task 2: The two mapping tables

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/Identity/BaseModelHeaderMap.cs`
- Create: `DiffusionNexus.Service/Services/Sync/Identity/FilenameBaseModelHeuristic.cs`
- Test: `DiffusionNexus.Tests/Sync/Service/Identity/BaseModelHeaderMapTests.cs`, `FilenameBaseModelHeuristicTests.cs`

**Interfaces (Produces):**
```csharp
public static class BaseModelHeaderMap
{
    /// <summary>Civitai display label for a parsed header, or null when the header says nothing usable.</summary>
    public static string? Map(SafetensorsHeaderInfo info);
}
public static class FilenameBaseModelHeuristic
{
    /// <summary>Civitai display label guessed from a model FILE NAME (no directory, extension ignored), or null.</summary>
    public static string? Guess(string? fileName);
}
```

**`BaseModelHeaderMap.Map` — evaluation order is load-bearing** (Pony/Illustrious/NoobAI are SDXL-architecture refinements: the name hint MUST win over the architecture, or every Pony LoRA maps to `"SDXL 1.0"`):
1. `ModelNameHint` lowercase-contains: `"pony"` → `"Pony"`, `"illustrious"` → `"Illustrious"`, `"noob"` → `"NoobAI"`.
2. `Architecture`: lowercase, strip everything from the first `'/'` (drops `/lora`), then exact:
   `"stable-diffusion-xl-v1-base"` → `"SDXL 1.0"`, `"stable-diffusion-v1"` → `"SD 1.5"`, `"stable-diffusion-v2-768-v"` → `"SD 2.1"`, `"stable-diffusion-v2"` → `"SD 2.0"`, `"stable-diffusion-3-medium"` → `"SD 3"`, `"flux-1-dev"` → `"Flux.1 D"`, `"flux-1-schnell"` → `"Flux.1 S"`.
3. `BaseModelVersion`: lowercase prefix-match: `"sdxl_base_v1-0"` → `"SDXL 1.0"`, `"sdxl_base_v0-9"` → `"SDXL 0.9"`, `"sd_v1"` → `"SD 1.5"`, `"sd_v2"` → `"SD 2.1"` (kohya writes `sd_v1` for all 1.x and `sd_v2` for 2.x — 1.5/2.1 are the dominant members; a code comment states the approximation).
4. Nothing matched → null. Every returned string must appear verbatim in `CivitaiBaseModelCatalog.BundledSnapshot` — write the test `Map_EveryOutputIsACatalogLabel` that reflects over the table (expose the outputs via an `internal static IReadOnlyCollection<string> AllLabels` or assert against a hand-kept list mirroring the table) and asserts each is in the bundled snapshot list.

**`FilenameBaseModelHeuristic.Guess` — tokenize then match, most-specific first:**
- Normalize: `Path.GetFileNameWithoutExtension`, lowercase invariant, split on `[^a-z0-9]+` → tokens; additionally form each adjacent-pair concatenation (so `sd1.5` → tokens `sd1`,`5` → pair `sd15`; `sd_15` likewise).
- Match order (first hit wins):
  1. Whole-name substring (safe, distinctive words): `"illustrious"` → `"Illustrious"`, `"noobai"` → `"NoobAI"`, `"sdxl"` → `"SDXL 1.0"`.
  2. Token-or-pair EXACT: `"sd15"` → `"SD 1.5"`, `"sd21"` → `"SD 2.1"`, `"sd35"` → `"SD 3.5"`, `"sd3"` → `"SD 3"`, `"pdxl"` → `"Pony"`, `"il"` → `"Illustrious"`, `"wan"`/`"wan21"`/`"wan22"` → `"Wan Video"`.
  3. Token StartsWith: `"pony"` → `"Pony"` (catches `ponyxl`, `ponyv6`; NOT `harmony` — that has no token starting with pony), `"flux"` → `"Flux.1 D"`, `"illust"` → `"Illustrious"`, `"noob"` → `"NoobAI"`.
- Null/blank input → null.

- [ ] **Step 1: Failing tests.** Map: one test per rung + the priority test `Map_NameHintBeatsArchitecture` (`ponyDiffusionV6XL` + `stable-diffusion-xl-v1-base/lora` → `"Pony"`), `Map_ArchitectureBeatsVersionString`, `Map_UnknownEverythingReturnsNull`, `Map_EveryOutputIsACatalogLabel`. Heuristic (Theory):
```csharp
[InlineData("MyChar_Pony_v2", "Pony")]
[InlineData("ponyxl-style", "Pony")]
[InlineData("sdxl_lineart", "SDXL 1.0")]
[InlineData("myIllustriousMix", "Illustrious")]
[InlineData("style-il", "Illustrious")]
[InlineData("detailer_sd15", "SD 1.5")]
[InlineData("detailer_sd1.5", "SD 1.5")]
[InlineData("wan21_motion", "Wan Video")]
[InlineData("flux_dev_char", "Flux.1 D")]
[InlineData("noob_artist", "NoobAI")]
[InlineData("harmony_lora", null)]       // 'pony' inside a token must NOT match
[InlineData("wander_style", null)]       // 'wan' prefix of a longer token must NOT match
[InlineData("family_car", null)]         // 'il' inside a token must NOT match
[InlineData("mySd150Style", null)]       // 'sd15' inside a longer merged token must NOT match
[InlineData("", null)]
[InlineData(null, null)]
```
- [ ] **Step 2: RED.  Step 3: implement.  Step 4: GREEN, commit**
```bash
git add -A && git commit -m "feat(sync): header fields and filename tokens map to civitai labels — hints before architecture"
```

### Task 3: The chain in IdentifyModelStep

**Files:**
- Create: `DiffusionNexus.Service/Services/Sync/BaseModelWriter.cs` (the shared two-line write rule)
- Modify: `DiffusionNexus.Service/Services/Sync/SidecarMetadataApplier.cs` (`WriteBaseModel :513-520` delegates to the shared helper; behavior identical)
- Modify: `DiffusionNexus.Service/Services/Sync/Steps/IdentifyModelStep.cs` (miss-branch `:176-188`, `IsDue :133`, `StampAsync :301`, `Description :62`)
- Modify: `DiffusionNexus.DataAccess/Repositories/ModelRepository.cs` + `IModelRepository` (add `GetVersionByIdAsync` — tracking, no Includes; the Plan B `GetImageByIdAsync` twin. Do NOT widen `GetByIdWithIncludesAsync`: it carries image BLOBs)
- Test: extend `DiffusionNexus.Tests/Sync/Service/Steps/IdentifyModelStepTests.cs`; extend `DiffusionNexus.Tests/DataAccess/CoreDbMigrationTests.cs`

**Interfaces (Produces):**
```csharp
namespace DiffusionNexus.Service.Services.Sync;
public static class BaseModelWriter
{
    /// <summary>Writes both spellings of the base model — the raw Civitai string and the parsed
    /// enum — or neither. Blank input is a missing answer, not an instruction to forget.</summary>
    public static bool Write(ModelVersion dbVersion, string? baseModelRaw);   // exact body of the old WriteBaseModel

    /// <summary>The header/heuristic gate: only fill a placeholder, never a user's edit.</summary>
    public static bool CanFill(ModelVersion dbVersion) =>
        !dbVersion.IsUserEdited && dbVersion.BaseModelRaw is null or "???";
}
// IModelRepository:
Task<ModelVersion?> GetVersionByIdAsync(int versionId, CancellationToken ct = default);
```

**The miss-branch replacement** (`IdentifyModelStep.cs:176-188`; `ct.ThrowIfCancellationRequested()` between rungs, `sidecar.Signature` still passed to the stamp in every case so the new-sidecar-evidence path keeps working):
```csharp
var sidecar = await _sidecar.ApplyAsync(uow, candidate.ModelId, candidate.LocalPath, ct).ConfigureAwait(false);
ct.ThrowIfCancellationRequested();
if (sidecar.Applied)
{
    await StampAsync(uow, candidate.ModelId, SyncOutcome.Sidecar, now, sidecar.Signature, error: null, ct).ConfigureAwait(false);
    return SyncItemResult.Success;
}

// No Civitai match, no sidecar — read the file's own header, then guess from its name.
var header = SafetensorsHeaderReader.TryRead(candidate.LocalPath);
ct.ThrowIfCancellationRequested();
var headerCheckedAt = header is not null ? now : (DateTimeOffset?)null;
var label = header is not null ? BaseModelHeaderMap.Map(header) : null;
var outcome = label is not null ? SyncOutcome.Header : SyncOutcome.NotIdentified;
if (label is null)
{
    label = FilenameBaseModelHeuristic.Guess(Path.GetFileNameWithoutExtension(candidate.LocalPath));
    if (label is not null) outcome = SyncOutcome.Heuristic;
}
if (label is not null)
{
    var version = await uow.Models.GetVersionByIdAsync(candidate.VersionId, ct).ConfigureAwait(false);
    if (version is not null && BaseModelWriter.CanFill(version)) BaseModelWriter.Write(version, label);
}
await StampAsync(uow, candidate.ModelId, outcome, now, sidecar.Signature, error: null, ct, headerCheckedAt).ConfigureAwait(false);
_logger?.Debug(LogCategory.Network, LogSource, $"'{candidate.Name}' not on Civitai → {outcome}" + (label is not null ? $" ({label})" : string.Empty), sidecar.SidecarPath);
return SyncItemResult.Success;
```
Rules encoded above, stated for the reviewer: the **outcome reflects the best source that produced an answer this run** even when the write itself was skipped (value already real, or user-edited); the write is separately guarded by `CanFill`. `StampAsync` gains a trailing optional `DateTimeOffset? headerCheckedAt = null` parameter that sets `state.HeaderCheckedAt` when non-null (existing callers unchanged). `IsDue :133` becomes `is not (SyncOutcome.Sidecar or SyncOutcome.Header or SyncOutcome.Heuristic or SyncOutcome.NotIdentified)`. `Description :62` → `"Identify models (Civitai, sidecar, file header, filename)"`.

- [ ] **Step 1: Failing tests** (all on `NewNotFoundStep()`; model files created via the existing `NewModelFile(name, content)` with `.safetensors` names and the Task 1 fixture builder — copy `Safetensors`/`Meta` helpers into this test class or a shared internal test helper):
  - `Execute_HeaderIdentifiesAnSdxlLora` — arch header → state `Header`, `HeaderCheckedAt == state.MetadataCheckedAt`, version `BaseModelRaw == "SDXL 1.0"` + `BaseModel == SDXL10`; second `SelectAsync` at the same `now` → not due.
  - `Execute_NameHintBeatsArchitecture` — pony hint + sdxl arch → `"Pony"`.
  - `Execute_HeaderDoesNotOverwriteARealBaseModel` — seed version `BaseModelRaw = "Flux.1 D"` → value untouched, outcome still `Header`.
  - `Execute_HeaderRespectsUserEditedVersion` — `IsUserEdited = true`, placeholder value → value untouched, outcome `Header`.
  - `Execute_FilenameHeuristicIsTheLastResortBeforeNotIdentified` — headerless `.safetensors` named `MyChar_Pony_v2.safetensors` → outcome `Heuristic`, `"Pony"` written, `HeaderCheckedAt` set (header WAS read — it just said nothing).
  - `Execute_NonSafetensorsFileSkipsStraightToHeuristic` — `.pt` file named `style_sdxl.pt` → outcome `Heuristic`, `HeaderCheckedAt` null.
  - `Execute_NothingMatchesStampsNotIdentified` — garbage-named headerless file → `NotIdentified`, no base-model write.
  - `Execute_SidecarStillBeatsTheHeader` — sidecar present AND sdxl header → outcome `Sidecar`.
  - `Execute_CivitaiHitNeverConsultsTheHeader` — hash-hit step (existing `NewStep` with a version) on a file whose header says Pony → outcome `Matched`, header label nowhere.
  - `Select_ANewSidecarMakesAHeaderIdentifiedModelDue` — execute to `Header` (signature `""`), then write a `.civitai.info` next to the file → `SelectAsync` at the same `now` includes the model (extends the existing signature-evidence test family).
  - `CoreDbMigrationTests`: mirror the `NotIdentified` string round-trip at `:78-100` for `Header` (stored string `"Header"`).
  - Revisit the two assertions that `HeaderCheckedAt` stays null (`ModelSyncStateTests.cs:19`, `SyncStateDeriverTests.cs:154`): the deriver one stays true (derivation never reads headers — keep, with a comment); the entity-default one stays true. Neither should be weakened.
- [ ] **Step 2: RED** (`--filter "FullyQualifiedName~IdentifyModelStep|FullyQualifiedName~CoreDbMigration"`).  **Step 3: implement** (BaseModelWriter first, applier delegation — its tests must stay green unchanged — then the step).  **Step 4: GREEN** on `~Sync`, commit:
```bash
git add -A && git commit -m "feat(sync): the identify chain reads the file itself before giving up"
```

### Task 4: Identity source in the detail view

**Files:**
- Modify: `DiffusionNexus.UI/ViewModels/ModelDetailViewModel.cs` (`LoadAsync :296-319`, property + loader)
- Modify: `DiffusionNexus.UI/Views/Controls/ModelDetailView.axaml` (`:260-273` region — insert one grid row after "Base Model:", renumber `Grid.Row` on the rows below)
- Modify: `DiffusionNexus.UI/ViewModels/LoraViewerViewModel.cs` (`OnDetailMetadataDownloadRequested :1349-1392` wording)
- Test: extend `DiffusionNexus.Tests/Viewer/` (headless-testable pieces)

**Changes:**
1. `ModelDetailViewModel`: `[ObservableProperty] private string? _identitySourceDisplay;` plus `public bool HasIdentitySource => IdentitySourceDisplay is not null;` (notify on change). In `LoadAsync`, fire-and-forget `_ = LoadIdentitySourceAsync(modelId);` exactly like the existing `_ = LoadBaseModelCatalogAsync();` — new scope from `_scopeFactory`, `uow.SyncStates.GetByModelIdAsync(modelId)`, map:
```csharp
internal static string? DescribeIdentitySource(SyncOutcome outcome) => outcome switch
{
    SyncOutcome.Matched => "Civitai",
    SyncOutcome.Sidecar => "sidecar file",
    SyncOutcome.Header => "file header",
    SyncOutcome.Heuristic => "guessed from filename",
    _ => null,   // None, NotIdentified, Error — say nothing rather than something scary
};
```
   (`internal static` so it is directly testable.) Swallow-and-log failures like the catalog loader does. Document the granularity honestly in the XML doc: the source is **per model** (one state row) while the base model is **per version** — on a multi-version model the row describes how the model was identified, not each version's value.
2. XAML: after the Base Model row insert `Identity source:` label + `TextBlock Text="{Binding IdentitySourceDisplay}"` with `IsVisible="{Binding HasIdentitySource}"` on both, tooltip: `How the base model was determined. 'Guessed from filename' is the lowest-confidence source — correct it via the Base Model dropdown and your choice is kept.` Renumber the `Grid.Row` indices below and the grid's `RowDefinitions`.
3. Per-tile wording (`:1377-1379`): the success message `"Metadata refreshed from Civitai."` becomes `"Metadata refreshed."` (the new row states the source); the no-result message stays for genuinely-nothing runs, but the condition must use the report: when `outcome.Applied` is false AND `IdentifyPlanned > 0` keep `"No metadata found on Civitai for this file."` — no logic change needed beyond the string, because a header/heuristic identification counts as `Succeeded` in the step result and therefore as `Applied` (verify this while wiring; state it in the report).
4. The chip refreshes after a per-tile run for free (`detail.LoadAsync(tile)` re-fires the loader — verify).

- [ ] **Step 1: Failing tests**: `DescribeIdentitySource` theory (all seven enum values); a `LoraViewerViewModelSyncTests`-style test only if the existing harness reaches the wording branch cheaply — otherwise state why not in the report.
- [ ] **Step 2: RED.  Step 3: implement.  Step 4:** `--filter "FullyQualifiedName~Viewer|FullyQualifiedName~ModelDetail"` green + `dotnet build DiffusionNexus.UI/DiffusionNexus.UI.csproj -c Release --no-incremental` (0 warnings). Commit:
```bash
git add -A && git commit -m "feat(viewer): the detail view says where an identity came from"
```

### Task 5: Docs + full verification

**Files:**
- Modify: `DiffusionNexus.UI/Doc/LoraViewer.md` (identity-chain section: the four rungs and their order, outcome table incl. `Header`/`Heuristic`, the placeholder-only + user-edit guards, the per-model vs per-version granularity note, the "guessed" correction flow, retry: all identity outcomes re-check at 30 days and a new/changed sidecar makes any of them due immediately)
- Modify: `docs/superpowers/specs/2026-08-21-metadata-sync-overhaul-design.md` (tick WP4 in §5)

- [ ] **Step 1: Write the docs** — every behavioral claim verified against the code before it is written (the Plan B lesson: docs get reviewed with the same skepticism as code).
- [ ] **Step 2: Full verification**: full Release suite (known flakes: `GenerationGalleryViewModelTests.TagCloudSearchText…`, `CheckScoreAdapterTests`, occasionally a Distiller temp-file test — rerun once if alone); UI build 0 warnings; BOM check over all files this plan created; byte-level line-ending audit of touched files vs base (no `grep -c $'\r$'` — it misreported three times in Plan B; use a byte-count method and state the command).
- [ ] **Step 3: Commit**
```bash
git add -A && git commit -m "docs(viewer): the identity chain — four rungs, one honest source label"
```

---

## Manual acceptance (user, after merge)
1. Per-tile Download Metadata on a self-trained LoRA with header metadata → base model filled, detail view shows `file header` (spec §6 `:213`).
2. A file like `MyChar_Pony_v2.safetensors` unknown to Civitai with no sidecar → base model `Pony`, source `guessed from filename`; correcting it via the dropdown survives the next sync.
3. Bulk run on the library → previously-`NotIdentified` models with readable headers flip to `Header`; the Sorter preview's `Unknown` bucket shrinks correspondingly (still using its own resolver until the follow-up — the DB values improve either way, because DB-known files already bypass the resolver).
