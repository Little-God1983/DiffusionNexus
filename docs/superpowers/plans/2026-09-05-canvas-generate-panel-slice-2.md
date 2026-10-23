# Diffusion Canvas — Generation Suite, Slice 2 (generate panel + capability gating)

Issue #518, regions **B** (generate panel) and the capability half of **A**, plus the
*Capability gating* section. The issue's own suggested order groups these: "Generate panel
bindings + capability gating (B, A) — mostly wiring, safe to run in parallel".

Slice 1 (regions C, E, cancel half of F) shipped in PR #558, merge `30aa9d59`.

## Why the two are one slice

Region B cannot ship truthfully on its own. The two backends disagree on nearly every parameter
the panel exposes:

| Parameter | Local (stable-diffusion.cpp) | Engine (Krea 2 / ComfyUI) |
|---|---|---|
| Steps, Guidance | honoured | honoured |
| Sampler, Scheduler | honoured | **ignored** — baked into node 37 of the template |
| Negative prompt | **ignored** — `WithNegativePrompt` exists in the shipped binding, just unwired | honoured (node 35) |
| LoRAs | honoured, absolute paths | **ignored** — node 55 is an rgthree loader the patcher never touches |
| Seed | honoured, 63-bit | honoured, 31-bit |

A sampler dropdown wired naively is a per-backend lie: the user picks `dpmpp2m`, the Engine
silently runs `euler`, and the result looks like the model ignoring them. So the panel and the
capability surface ship together, and every control the selected backend cannot honour is
**disabled carrying a one-line reason**, per the issue: hiding teaches nothing, and greying
without a reason reads as a bug.

## Definition of done

- [ ] Every placeholder property on the view model has a live, enabled control bound to it, and its
      value reaches `DiffusionRequest`.
- [ ] Steps, guidance, sampler, negative prompt, seed and LoRAs are each either live or disabled
      with a one-line reason naming the backend that would honour them.
- [ ] LoRA rows filter to the selected model's base model, with per-row strength.
- [ ] Sampling is collapsed by default and its header summarises itself (`euler · 9 · cfg 1.0`).
- [ ] Seed can be locked and randomised; batch count sits on the Generate button.
- [ ] `IDiffusionBackend` declares what it supports; both backends answer honestly.
- [ ] The panel scrolls and Generate never scrolls out of reach.
- [ ] Every step of the flow is traced to the Unified Console (standing repo rule).

## Architecture

### Capabilities are per-feature with a reason, not a bool

A bare `bool SupportsSampler` cannot satisfy the issue's rule, which demands the limit be *stated
at the control*. The reason has to come from the backend, because the backend is what knows why.

`BackendFeature` (enum) + `BackendCapabilities` (supported set plus a limitation string per
unsupported feature). The view model projects one `Is…Supported` / `…Limitation` pair per feature
so compiled bindings resolve against real instance members. Limitation text names the other
backend, because the design says to offer the switch that removes the limit.

### The panel is a new left column, and three things move into it

The view root is today a five-row, **zero-column** grid. Adding `ColumnDefinitions` without adding
`Grid.Column="1"` to all five existing children renders the panel on top of the canvas with no
error, so all five move together.

Moves, following the mockup's order (model → prompt → negative → LoRAs → sampling):

- **Model combo** leaves the tool strip for the top of the panel.
- **Prompt and negative prompt** leave the bottom bar for the panel.
- **Status text** leaves the title bar for the panel footer, directly above Generate, where the
  user is already looking during a run. That frees the title bar for the backend selector beside
  the VRAM monitor, which is region A's stated rationale: picking a model is a VRAM decision.

The bottom bar disappears. Denoise stays with the region readout, which is about the box rather
than the model, and moves into the panel footer above Generate.

### LoRAs: local backend only this slice

`ILoraCatalog` + `MultiLoraPickerControl` + `LoraPickerItemViewModel` are reusable as they stand,
`DiffusionRequest.Loras` exists, and the local backend already applies it — the canvas simply
never filled the field. That half is wiring with zero backend change.

The Engine half is **scoped out** and gated with a reason. It needs three things that do not
exist: a node-55 patch (small), an absolute-path → ComfyUI-relative-name resolver over the
engine's `extra_model_paths` roots (not small; LoRA sources and Base Model Folders are separate
registries with no guarantee of overlap), and a Civitai base-model label for Krea 2 (none exists).

### The base-model label map is authored, not derived

`ModelDescriptor.DisplayName` is not the Civitai label — the descriptor says `Z-Image-Turbo`,
Civitai says `ZImageTurbo`. No string transform bridges them. The only precedent is a four-string
array duplicated across two pipeline view models covering FLUX.2-klein alone.

New `ModelBaseModelLabels`: model key → the raw Civitai labels that are compatible, hand-authored,
with an explicit "no labels known" entry for `krea2`. The two pipeline view models collapse onto
it, removing the duplication.

The fallback for a model with no labels is **an empty list plus an explanation**, never the
unfiltered library: `LoraCatalog` treats a null filter as "return everything", which on a large
library means thousands of rows each decoding a thumbnail from a database BLOB.

### Two live defects block honesty and are fixed here

- `ManagedComfyUiBackend` never reads `request.ModelKey`; it runs the Krea 2 graph for whatever is
  selected. The panel makes the model dropdown more prominent, so it is fixed first, as
  error-as-data through the existing seam.
- The local backend drops `NegativePrompt` even though the shipped binding exposes
  `WithNegativePrompt`. Without this the negative box is dead on the default backend, and gating it
  as "unsupported" would be recording a TODO as a capability.

### Deliberately not in this slice

- **Persistence.** The canvas persists nothing today, and a new `AppSettings` column costs a
  migration plus a recovery-service entry. Region B does not ask for it. Follow-up.
- **A width/height control.** The bounding box owns dimensions; a second editor for the same number
  invites the two to disagree. The box's readout already shows them.
- **Region D, F's queue, G.** Untouched.

## Global constraints

- Feature branch `feature/canvas-generate-panel`, never commit to `develop`.
- Un-pathed Glob/Grep hits the SDK repo. Always path under `e:\Repos\DiffusionNexus`.
- Check `REUSABLES.md` before any new control; add a row for anything reusable, same commit.
- New shortcuts go in `DiffusionNexus.UI/Doc/Shortcuts.md`.
- Compiled bindings are on and the view declares `x:DataType`; every binding path must resolve to a
  real instance member. Default-interface members and statics do not bind.
- The parameterless design-time constructor **must stay parameterless** — the test project builds
  against it. `ILoraCatalog` enters as an optional trailing parameter on the production ctor only.
- No Avalonia platform in `DiffusionNexus.Tests`: no real bitmaps, and panel layout is untestable
  there. Behaviour goes through the view model.
- Restore CRLF on any file whose `develop` blob had it (see the line-endings lesson from #558).

## Tasks

- [ ] **1 — `BackendFeature` + `BackendCapabilities`** on `IDiffusionBackend`; both backends declare
      theirs honestly, including the local backend's inability to interrupt mid-sample. Tests.
- [ ] **2 — Local negative prompt**: wire `WithNegativePrompt`, removing the TODO. Test.
- [ ] **3 — Engine `ModelKey` guard**: refuse a foreign model as error-as-data rather than silently
      running the Krea 2 graph under another model's name. Test.
- [ ] **4 — `ModelBaseModelLabels`**: authored key → Civitai label map, `krea2` explicitly empty;
      both pipeline view models collapse onto it. Tests.
- [ ] **5 — View-model panel state**: retype `Loras`, add `AvailableLoras`, inject `ILoraCatalog`
      optionally, reload on model change, seed lock/randomise, sampling summary, prompt character
      count, per-feature capability projections. Tests.
- [ ] **6 — Request wiring**: Steps/Cfg/Sampler/NegativePrompt/Loras onto `DiffusionRequest`,
      snapshotted once per batch like the prompt already is. Tests.
- [ ] **7 — The panel**: new left column; move model, prompt, negative, status; build LoRA rows,
      collapsed sampling with a summary header, seed row, pinned Generate carrying batch count.
- [ ] **8 — Title bar**: backend selector beside the resource monitor.
- [ ] **9 — Keyboard**: the tunnel guard exempts only a focused `TextBox`, so a focused slider or
      combo in the panel loses its arrow keys to the staging strip. Broaden it.
- [ ] **10 — Grid follows the box**: the dot lattice is hard-coded to 64 while the box snaps to the
      model's alignment, so after one Generate on a 16-aligned model they disagree.
- [ ] **11 — Docs**: REUSABLES row for anything reusable; Shortcuts.md for any new key.
- [ ] **12 — Verify**: full build, full test run, adversarial review, then a manual GUI smoke.
