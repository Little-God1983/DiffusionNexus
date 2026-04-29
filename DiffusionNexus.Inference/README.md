# DiffusionNexus.Inference

A backend-agnostic seam for running local diffusion inference inside DiffusionNexus.
Currently shipping a single implementation against `StableDiffusion.NET` (which wraps
[`stable-diffusion.cpp`](https://github.com/leejet/stable-diffusion.cpp)).

## What this project is

- A small **class library** with **no UI dependencies**, so it can be reused in other
  .NET projects later.
- Defines the `IDiffusionBackend` contract that the UI consumes. The same contract will
  be satisfied by a future ComfyUI adapter so users can pick their engine.

## What this project is NOT (today)

This is the v1 cut. Many fields and concepts exist as **placeholders** so the public API
shape never has to change later. They are tracked with `TODO(v2-<tag>)` markers in source.

| Tag | What it unlocks |
|---|---|
| `v2-models` | Add SDXL / Flux / Qwen-Image-Edit support in `ModelKind` + corresponding loaders. |
| `v2-negative-prompt` | Honor `DiffusionRequest.NegativePrompt` in the backend. |
| `v2-loras` | Honor `DiffusionRequest.Loras` (LoRA stacking). |
| `v2-controlnet` | Honor `DiffusionRequest.ControlNets` (ControlNet conditioning). |
| `v2-img2img` | Honor `DiffusionRequest.InitImage` (img2img / canvas init image). |
| `v2-inpaint` | Honor `DiffusionRequest.MaskImage` (inpaint with mask). |
| `v2-cancel` | Plumb `CancellationToken` into the native sampler when upstream exposes a cancel hook. |
| `v2-live-preview` | Populate `DiffusionProgress.PreviewPngBytes` when upstream exposes intermediate latents. |

To find every gap: `grep -r "TODO(v2-" DiffusionNexus.Inference/`.

## Native runtime

The managed `StableDiffusion.NET` package alone is **not enough to run anything** — it
needs a backend native package (`StableDiffusion.NET.Backend.Cuda12.Windows`,
`...Vulkan`, `...Cpu`, …). Those are intentionally added on the **executable project**
(`DiffusionNexus.UI`), not here, so other projects can reference this library without
inheriting a 600 MB CUDA dependency.
