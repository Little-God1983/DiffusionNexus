# Batch Metadata Distiller — Design

**Date:** 2026-07-13
**Repo:** `e:\Repos\DiffusionNexus` (project: `DiffusionNexus.UI`)
**Status:** Approved design — ready for implementation planning

---

## 1. Goal

A new Workflows-gallery tile, **Batch Metadata Distiller**, that lets a user:

1. Load a batch of loose images (reusing the shared image-input control).
2. Auto-detect, on load, how many images carry embedded generation metadata (ComfyUI `prompt`
   chunk or A1111 `parameters`), shown as a running tally.
3. Recover the *real* generation parameters per image — positive/negative prompt, steps, CFG,
   sampler, scheduler, seed, checkpoint, and **all LoRAs including rgthree Power Lora Loader and
   Lora Loader Stack** — and hand-curate them in an editable middle panel.
4. Apply batch-wide automation: multiple named **delete** (blacklist) and **replace** rule sets.
5. Write out **clean, CivitAI-readable copies** into a user-picked folder, with an A1111
   `parameters` chunk (prompt + `<lora:...>` tokens + Model), optionally with resource hashes, and
   **optionally** stripped of the embedded ComfyUI workflow.

Plus a small, related UI change: a **horizontal divider** in the Workflows gallery that groups
generation workflows apart from the new utility workflow.

The extraction algorithm is specified in `comfyui-metadata-extraction-for-csharp.md` (repo root).
This design ports that algorithm and wraps it in a run screen.

---

## 2. Scope

### In scope
- New gallery tile + bespoke three-column run screen.
- ComfyUI `prompt`-chunk tracer with full rgthree Power Lora / Lora Stack support, save-node→sampler
  BFS, load-order reversal, and the disabled/`"None"`/zero-strength skip rules (per the spec).
- Reading images already saved by the **AI2Go "Save Metadata (Civitai)"** node (and any A1111/Forge/
  Civitai image): its output is a standard A1111 `parameters` chunk with LoRAs as `<lora:name:strength>`
  tokens. The A1111 read path normalizes these — LoRA tokens are extracted into the LoRA list and
  removed from the stored prompt — so re-distilling such an image does not duplicate the tokens.
- Per-image editable metadata + per-LoRA include toggles.
- Multiple named delete/replace rule sets applied batch-wide.
- A1111 `parameters` formatter with `<lora:...>` tokens and Model, plus an optional `Hashes:` block.
- Output writer: user-picked folder; **optional** workflow-strip; originals untouched.
- Unit tests for the pure logic (tracer, rule engine, formatter, writer).

### Out of scope (v1)
- GPU / inference of any kind — this workflow does no rendering.
- JPEG/WebP **output** with metadata embedded in EXIF. JPEG/WebP inputs are still *read* for
  detection; **PNG is the first-class output format** (matches ComfyUI's default and the existing
  PNG chunk writer). A JPEG/WebP output path is a defined future extension (§11).
- Editing the CivitAI resource database or calling the CivitAI API. "CivitAI-readable" means only
  "emits the A1111 `parameters` metadata CivitAI's image parser consumes."
- Re-ordering LoRAs by hand (load order is derived and displayed read-only in order; you may toggle
  inclusion and edit strength, but not reorder).

---

## 3. Key decisions (resolved with the user)

| # | Decision | Choice |
|---|----------|--------|
| D1 | Output destination | **User-picked output folder** each run. Originals untouched. |
| D2 | Output metadata format | A1111 `parameters` tEXt chunk (prompt + `<lora:...>` + Model). No JSON sidecar. |
| D3 | Automation model | **Multiple named delete/replace rule sets**, each toggleable, applied batch-wide; per-image manual edits override the automated result. |
| D4 | Workflow stripping | **Optional toggle** (default on). Off = keep the embedded ComfyUI chunks, still add the A1111 `parameters`. |
| D5 | LoRA/checkpoint recognition | `<lora:name:strength>` tokens + Model stem always (spec's required path). **Optional** "compute resource hashes" toggle fills an A1111 `Hashes:` block from the local catalogs when a file is found; silently falls back to name-only. |
| D6 | Per-LoRA include toggles | Yes — each detected LoRA has an include toggle in the editor (disabled/skipped ones are shown but off by default per the spec's skip rules). |
| D7 | Gallery divider | A **labelled group divider** ("Utilities") separating generation workflows from utility workflows, driven by a manifest `category`. |
| D8 | Run-screen architecture | **Bespoke run VM** that does *not* extend the generation-oriented `PipelineRunViewModel`; both share a thin base so the gallery can host either. |

---

## 4. Architecture

### 4.1 Gallery integration

Tiles are data-driven from JSON manifests in `Assets/Pipelines/`, loaded by
`PipelineManifestProvider` (which has a hardcoded `ManifestIds` allow-list), filtered by
`ShowInGallery`, and hosted by `PipelinesViewModel` / `PipelinesView`. Four touch points:

**a. Manifest** — new `Assets/Pipelines/batch-metadata-distiller.json`:
```jsonc
{
  "id": "batch-metadata-distiller",
  "title": "Batch Metadata Distiller",
  "description": "Recover ComfyUI generation data (prompt, sampler, CFG, LoRAs incl. Power Lora / Lora Stack) and re-save clean, CivitAI-readable copies.",
  "showInGallery": true,
  "category": "Utilities",     // NEW field; existing manifests default to "Generation"
  "requiresModels": false,     // NEW field; default true
  "icon": "Icons/metadata-distiller.jpg",
  "assets": []                 // no downloadable models
}
```
Register `"batch-metadata-distiller"` in `PipelineManifestProvider.ManifestIds`. Add `Category`
(default `"Generation"`) and `RequiresModels` (default `true`) to `PipelineManifest`.

**b. Readiness-gate bypass** — `PipelinesViewModel.OpenPipelineInternalAsync` currently resolves the
ComfyUI models root and refuses to open when absent. Add an early branch: when
`tile.Manifest.RequiresModels == false`, skip the root/readiness check entirely and call
`OpenRun(tile, inputImages)` directly. The readiness badge for such tiles is fixed to `Ready`
(`RefreshAllStatusesAsync` short-circuits when `!RequiresModels`).

**c. Shared run abstraction (D8)** — realized as a thin **interface** rather than a base class:
`PipelineRunViewModel` already exposes `Title`, `CloseRequested`, `ResourceMonitor`,
`LoadInputImages`, and `Dispose`, so it satisfies the interface with **zero member changes** (lower
risk than re-parenting the large existing VM):
```csharp
public interface IPipelineRun : IDisposable
{
    string Title { get; }
    event EventHandler? CloseRequested;
    ResourceMonitorViewModel? ResourceMonitor { get; set; }   // distiller accepts + ignores it
    void LoadInputImages(IReadOnlyList<string> paths);
}
```
- `PipelineRunViewModel` gains `, IPipelineRun` on its declaration only (already has every member).
- `BatchMetadataDistillerViewModel : ViewModelBase, IPipelineRun` implements it directly (no
  generation machinery).
- Retype `PipelinesViewModel.ActiveRun`, the DI factory delegate
  (`Func<PipelineTileViewModel, IPipelineRun>`), and `OpenRun` to `IPipelineRun`.

**d. View selection** — `PipelinesView.axaml` currently hosts the single `PipelineRunView` bound to
`ActiveRun`. Replace with a `ContentControl Content="{Binding ActiveRun}"` resolved via DataTemplates
(or the app ViewLocator): `PipelineRunViewModel → PipelineRunView`,
`BatchMetadataDistillerViewModel → BatchMetadataDistillerView`. Register the factory case in
`App.axaml.cs`:
```csharp
"batch-metadata-distiller" =>
    ActivatorUtilities.CreateInstance<BatchMetadataDistillerViewModel>(sp, tile.Manifest),
```

**e. The divider (D7)** — add `Category` to the tile VM. In `PipelinesView.axaml`, render the gallery
as grouped sections: expose `GenerationPipelines` and `UtilityPipelines` (or a grouped view) on
`PipelinesViewModel`, render two `ItemsControl`s, and between them a labelled rule:
```xml
<Grid ColumnDefinitions="Auto,*" Margin="0,4,0,10"
      IsVisible="{Binding HasUtilityPipelines}">
  <TextBlock Text="UTILITIES" FontSize="11" Opacity="0.6"
             Classes="sectionLabel" VerticalAlignment="Center"/>
  <Border Grid.Column="1" Height="1" Background="#40FFFFFF"
          Margin="10,0,0,0" VerticalAlignment="Center"/>
</Grid>
```
(matches the app-wide `#40FFFFFF` 1px divider idiom). The second group is hidden when empty.

### 4.2 The run screen (three columns)

`BatchMetadataDistillerView.axaml` + `BatchMetadataDistillerViewModel`:

- **Top bar**: Back, title, and a live detection tally — `"{WithMetadataCount} / {TotalCount} images
  have embedded metadata"`.
- **Left — input & detection**: the shared `ImageListInputControl` (drag/browse) bound to
  `ImagePaths`/`SelectedImagePath`. Each loaded image becomes a `DistillerItemViewModel` and is parsed
  on a background task; thumbnails carry a badge (has-metadata / none) and a LoRA chip. Grey/no-metadata
  items are excluded from the run by default.
- **Middle — per-image editor**: bound to the selected item. Editable positive/negative prompt,
  Steps, CFG, Sampler, Scheduler, Seed, Model. Below: detected LoRAs in load order (Power Lora / Lora
  Stack aware), each with an include toggle, editable strength, a source badge, and a local-catalog
  match indicator (✓ local / ⚠ not found).
- **Right — automation & output**: the rule-set manager (create/rename/toggle/delete named delete and
  replace sets, each with its own word list / replacement pairs); an "Apply rules" affordance that
  refreshes the derived prompts; the optional **Strip ComfyUI workflow** toggle; the optional
  **Compute resource hashes** toggle; the output-folder picker; and the **Distill N images** run button
  with progress + cancel.

The VM owns: `ObservableCollection<DistillerItemViewModel> Items`, `SelectedItem`,
`ObservableCollection<PromptRuleSetViewModel> RuleSets`, `bool StripWorkflow`, `bool ComputeHashes`,
`string? OutputFolder`, progress/cancellation, and the `Distill` command. It has **no** LoRA-generation
picker, image-influence, or test/all-generate machinery.

### 4.3 Core services (pure, testable — the heart of the feature)

All four live in `DiffusionNexus.UI/Services/Distiller/` (or promoted to `DiffusionNexus.Service` if we
later need them outside the UI assembly; see §9). They contain no Avalonia types.

1. **`ComfyUiPromptTracer`** — ports `tracer.py` (spec §3). Input: the parsed `prompt` JSON graph.
   Output: `ImageGenerationData` (reused) plus a raw-LoRA list. Responsibilities:
   - Pick the starting sampler: save-node→sampler BFS, else single-sampler fallback (spec §3.1).
   - Read sampler scalars literal-only; `seed ?? noise_seed` (§3.2).
   - Resolve positive/negative via BFS to `CLIPTextEncode`, following linked `text` (§3.3).
   - Walk the model chain; handle `LoraLoader`/`LoraLoaderModelOnly`, **Power Lora Loader (rgthree)**,
     **Lora Loader Stack (rgthree)**, `MODEL_SOURCES` checkpoint/UNET loaders, and pass-through nodes;
     apply skip rules; **reverse** for load order (§3.4–3.5).
   This is a new class rather than an edit to `ImageMetadataParser` so the graph algorithm is testable
   in isolation and mirrors the Python reference 1:1. `ImageMetadataParser.ParseComfyUiGraph` is
   refactored to delegate to it (its current, weaker walk is replaced).

2. **`PromptRuleEngine`** — applies an ordered list of enabled rule sets to a prompt string. Delete =
   remove matching words (default: whole-word, case-insensitive; tidy up doubled separators/commas).
   Replace = substitute x→y (same matching options). **LoRA `<lora:...>` tokens are protected** — they
   are stripped out, rules applied to the remaining text, then re-appended, so a blacklist can't
   mangle a LoRA name. Pure function: `string Apply(string prompt, IReadOnlyList<PromptRuleSet> sets)`.

3. **`A1111MetadataFormatter`** — builds the A1111 `parameters` string from an `ImageGenerationData` +
   the final (curated + rule-applied) prompts + included LoRAs + options (spec §4.2):
   ```
   <positive> <lora:Name:Strength> …
   Negative prompt: <negative>
   Steps: N, Sampler: <a1111 name>, Schedule type: <sched>, CFG scale: N, Seed: N, Size: WxH, Model: <stem>[, Model hash: <autov2>]
   [Hashes: {"model":"…","lora:Name":"…"}]
   ```
   Includes a small ComfyUI→A1111 sampler-name lookup (`euler/normal → Euler`,
   `dpmpp_2m/karras → DPM++ 2M Karras`, …), passing unmapped names through. The `Hashes:` block and
   `Model hash:` are only emitted when hashes were computed (D5).

4. **`ImageResourceHasher`** — given a LoRA/checkpoint stem, resolves the file via the local catalogs
   (`ILoraCatalog` for LoRAs; `ComfyUiModelCatalog` for checkpoints/UNETs) and computes **AutoV2**
   (first 10 hex chars of SHA-256). Cached by `(path, size, mtime)` so a batch hashes each file once.
   Only invoked when D5's toggle is on; missing files return null → name-only fallback.

**Output writing** — extend `PngMetadataWriter` (or wrap it in a `DistilledImageWriter`):
- Add a `stripExisting` flag to `CopyWithMetadata`. `stripExisting: true` = current behaviour (drop all
  tEXt/iTXt, also extend to drop `zTXt`, write the new `parameters`). `stripExisting: false` = keep
  existing chunks, drop/replace only an existing `parameters` chunk, leave `prompt`/`workflow` intact.
- Replace the placeholder `FormatAsA1111Parameters` body for the distiller by routing through
  `A1111MetadataFormatter` (keep the old signature for its existing caption-baking caller, or migrate
  that caller too).

### 4.4 Orchestration

A `MetadataDistillerService` (or a private method on the VM delegating to the pure services) ties it
together per run: for each included item → take curated fields → `PromptRuleEngine.Apply` →
`A1111MetadataFormatter.Build` (with optional hashes) → `DistilledImageWriter` into the output folder,
honoring the strip toggle. Reports progress; cancellable; per-item failures are collected and surfaced
without aborting the batch.

---

## 5. Data flow

```
Load images (ImageListInputControl)
   → per item: PngChunkReader → ImageMetadataParser → ComfyUiPromptTracer → ImageGenerationData
   → badge each item, update "N / M have metadata" tally
Curate: select item → edit fields / toggle LoRAs   (per-image overrides)
Automate: define & enable rule sets                 (batch-wide)
Run: for each included item →
   curated prompts → PromptRuleEngine.Apply(ruleSets)
   → A1111MetadataFormatter.Build(data, prompts, loras, hashOptions)
   → DistilledImageWriter.Write(src, outFolder, params, strip: StripWorkflow)
```

---

## 6. Data model

**Reused:** `ImageGenerationData` (add nothing; it already has prompt/negative/checkpoint/loras/
sampler/scheduler/steps/seed/cfg/denoise). `LoraInfo` (add an optional raw `File`/source field if
needed for hashing/skip-source display — a minimal, additive change).

**New (view/model):**
- `DistillerItemViewModel` — wraps one input image: `Path`, `Thumbnail`, `HasMetadata`,
  `HasLoras`, `IncludeInRun`, the parsed `ImageGenerationData`, and editable working copies of the
  fields + `ObservableCollection<DistillerLoraViewModel>`.
- `DistillerLoraViewModel` — `Name`, `Strength`, `SourceLabel` (Power Lora / Lora Stack / LoraLoader),
  `Include`, `FoundLocally`.
- `PromptRuleSet` (model) — `Name`, `RuleKind { Delete, Replace }`, `Enabled`, and entries
  (`IReadOnlyList<string>` for delete; `IReadOnlyList<(string From, string To)>` for replace) +
  `PromptRuleSetViewModel` for the editor.
- `DistillOptions` — `StripWorkflow`, `ComputeHashes`, `OutputFolder`.

---

## 7. Error handling & edge cases

- **No metadata** (other tools, `--disable-metadata`, re-saved): item badged grey, excluded from run
  by default; fail soft, never throw (spec §5.8).
- **`prompt` chunk missing but `parameters` present**: use the existing A1111 reader path; still
  editable and distillable.
- **Unresolved fields** (linked scalars): leave blank/omitted from the A1111 string rather than
  emitting a raw `[id,slot]` (spec §5.3).
- **Disabled / `"None"` / zero-strength LoRAs**: skipped by the tracer's rules; shown in the editor as
  off so the user can see they existed (spec §5.5).
- **LoRA not in local catalog** (hashing on): emit the `<lora:...>` name token, omit its hash, flag
  ⚠ in the UI (D5).
- **Output folder unset / unwritable**: run button disabled until a valid folder is picked; write
  errors per item are collected and reported, batch continues.
- **Same-name collisions in output**: never overwrite silently; de-duplicate (`name (2).png`) or
  require an empty/confirmed folder (decide in planning; default = de-duplicate).
- **Non-PNG input**: read for detection; v1 output re-saves as PNG (or the item is flagged as
  "PNG output only" — decide in planning).

---

## 8. Testing strategy

Unit tests target the four pure services (no UI). Fixtures mirror the Python reference set (spec §5):
plain checkpoint; `LoraLoader` chain; `LoraLoaderModelOnly` behind a pass-through; **Power Lora Loader**
with enabled/disabled/`"None"` slots; **Lora Loader Stack** with `"None"` + zero-strength; mixed Power
Lora + stock loader load-order; `UNETLoader` diffusion model. Plus:
- `PromptRuleEngine`: delete removes whole words only; replace pairs; LoRA tokens survive both.
- `A1111MetadataFormatter`: token emission, sampler-name mapping, `Hashes:` block on/off.
- `PngMetadataWriter`: strip-on drops `prompt`/`workflow`/`zTXt` and writes `parameters`; strip-off
  preserves `prompt`/`workflow` and replaces only `parameters`; round-trips through `ImageMetadataParser`.

No dedicated UI/VM tests beyond what the existing suite covers (the VM is thin orchestration over the
tested services).

---

## 9. File-by-file change list

**New**
- `Assets/Pipelines/batch-metadata-distiller.json` (+ tile icon)
- `Services/Distiller/ComfyUiPromptTracer.cs`
- `Services/Distiller/PromptRuleEngine.cs`
- `Services/Distiller/A1111MetadataFormatter.cs`
- `Services/Distiller/ImageResourceHasher.cs`
- `Services/Distiller/DistilledImageWriter.cs` (or extend `PngMetadataWriter`)
- `Services/Distiller/MetadataDistillerService.cs`
- `ViewModels/Pipelines/BatchMetadataDistillerViewModel.cs`
- `ViewModels/Pipelines/DistillerItemViewModel.cs`, `DistillerLoraViewModel.cs`,
  `PromptRuleSetViewModel.cs`
- `Models/Distiller/PromptRuleSet.cs`, `DistillOptions.cs`
- `Views/Pipelines/BatchMetadataDistillerView.axaml` (+ `.axaml.cs`)
- Tests under `DiffusionNexus.Tests` for the four pure services.

**Edited**
- `Models/Pipelines/PipelineManifest.cs` — add `Category` (default "Generation"), `RequiresModels`
  (default `true`).
- `Services/Pipelines/PipelineManifestProvider.cs` — add id to `ManifestIds`.
- `ViewModels/Pipelines/PipelineRunViewModel.cs` — re-parent to `PipelineRunViewModelBase`.
- **New** `ViewModels/Pipelines/PipelineRunViewModelBase.cs`.
- `ViewModels/PipelinesViewModel.cs` — retype `ActiveRun`/factory/`OpenRun`; `RequiresModels` bypass in
  open + readiness; expose grouped collections (`GenerationPipelines`/`UtilityPipelines`,
  `HasUtilityPipelines`).
- `ViewModels/PipelineTileViewModel.cs` — surface `Category`.
- `Views/PipelinesView.axaml` — grouped sections + labelled divider; `ContentControl` view resolution.
- `App.axaml.cs` — factory switch case; DataTemplate registration if not via ViewLocator.
- `Services/PngMetadataWriter.cs` — `stripExisting` flag, `zTXt` skipping, route formatting through
  `A1111MetadataFormatter`.
- `Services/PngChunkReader.cs` — tolerate `zTXt` (compressed) chunks on read (spec §1).
- `Services/ImageMetadataParser.cs` — `ParseComfyUiGraph` delegates to `ComfyUiPromptTracer`.
- `Models/LoraInfo.cs` — optional additive `File`/source field.

**Testability note:** `ImageMetadataParser`/`PngChunkReader`/`PngMetadataWriter` are `internal` to
`DiffusionNexus.UI`. Put the new services in the same assembly and rely on the existing
`InternalsVisibleTo(DiffusionNexus.Tests)` (confirm it's present; add if missing), or make the four
pure services `public`. Prefer `internal` + `InternalsVisibleTo` to match the current pattern.

---

## 10. Risks / open implementation questions (defer to the plan)

- **Grouped gallery rendering** in Avalonia — two `ItemsControl`s vs. a grouped `CollectionView`. Two
  ItemsControls is simpler and matches the small number of categories; go with that unless grouping is
  trivially available.
- **Output collision policy** (§7) — de-duplicate vs. require empty folder. Default: de-duplicate.
- **Non-PNG output** (§7) — re-encode to PNG vs. flag PNG-only. Default: PNG output; note the input's
  original format in the item.
- Whether to migrate the existing `FormatAsA1111Parameters` caption-baking caller onto
  `A1111MetadataFormatter` now or leave it untouched.

---

## 11. Future extensions
- JPEG/WebP output with A1111 written into EXIF `UserComment` (ImageSharp is already referenced).
- A JSON sidecar option (the structured `TraceResult`), if a user later wants lossless export.
- Rule-set persistence (save/load named sets across sessions) and import/export.
- Recognizing additional LoRA-stack node packs beyond rgthree.
