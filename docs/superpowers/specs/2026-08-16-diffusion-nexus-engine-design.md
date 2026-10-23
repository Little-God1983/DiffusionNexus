# Diffusion Nexus Engine — Step 1 design

**Date:** 2026-08-16
**Issue:** [#484](https://github.com/Little-God1983/DiffusionNexus/issues/484) — hybrid inference architecture
**Repos touched:** `DiffusionNexus` (main app). SDK consumed as published NuGet packages; no SDK source change planned.

## Context

Issue #484 decided a two-engine hybrid. That decision has since been narrowed: **every AI
workflow moves to an app-owned embedded ComfyUI**, and the only local runtimes that survive are
ONNX Runtime for the gallery's WD14 tagger and background removal. sd.cpp, LLamaSharp captioning
and user-managed ComfyUI all become legacy paths to retire later.

This spec covers **Step 1** only: standing the embedded engine up, installing a real workload
into it, and letting the Diffusion Canvas generate through it. Nothing is removed in this step.

The Canvas stays hidden behind the existing hamburger switch
(`DiffusionNexusMainWindowViewModel.IsDiffusionCanvasEnabled`), and the new engine tile is bound
to the same switch.

## Goals

1. The Installation Manager gains a static **Diffusion Nexus Engine** tile that installs an
   app-owned ComfyUI through the Installer SDK.
2. That engine can have curated **workloads** installed into it on demand. Krea 2 Turbo is the
   first, using the existing `Krea-2-Turbo` catalog configuration.
3. The Diffusion Canvas gains a **backend dropdown**; selecting the engine generates a Krea 2
   image through it.
4. Models are **not duplicated** — the engine reuses the registered shared model library.

## Non-goals for this step

- Removing or disabling sd.cpp, LLamaSharp captioning, or user-managed ComfyUI.
- Migrating Inpaint / Outpaint / Batch Upscale to the engine.
- A C# workflow graph builder, ControlNet support, VRAM arbitration between engines.
- Eliminating custom nodes (issue #484 Workstream 4). The Krea 2 workload installs as authored.
- Linux/macOS.

## Decisions

| # | Decision | Rationale |
|---|---|---|
| 1 | Engine is a **two-stage** system: base engine install, then per-workload installs | Extensible — each further workload is a catalog entry, not another full install |
| 2 | Engine base **inherits whatever torch/CUDA the base configuration declares** | The maintainer named CUDA 13.0 + torch 2.11.0 as the preferred pairing, but see the correction below — the shipped catalog declares 12.8 + 2.8.0 |
| 3 | Install target is a **user-chosen folder**, default `%LocalAppData%\DiffusionNexus\Engine\ComfyUI` | ~5–8 GB with torch; forcing C: has burned us before |
| 4 | Engine is stored as an `InstallerPackage` row with `Type = ComfyUI` plus a new `IsAppManaged` flag, hidden from the ordinary card list | Reuses model-root resolution, `extra_model_paths.yaml` discovery and the Base Model Folder registrar for free, without a duplicate card or a user-facing Remove/Edit path |
| 5 | Workload models **reuse the shared model library**; only missing files download, into the starred default Base Model Folder | No second copy of the Krea GGUF / text encoder / VAE |
| 6 | Canvas workflow template is an **open slot** — the maintainer supplies the text2image workflow | Deliberately deferred; the catalog's shipped workflow is UI-format and unusable as-is (see below) |
| 7 | Install progress streams to the **Unified Console** | Matches the existing installer Update flow and the standing "log every working step" rule |
| 8 | Engine tile visibility is bound to `IsDiffusionCanvasEnabled` | Both surfaces stay hidden until the feature is ready |

### Why the catalog workflow cannot be used directly

`1.Krea2-Turbo-Text2Image+Upscale` is stored in **UI (frontend) format**: 30 nodes including
`GetNode`/`SetNode` virtual links, `Power Lora Loader (rgthree)`, `Fast Groups Bypasser (rgthree)`
and `AI2GoResolutionSelector`. ComfyUI's `/prompt` endpoint accepts only **API format**. Runtime
UI→API conversion would mean reimplementing the ComfyUI frontend's link resolution, so the
maintainer supplies a text2image workflow instead; the backend patches parameters into it.

## Prerequisite

Bump the main app's SDK package references **1.2.36 → 1.2.39** (`Models`, `Services`,
`DataAccess`, `Shared`, `Database` in `DiffusionNexus.UI.csproj`). `v1.2.39` is the latest
published tag; its publish workflow succeeded on 2026-08-14. Build and run the full suite before
any feature work, so an SDK-caused failure is never confused with a feature-caused one.

## Architecture

```
Installation Manager                      Diffusion Canvas
  └─ Diffusion Nexus Engine tile            └─ Backend dropdown
       ├─ Stage 1: base engine                   ├─ Diffusion Nexus Core (sd.cpp, unchanged)
       │    └─ ManagedEngineInstaller            └─ Diffusion Nexus Engine
       │         └─ SDK IInstallationCoordinator      └─ ManagedComfyUiBackend : IDiffusionBackend
       └─ Stage 2: workload list                           ├─ ManagedComfyUiEngine (process host)
            └─ WorkloadInstallService (existing)           └─ ComfyUIWrapperService (existing)
```

### Stage 1 — base engine (`ManagedEngineInstaller`)

New service in `DiffusionNexus.UI/Services/Engine/`, modelled on
`DiffusionNexus.IntegrationRunner.Core/CoordinatorWorkloadInstaller.cs` in the Installers repo.

Flow: resolve the base configuration → `EvaluateGpuGateAsync` → `RunPreChecksAsync` →
`InstallAsync` → persist the `InstallerPackage` row.

The base configuration is the **`Krea-2-Turbo` configuration** (`E79C079A-2FD7-4FE7-8086-23731092555D`)
run with `InstallationOptions.ExcludedModelIds`, `ExcludedNodeIds` and `ExcludedWorkflowIds`
filled with every id it declares. That yields ComfyUI + venv + the configuration's torch + Triton +
SageAttention and no content — the decision-2 pairing without authoring a new catalog entry.
Shortcuts are forced off (`CreateDesktopShortcut = false`, `CreateStartMenuShortcut = false`):
the engine is not a user-launchable app.

`GenerateExtraModelPaths = true` with the registered shared model roots, per decision 5.

`IUserPromptService` is implemented as a thin shim over the app's existing `IDialogService`
(3 methods: `ConfirmAsync`, `ShowErrorAsync`, `ShowInfoAsync`). Against a fresh empty folder the
pre-checks are trivial; the one prompt that can genuinely fire is the GPU gate's CPU-only offer.

**Known constraint:** the engine's torch/CUDA is fixed by this base config. A future workload
requiring a different torch needs an explicit policy (rebuild the engine, or a second engine).
Out of scope here, recorded so it is not discovered by accident.

### Stage 2 — workload catalogue

The tile lists engine workloads from a **curated allow-list of configuration GUIDs** held in the
app (Krea-2-Turbo only, initially). Each row resolves its configuration through the
`IConfigurationRepository` the Installer Manager already injects, and reports installed/missing
state through the existing `ConfigurationCheckerService`.

Installing a workload calls the app's existing `WorkloadInstallService.InstallSelectedAsync`,
which shallow-clones the custom nodes, pip-installs their requirements into the engine's venv
(`{repo}/venv/Scripts/python.exe` — the path that service already resolves for non-portable
installs), and downloads missing models at the selected VRAM tier.

VRAM tier is auto-detected via the SDK's GPU detection and overridable in the UI. The Krea 2
workload declares tiers 8/12/16/24/32 GB mapping to Q3_K_S … Q8_0 GGUF quantizations.

The Krea 2 workload installs **as authored**, including ComfyUI-Manager and Crystools. Trimming
the node set belongs to issue #484 Workstream 4, not here.

### Engine runtime (`ManagedComfyUiEngine`)

Starts the engine's own venv Python running `main.py` with `--listen 127.0.0.1`,
`--port <dynamically allocated free port>` and `--disable-auto-launch`, as a hidden child process
using the `PackageProcessManager` job-object pattern so it cannot outlive the app. Never port
8188 — a user's own ComfyUI must never be collided with. Readiness is a `/system_stats` poll with
a timeout. Start is on demand (first Canvas generation against the engine); stop is on app exit.

`ComfyUIWrapperService` currently resolves its base URL from a constant. It gains a way to be
pointed at the engine's allocated port. This is also the pre-existing bug noted in issue #484's
incidental findings (the user's `ComfyUiServerUrl` setting never reaches the wrapper).

### Canvas

`DiffusionCanvasViewModel` gains a `Backend` selection bound to a two-entry list: the existing
local backend and the engine. Selection is in-memory for this step (not persisted to AppSettings).
Everything else about the Canvas is unchanged.

`ManagedComfyUiBackend` implements the existing `IDiffusionBackend` seam:

- `DisplayName` — "Diffusion Nexus Engine".
- `Catalog` — models discovered under the engine's resolved model roots.
- `IsAvailableAsync` — engine installed, workload installed, process healthy. Each unmet
  condition is a distinct `MissingRequirements` entry, so the Canvas message says which step is
  missing. Caller cancellation propagates as `OperationCanceledException` per the seam's
  documented contract (issue #434).
- `GenerateAsync` — loads the supplied workflow template, patches prompt / seed / width / height /
  steps / cfg, queues it through `ComfyUIWrapperService`, maps WebSocket progress onto
  `DiffusionProgress`, and yields the final image. Failures surface as a completed item carrying a
  message (the error-as-data contract the Canvas already relies on), not as exceptions.

Until the workflow template is supplied, `GenerateAsync` reports "no workflow configured" through
that same path.

## Data model change

`InstallerPackage` gains `bool IsAppManaged` (default `false`) with an EF migration under
`DiffusionNexus.DataAccess/Migrations/Core`. Consumers:

- Installation Manager filters app-managed rows out of the ordinary card list and renders the
  static tile instead.
- Everything else (model-root resolution, base-folder registrar, gallery linking) treats the row
  as the ordinary ComfyUI installation it is.

## Error handling

- Install failure: the SDK's truthful per-operation report is written to the Unified Console, the
  card shows the failure, and the user is offered the existing feedback dialog.
- Cancellation: `InstallationResult.IsCancelled` is honored — a deliberate abort must not trigger
  failure-reporting UI.
- Engine start failure / health-check timeout: reported as a Canvas `MissingRequirements` entry
  naming the actual cause, never a generic "backend unavailable".
- A partially installed engine (row present, folder gone) is detected the same way ordinary cards
  detect it (`Directory.Exists`) and offers reinstall.

## Testing

Unit tests:

- Base-install options: every declared model/node/workflow id lands in the corresponding
  `Excluded*Ids` set, shortcuts off, extra-model-paths on (mocked `IInstallationCoordinator`, as
  the Installers repo's `CoordinatorWorkloadInstallerTests` already do).
- App-managed row filtering in `InstallerManagerViewModel`.
- Workload allow-list → configuration resolution → checker state mapping.
- Canvas backend selection, and each `MissingRequirements` case.
- Workflow parameter patching, once the template exists.

Manual smokes (explicitly not claimed from mocks): a real base engine install, a real Krea 2
workload install reusing an existing model library, and a first Krea 2 image generated on the
Canvas.

## Sequencing

1. SDK bump 1.2.36 → 1.2.39, suite green.
2. `IsAppManaged` column + migration.
3. Stage 1: `ManagedEngineInstaller`, SDK DI registration, prompt shim, engine tile with install +
   Unified Console progress.
4. Stage 2: workload list, VRAM tier, install through `WorkloadInstallService`.
5. `ManagedComfyUiEngine` process host + `ComfyUIWrapperService` port plumbing.
6. Canvas dropdown + `ManagedComfyUiBackend` (workflow slot open).
7. Workflow template lands → parameter patching → first real generation.

## Open items

- The text2image workflow template (maintainer-supplied).
- **The engine's actual torch/CUDA pairing.** Decision 2 was written as "CUDA 13.0 + torch 2.11.0",
  the pairing the maintainer named. The final whole-branch review caught that the `Krea-2-Turbo`
  configuration does not declare it: both the SDK's shipped database (1.2.39) and the live
  `%LocalAppData%\diffusion_nexus.db` declare **CUDA 12.8 + torch 2.8.0**. The live database's copy of
  that row read 13.0 + 2.11.0 at the start of this work and read 12.8 + 2.8.0 afterwards, stamped
  `DbVersion 1.2.41` — consistent with the embedded-DB auto-deploy overwriting a hand-edited row.
  The code is correct either way: it authors no torch settings and inherits whatever the configuration
  declares, and it now logs the resolved values at install start. What remains open is a data decision
  for the maintainer: update the catalog row to 13.0 + 2.11.0, or accept 12.8 + 2.8.0 deliberately.
  The spec's own note that this is effectively a one-way door still stands.
- Torch policy if a future workload disagrees with whatever pairing is settled on.
- Whether the backend selection should later persist to AppSettings and replace
  `DiffusionFeatureFlags.UseLocalDiffusionBackend` (its own `TODO(v2-backend-dropdown)`).
