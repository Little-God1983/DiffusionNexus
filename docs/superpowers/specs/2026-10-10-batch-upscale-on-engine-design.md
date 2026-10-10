# Batch Upscale on the Diffusion Nexus Engine (#608)

Milestone 2, "Editor on the Diffusion Nexus Engine": the app's ComfyUI features run on the app-owned
embedded ComfyUI with their existing workflows, so no user-run ComfyUI is needed. Builds on #606
(Engine client, Features dialog) and #607 (the Qwen3-VL GGUF describer, the llama-cpp-python wheel,
readiness model paths). Owner ruling: ComfyUI only, existing custom nodes over our own inference code.

## What changes for the user

- **Installation Manager → Engine → Features** has two new rows: **Batch Upscale** (Z-Image Turbo +
  Ultimate SD Upscale) and **Batch Upscale Vision** (the same plus Qwen3-VL, which writes each image's
  prompt). Files already in a model folder are found and not downloaded again; the Vision row shares
  the Qwen3-VL GGUF with Outpaint Vision.
- **Batch Upscale tab** runs on the server chosen in Settings (the Engine by default, started on
  demand; or the user's own ComfyUI URL). There is no per-tab server picker. The readiness panel
  shows where it runs and, when something is missing, an install link that opens Features with the
  matching row ticked.
- **Vision Auto-Prompt runs in two steps**, shown in a banner above the progress bar:
  - *Step 1 of 2 · Describing images with Qwen3-VL · n/N*, with the line "Upscaled images appear in
    step 2. Qwen3-VL describes every image first so it loads only once; then it is unloaded and the
    upscaler gets the VRAM." Described thumbnails get their own border colour; hovering one shows
    its description.
  - *Step 2 of 2 · Upscaling · n/N*: the existing run, each image with its own description as the
    prompt.
  The progress bar runs 0–100 within each step.
- The other prompt modes (Manual, From Captions, From Metadata) run one step, as today.
- The Unified Console shows the server, each step change, every description, and failures naming
  the ComfyUI node.

## Why two steps

The describer is KLL535's `SimpleQwenVLggufV2` (llama.cpp, the same node and model as Outpaint
Vision). Run per image inside the upscale job, it must free its 6–9 GB after every description so
Z-Image Turbo (~20 GB with its text encoder) has the VRAM back, and reloading the GGUF costs about
10 s per image: roughly 15 extra minutes for 100 images. Keeping both loaded does not fit on a
16–24 GB card. Describing every image first with the model kept loaded (`keep_vram`), freeing it
after the last (`direct_clean`), and then upscaling takes a few seconds per description.

## Pieces

1. **Catalog** (`Into-The-Latent/DiffusionNexus.Catalog`, commits on `main`):
   - *Upscaling-Z-Image-Turbo* (`B853EB7C…`) keeps its four models (z_image_turbo_bf16, qwen_3_4b,
     ae, 4x-UltraSharp) and lists only the Ultimate SD Upscale node pack (Qwen3-VL-Instruct and
     rgthree go).
   - New *Upscaling-Z-Image-Turbo Vision*: the same models and pack plus ComfyUI_Simple_Qwen3-VL-gguf,
     ComfyUI-Custom-Scripts (ShowText puts the description in the job's outputs), the models
     *Qwen3-VL-8B-Abliterated-Caption-it* and *… mmproj* with the same VRAM-tiered links as the
     Outpainting workload, and `installLamaCpp` on.
   - Preview rebuilds from `main`; Stable needs a v6 release (owner, via the Toolbox).
2. **App wiring**: `EngineFeatureCatalog` gains `BatchUpscale` and `BatchUpscaleVision` rows (one
   workload each) and maps `Feature.BatchUpscale` / `Feature.BatchUpscaleVision` to them;
   `FeatureRegistry` points `BatchUpscaleVision` at the new workload. Readiness `ModelPaths` carry
   the GGUF and projector paths, as for Outpaint Vision.
3. **Workflows** (`DiffusionNexus.Service/Assets/Workflows/`):
   - `Z-Image-Turbo-Upscale.json`: the `Power Lora Loader (rgthree)` node (53) is removed; the
     prompt encoders take the clip from `CLIPLoader` (27) and Ultimate SD Upscale takes the model
     from `UNETLoader` (28).
   - New `Qwen3-VL-Describe.json`: `LoadImage` → `ImageScaleToMaxDimension` (1280 px) →
     `SimpleQwenVLggufV2` → `ShowText|pysssss`. The app sets the describer's `mode` (`keep_vram`
     for every image but the last, `direct_clean` for the last), its 32-bit seed and its
     `config_override` (model and projector paths, `chat_handler: qwen3`, 400 output tokens,
     temperature 0.3). User prompt: "Describe the image in about 100 detailed words."
   - `Vision-Z-Image-Turbo-Upscale.json` is deleted.
   - The Qwen3-VL config Outpaint builds (`BuildVisionConfig`, the model names) moves to one shared
     helper used by both.
4. **Batch Upscale tab** (`BatchUpscaleTabViewModel`):
   - Uses `IComfyUiClientProvider` (a lease per run, the Engine started on demand) instead of the
     fixed `IComfyUIWrapperService`.
   - Vision runs step 1 then step 2 as above. Step 2 queues the normal upscale workflow with the
     image's description as the positive prompt.
   - A description that fails for one image is logged as a warning and that image is upscaled with
     an empty prompt (denoise 0.25 works without one).
   - Cancel or a failure during step 1 queues one describe job in `direct_clean` mode, which reuses
     the loaded model and frees it, so Qwen3-VL does not stay in the Engine's VRAM. Nothing resumes.
   - Failures name the server or the node, as in the editor (`ComfyUiUnavailableException`,
     `ComfyUIExecutionException`, `ComfyUIWorkflowRejectedException`, HTTP status).
5. **View** (`BatchUpscaleTabView.axaml`): the step banner and explanation line, the "described"
   thumbnail border and description tooltip, the readiness panel's install link.

## Testing

- Unit tests: the two steps in order; `keep_vram` for every image but the last; the step texts;
  freeing Qwen3-VL on cancel and on failure; each image's description reaching step 2 as its
  prompt; a failed description upscales with an empty prompt; no rgthree node in the upscale
  workflow; the Features rows and the readiness mapping.
- BenQ smoke on the Engine: three images with a manual prompt, then three with Vision Auto-Prompt.

## Out of scope

- App-side tiling or replacing Ultimate SD Upscale; the orphaned ONNX 4x-UltraSharp upscaler.
- Editing or reviewing descriptions between the two steps; resuming a cancelled run.
- Captioning on the Engine (a later issue may reuse the describe workflow).
