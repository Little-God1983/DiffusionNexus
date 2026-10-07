# Image Editor Inpaint + Outpaint on the Diffusion Nexus Engine

Issue #606, first of milestone #2 "Editor on the Diffusion Nexus Engine" (#606 → #607 → #608). Part of #484.

## 1. Goal

Inpaint and Outpaint (non-Vision) in the Image Editor run on the app-owned embedded ComfyUI, the
**Diffusion Nexus Engine**, so they work on a machine without a ComfyUI the user runs themselves.
The workflow JSON and its node mutation stay exactly as they are. Only three things change: where
the user picks the ComfyUI that runs these tools, how the editor obtains a client for it, and how
the Engine gets the models and node pack the workflows need.

**Done when:** on a machine with no user-run ComfyUI, the user installs the Engine from the app,
ticks "Inpaint & Outpaint" in the Engine's Features dialog, then inpaints and outpaints an image in
the Image Editor. Both produce a result.

## 2. Decisions taken during brainstorming (2026-10-07)

| Topic | Decision |
|---|---|
| Where the backend is chosen | One app-wide setting in **Settings → ComfyUI Server**: a "Server" dropdown with *Diffusion Nexus Engine* (default) and *Custom URL*. No per-tool pickers. |
| What the editor shows | The readiness panel shows the active backend and its state, with a link to change it or to install what is missing. The editor holds no backend state of its own. |
| How Engine content is installed | A **Features** dialog on the Engine tile (replaces "Workloads" there): one row per app feature with a checkbox, a "Needs" column, a status, and one *Install selected* button. |
| Install bundled with the Engine? | No. The Engine install stays bare. Features are installed on demand from the dialog. |
| Status indicator elsewhere | None needed. The install tile row above the Unified Console already shows the Engine with its status. |
| Local core (sd.cpp) for these tools | Not offered. It cannot run the Qwen DiT ControlNet. |
| The June branch `feature/replace-ComfyUI-Inpainting` | Not merged. It is 996 commits behind with 11 conflicts, and its per-tool picker contradicts the decision above. Delete after #606 lands. |

## 3. Current state (develop @ 4839d7e2)

- `IComfyUIWrapperService` is one singleton built with no arguments (`App.axaml.cs:766-771`), so it
  always talks to `http://127.0.0.1:8188`. Its base URL is fixed at construction. `AppSettings.ComfyUiServerUrl`
  is read only by the readiness check (`ComfyUIFeatureBackend`), never by the jobs. Readiness can
  therefore probe one server while jobs run against another.
- `InpaintingViewModel` and `OutpaintingViewModel` receive that singleton through
  `LoraDatasetHelperViewModel → ImageEditTabViewModel → ImageEditorViewModel`. Their generate
  sequence is: upload image, resolve the GGUF model name via `GetNodeInputOptionsAsync("UnetLoaderGGUF","unet_name")`,
  queue the workflow with node modifiers, wait for completion with progress, get the result, download the image.
- `FeatureBackendRouter.DefaultRouting` is a static map sending every `Feature` to `BackendKind.ComfyUI`.
  `BackendKind` has `ComfyUI` and `LocalInference`. No `IFeatureBackend` exists for the Engine.
- `FeatureReadinessPanel.axaml` shows a dot, a status text and a Check button. `ActiveBackendName`
  exists on the view model but is not bound.
- `ManagedComfyUiEngine` (`UI/Services/Engine/`) exposes `EnsureRunningAsync(installRoot, ct)` returning
  `EngineStartResult(IsRunning, BaseUrl, FailureReason)`, plus `BaseUrl` (null when not running) and `StopAsync()`.
  It allocates a fresh loopback port per start, never 8188. The install root comes from the
  `InstallerPackage` row with `IsAppManaged = true` (resolver lambda at `App.axaml.cs:632-655`, which
  also syncs `extra_model_paths.yaml`).
- `ManagedComfyUiBackend` is the only thing that starts the Engine today. For each call it builds a
  throwaway `new ComfyUIWrapperService(_engine.BaseUrl!)`. Its node check is Krea-specific.
- The Engine tile in the Installation Manager is visible only while the Canvas hamburger switch is on
  (`IsDiffusionCanvasEnabled`, not persisted, so it is off after every launch). The tile's
  "Workloads" button opens `WorkloadsDialog` filtered to `EngineWorkloadCatalog.WorkloadIds`, which
  contains only Krea 2 Turbo.
- `WorkloadInstallationCheckerAdapter` reports an item installed if it is present in *any* ComfyUI
  install on the machine, including the Engine. That is wrong for an Engine-scoped check.
- `FeatureRegistry` maps `Feature.Inpainting` → workload `4C486765-A4C1-4E94-ACC2-BBAC0E405B6A`
  ("Inpainting-Qwen 2512") and `Feature.Outpaint` / `OutpaintVision` → `137929E4-5C05-4304-80D4-5D785D45FD3F`
  ("Outpainting-Qwen 2512"). Verified in the embedded catalog (`Assets/Catalog/catalog.zip`, workloads v5):
  - **Inpainting-Qwen 2512** = exactly the non-Vision set: node pack ComfyUI-GGUF (city96, `UnetLoaderGGUF`)
    and five models (`qwen_image_vae`, `qwen_2.5_vl_7b_fp8_scaled`, InstantX inpaint ControlNet, Lightning
    4-step LoRA, Qwen-Image-2512 GGUF with five quant links picked by VRAM tier). The non-Vision outpaint
    workflow uses this identical set.
  - **Outpainting-Qwen 2512** = the same set **plus** the Vision additions: node packs ComfyUI_Qwen3-VL-Instruct,
    ComfyUI-Custom-Scripts (pysssss), ComfyUI-KJNodes, and a model entry "Qwen 3 VL" that is a
    **placeholder with no download link**.
  So the `FeatureRegistry` mapping of plain Outpaint to the Outpainting workload over-asks: it would demand
  the Vision packs for a tool that does not use them. #606 does not use `FeatureRegistry` for the Engine.
- `IDatasetEventAggregator` has a `NavigateToSettingsRequested` event with empty args, wired in
  `App.axaml.cs:1385-1388` but published by nobody. `InpaintingViewModel` already holds the aggregator;
  `OutpaintingViewModel` does not.

## 4. Design

### 4.1 Setting: `ComfyUiServerMode`

- `AppSettings.ComfyUiServerMode` of new enum `ComfyUiServerMode { Engine, CustomUrl }`, default `Engine`.
  Stored as a string (`HasConversion<string>().HasMaxLength(32)`), matching how other enums in the
  repo are stored. `ComfyUiServerUrl` is unchanged and keeps its value regardless of mode.
- Persistence touches every place a settings column lives: entity, `AppSettingsConfiguration`, an EF
  migration generated with `dotnet ef migrations add AddComfyUiServerMode --context DiffusionNexusCoreDbContext --output-dir Migrations/Core`
  (never hand-written; CI fails on model drift), `DatabaseRecoveryService.requiredColumns`
  (`TEXT NOT NULL DEFAULT 'Engine'`), the `AppSettingsService` save whitelist, `SettingsExportData`
  (schema version 4 → 5, missing field on import = `Engine`), `SettingsExportService` export and import,
  and the raw INSERT in `publish.ps1`.
- **Settings UI.** The "ComfyUI Server" expander keeps its name and position. Above the URL row a
  "Server" ComboBox offers *Diffusion Nexus Engine* and *Custom URL*. The URL TextBox and
  *Test Connection* are enabled only for *Custom URL*. Under *Diffusion Nexus Engine* a one-line
  status reads *installed* / *not installed* / *running* with a button *Open Installation Manager*.
  The status dot reflects the selected target. Description text: "Which ComfyUI runs Inpaint and
  Outpaint in the Image Editor." (#608 extends this to Batch Upscale.) Changing the mode sets `HasChanges`
  like every other field.
- Changing the mode publishes `AppSettingsChanged` as today. Open editor panels re-run their readiness
  check on that event so the "Running on" line updates without reopening the tool.

### 4.2 `IComfyUiClientProvider`: one place that hands out a client

New interface in `DiffusionNexus.Domain.Services`:

```csharp
public interface IComfyUiClientProvider
{
    /// Returns a client for the ComfyUI selected in Settings, starting the Engine if needed.
    Task<ComfyUiClientLease> AcquireAsync(IProgress<string>? progress, CancellationToken ct);
}

public sealed record ComfyUiClientLease(IComfyUIWrapperService Client, ComfyUiServerMode Mode, string BaseUrl) : IDisposable;
```

Implementation `ComfyUiClientProvider` in `DiffusionNexus.UI/Services/Diffusion/`:

- Reads `ComfyUiServerMode` from `IAppSettingsService` on every call. No caching of the mode.
- **Engine:** resolves the install root through the same `Func<Task<string?>>` lambda the Canvas backend
  uses; if `ManagedEngineLocator.LooksInstalled` is false, throws `ComfyUiUnavailableException("Diffusion Nexus Engine is not installed")`.
  Reports `"Starting Diffusion Nexus Engine…"` on `progress`, calls `ManagedComfyUiEngine.EnsureRunningAsync`,
  and on failure throws `ComfyUiUnavailableException(result.FailureReason)`. Returns a new
  `ComfyUIWrapperService(result.BaseUrl)`; disposing the lease disposes the client. The Engine
  process itself is left running (it is stopped on app shutdown as today).
- **CustomUrl:** returns a new `ComfyUIWrapperService(settings.ComfyUiServerUrl)`. The lease disposes it.
- Logs each acquisition to the Unified Console: `LogCategory.InstanceManagement`, source
  `"Diffusion Nexus Engine"` for Engine mode (start requested / already running / started on port N / failed),
  `LogCategory.Configuration`, source `"ComfyUI"` for Custom mode.
- The per-call client is cheap (one `HttpClient` per lease). Inpaint and Outpaint are single-shot
  operations, so one lease per generate is the right granularity; the GGUF model-name lookup uses
  the same lease as the queue call.

**Consumers in #606:** `InpaintingViewModel` and `OutpaintingViewModel` take `IComfyUiClientProvider`
instead of `IComfyUIWrapperService`. Their generate methods acquire a lease at the top and dispose it in
`finally`. The null-service guards ("ComfyUI service not available…") become a catch of
`ComfyUiUnavailableException` that sets `HasError`, shows the exception message as the status, and
logs it. The constructor chain (`LoraDatasetHelperViewModel → ImageEditTabViewModel → ImageEditorViewModel`)
passes the provider through. Nothing else about the two view models changes.

**Not consumers yet:** `BatchUpscaleTabViewModel`, `ComfyUICaptioningBackend` and `ComfyUIFeatureBackend`
keep the singleton. The singleton registration is changed to `new ComfyUIWrapperService(settings.ComfyUiServerUrl)`
read once at startup, so the Settings URL finally reaches the jobs that still use it. #608 moves Batch
Upscale onto the provider and removes the singleton for image features.

### 4.3 Readiness

- `BackendKind` gains `Engine`. New `EngineFeatureBackend : IFeatureBackend` in `DiffusionNexus.UI/Services/Engine/`
  with `DisplayName = "Diffusion Nexus Engine"`. `CheckFeatureAsync(feature)`:
  1. Engine root resolves and `LooksInstalled` → else *missing requirement* "Diffusion Nexus Engine is not installed".
  2. For each workload id `EngineFeatureCatalog` lists for the feature's row (Inpainting and Outpaint both
     map to the `InpaintOutpaint` row, i.e. the Inpainting-Qwen 2512 workload only), run the existing
     `IConfigurationCheckerService.CheckAsync(configuration, engineRoot)` scoped to the Engine root
     (the same call `WorkloadsViewModel` makes), and list every missing node pack and model as a
     missing requirement. Neither `FeatureRegistry` nor the adapter that walks all ComfyUI installs is used here.
  3. It never starts the Engine. "Not running" is not a requirement; Generate starts it.
  4. Reports `IsBackendOnline = true` when installed, since "online" for the Engine means installable-and-startable.
- `IFeatureBackendRouter.Resolve(feature)` becomes mode-aware: for `Inpainting` and `Outpaint` it returns
  the Engine backend when `ComfyUiServerMode == Engine`, else the ComfyUI backend. All other features keep
  the static default (ComfyUI) until their issues. The router takes `IAppSettingsService` and reads the
  mode on every call. `FeatureReadinessResult.Backend` carries the kind so the panel can label it.
- **Readiness panel.** Below the status row a new line binds `ActiveBackendName`:
  *Running on **Diffusion Nexus Engine*** · [change]. The *change* link publishes
  `NavigateToSettings(Section: ComfyUiServer)`. When the result lists missing requirements in Engine mode,
  the line reads *Not installed on the Engine* · [Install Inpaint & Outpaint], and the link publishes
  `NavigateToEngineFeatures(preselect: EngineFeature.InpaintOutpaint)`. Existing Missing Requirements and
  Warnings boxes stay for the detail. The panel is shared by Captioning and Batch Upscale; for them the
  line simply shows their ComfyUI backend name with the same *change* link.
- Generate's `CanExecute` (`!HasChecked || IsReady`) is unchanged.

### 4.4 Engine Features dialog

- `EngineFeature` enum and `EngineFeatureCatalog` (static, in `UI/Services/Engine/`) replace
  `EngineWorkloadCatalog`:

  | Row | Display | Workload configuration ids | Shipped in |
  |---|---|---|---|
  | `InpaintOutpaint` | Inpaint & Outpaint | `4C486765-…` Inpainting-Qwen 2512 (the exact non-Vision set) | #606 |
  | `Canvas` | Canvas · Krea 2 Turbo | `E79C079A-…` Krea 2 Turbo | #606 (moved) |
  | `OutpaintVision` | Outpaint Vision | `137929E4-…` Outpainting-Qwen 2512 (adds the three Vision packs; its "Qwen 3 VL" model entry is a placeholder the catalog must fill first) | #607 |
  | `BatchUpscale` | Batch Upscale | `B853EB7C-…` | #608 |

  Each entry has `DisplayName`, `Description` (one line: which tool, which model), `WorkloadIds`,
  and `SuggestVramTier` carried over from `EngineWorkloadCatalog`. Rows not yet shipped are simply absent
  from the list. Mapping rows to whole catalog workloads keeps the app free of hard-coded model lists:
  the catalog stays the single source of what a feature needs.
- `EngineFeaturesViewModel` + `Views/Dialogs/EngineFeaturesDialog.axaml`, built on the same services as
  `WorkloadsViewModel` (`ICatalog`, `IConfigurationCheckerService`, `IWorkloadInstallService`,
  `IResourceMonitor`) and the Engine root. Per row it shows: checkbox, name + description, *Needs*
  (node packs and model count derived from the check, with the total download size of what is missing
  when the catalog has sizes), *Status*: **Installed** (nothing missing; ticked and disabled),
  **Partial · n of m models**, **Not installed**. Footer: "Selected: n features · ~X GB to download ·
  Y GB free on <drive>", *Close*, *Install selected*.
- *Install selected* iterates the selected rows' workloads in order and calls
  `IWorkloadInstallService.InstallSelectedAsync(config, engineRoot, missingNodes, missingModels, vramGb, progress, downloadProgress, skipTokenProvider, ct)`
  with exactly the items the check reported missing. Files shared between workloads (from #607 on, the
  whole Qwen set is shared between the Inpaint & Outpaint and Outpaint Vision rows) are therefore
  downloaded once: after the first install the second check reports them present. The footer becomes a progress bar with the current file name; rows
  in flight show *Installing…*; a Cancel button cancels the current download. After the run every row is
  re-checked and `EngineModelPathsSynchronizer.SyncAsync(root)` runs, as the Workloads path does today.
  Each install step logs to the Unified Console under `LogCategory.Installation`, source
  `"Diffusion Nexus Engine"`.
- The dialog opens with an optional preselected feature (from the readiness link).
- **Engine tile.** Button text "Workloads" → "Features"; `WorkloadsRequested` for an Engine card opens the
  new dialog. Non-Engine ComfyUI cards keep `WorkloadsDialog` unchanged. The tile is visible whenever
  `DiffusionFeatureFlags.UseLocalDiffusionBackend` is on (hard-coded `true` today); the coupling to
  `IsDiffusionCanvasEnabled` in `App.axaml.cs:1252-1264` and `InstallerManagerViewModel.IsEngineTileVisible`
  is removed. The Canvas module itself stays behind its switch.

### 4.5 Generate flow (Engine mode)

1. User clicks Generate. The view model acquires a lease. Status: *Starting Diffusion Nexus Engine…*
   while the cold start runs (up to ~120 s, the engine's existing poll budget). If the Engine is already
   running this step is instant.
2. Upload, resolve GGUF name, queue workflow, wait with progress, get result, download. Identical to
   today, against the lease's client.
3. The lease is disposed in `finally`. The Engine keeps running.
4. Failures: `ComfyUiUnavailableException` → status shows its message (not installed / failed to start,
   with the reason) and `HasError`. Everything else keeps the existing handling, except the fixed text
   "Generation failed – is ComfyUI running?" becomes mode-aware: "Generation failed – is the Diffusion
   Nexus Engine running?" in Engine mode.
5. Unified Console, sources `"Inpaint"` / `"Outpaint"` (`LogCategory.General`): generate requested with
   mode and workflow name, image uploaded, prompt queued with id, completed, image downloaded, or failed
   with the reason. `OutpaintingViewModel` already has `IUnifiedLogger`; `InpaintingViewModel` gets it
   through the same constructor chain.

### 4.6 Navigation

- `NavigateToSettingsEventArgs` gains `SettingsSection? Section` (`enum SettingsSection { ComfyUiServer }`
  for now). The handler in `App.axaml.cs` navigates to the Settings module and, when a section is given,
  sets `SettingsViewModel.RequestedSection`, which the view uses to expand that Expander and scroll it into view.
- New `NavigateToEngineFeaturesRequested` event with `EngineFeature? Preselect`. The handler navigates to
  the Installation Manager module and calls `InstallerManagerViewModel.OpenEngineFeaturesAsync(preselect)`.
  If the Engine is not installed, it opens the Installation Manager and shows the Engine tile's install
  state instead (the tile's *Install* button is the next step).
- `OutpaintingViewModel` receives `IDatasetEventAggregator` through `ImageEditorViewModel`, as
  `InpaintingViewModel` already does.

### 4.7 Out of scope, filed as follow-ups

- The Engine's *Start* button in the console's install tile row is a silent no-op (`ExecutablePath` is null).
  Follow-up issue: Start should call `EnsureRunningAsync`, Stop should call `StopAsync`.
- Captioning on the Engine (a `Captioning` row in the Features dialog) is not in milestone #2.
- Catalog data: the two Qwen workload descriptions still say "running as a Diffusion Nexus Core
  capability", and the Outpainting workload's "Qwen 3 VL" model has no download link. Both are catalog
  edits (repo `Into-The-Latent/DiffusionNexus.Catalog`), needed by #607, not by #606.
- Outpaint Vision (#607), Batch Upscale (#608), the C# graph builder, custom-node elimination, porting
  Inpaint onto `IDiffusionBackend`, any workflow rewrite, the Canvas moving onto the global setting.

## 5. Error handling summary

| Situation | Where it shows | Text |
|---|---|---|
| Engine mode, Engine not installed | Readiness panel | *Not installed on the Engine* · Install Inpaint & Outpaint; Missing Requirements: "Diffusion Nexus Engine is not installed" |
| Engine mode, models or GGUF pack missing | Readiness panel | *Not installed on the Engine* · Install Inpaint & Outpaint; each missing file listed |
| Engine fails to start on Generate | Tool panel status, Unified Console | "Diffusion Nexus Engine failed to start: <reason>" |
| Custom mode, server offline | Readiness panel (existing) | *Server offline* · Running on ComfyUI · change |
| Custom mode, generate fails | Tool panel status (existing) | unchanged |
| Settings mode changed while a panel is open | Readiness panel | re-checks on `AppSettingsChanged` |

## 6. Testing

Unit tests (`DiffusionNexus.Tests`):

- `ComfyUiClientProvider`: Engine mode calls `EnsureRunningAsync` once and returns a client on its URL;
  not-installed and start-failure throw `ComfyUiUnavailableException` with the reason; Custom mode returns
  a client on the settings URL and never touches the engine; disposing the lease disposes the client.
  (`ManagedComfyUiEngine` is sealed; the provider depends on a small `IManagedComfyUiEngine` extracted
  from it: `EnsureRunningAsync`, `BaseUrl`.)
- `FeatureBackendRouter`: Inpainting/Outpaint resolve to Engine or ComfyUI by mode, read per call;
  other features unaffected.
- `EngineFeatureBackend`: not installed → one missing requirement; checker results map to missing
  requirements; the Engine is never started.
- `EngineFeatureCatalog`: every shipped row maps to workload ids that exist in the embedded catalog manifest.
- `EngineFeaturesViewModel`: status derivation (Installed / Partial / Not installed), selection and
  footer totals, install passes only the missing items, preselect.
- Settings: `ComfyUiServerMode` round-trips through save, export and import; import of a v4 file yields `Engine`.
- `SettingsViewModel`: mode change sets `HasChanges`; URL controls enabled only in Custom mode.

Manual verification on the BenQ monitor (BNQ7F05), the issue's "Done when":

1. Settings shows the Server dropdown defaulting to Diffusion Nexus Engine; URL row disabled.
2. Installation Manager shows the Engine tile without touching the Canvas switch. Install the Engine.
3. Features dialog lists Inpaint & Outpaint (Not installed) and Canvas · Krea 2 Turbo. Tick the first,
   Install selected; progress visible; both rows re-checked afterwards.
4. Image Editor → Inpaint: panel reads Ready · Running on Diffusion Nexus Engine. Generate: status shows
   the Engine starting, then progress, then the result lands on the canvas. Repeat for Outpaint.
5. Unified Console shows the Engine start, the install steps and the generate steps.
6. Switch to Custom URL pointing at a stopped 8188: panel re-checks and reads Server offline.

## 7. Files touched (expected)

- Domain: `Enums/BackendKind.cs`, new `Enums/ComfyUiServerMode.cs`, `Entities/AppSettings.cs`,
  `Models/SettingsExportData.cs`, `Services/IFeatureBackendRouter.cs`, `FeatureBackendRouter.cs`,
  new `Services/IComfyUiClientProvider.cs`, `Services/UnifiedLogging` (no change expected).
- DataAccess: `Configurations/AppSettingsConfiguration.cs`, new migration + snapshot,
  `Recovery/DatabaseRecoveryService.cs`.
- Service: `AppSettingsService.cs`, `SettingsExportService.cs`.
- UI: `App.axaml.cs` (DI, singleton URL, event handlers, tile gating), `Services/Diffusion/ComfyUiClientProvider.cs`,
  `Services/Engine/IManagedComfyUiEngine.cs`, `EngineFeatureBackend.cs`, `EngineFeatureCatalog.cs`
  (replacing `EngineWorkloadCatalog.cs`), `Services/DatasetEventAggregator.cs`,
  `ViewModels/SettingsViewModel.cs`, `Views/SettingsView.axaml`, `ViewModels/FeatureReadinessViewModel.cs`,
  `Views/Controls/FeatureReadinessPanel.axaml`, `ViewModels/InpaintingViewModel.cs`, `OutpaintingViewModel.cs`,
  `ImageEditorViewModel.cs`, `Tabs/ImageEditTabViewModel.cs`, `LoraDatasetHelperViewModel.cs`,
  `ViewModels/InstallerManagerViewModel.cs`, `InstallerPackageCardViewModel.cs`, `Views/InstallerManagerView.axaml`,
  new `ViewModels/EngineFeaturesViewModel.cs` + `Views/Dialogs/EngineFeaturesDialog.axaml`.
- `publish.ps1` (AppSettings INSERT), `.gitignore` (`.superpowers/`).
