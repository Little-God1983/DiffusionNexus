# Outpaint Vision on the Diffusion Nexus Engine (#607)

Milestone 2, "Editor on the Diffusion Nexus Engine": the editor's ComfyUI features run on the app-owned
embedded ComfyUI with their existing workflows, so no user-run ComfyUI is needed. Builds on #606.

## What changes for the user

- **Installation Manager → Engine → Features** has a new row, **Outpaint Vision**. It installs the
  catalog workload *Outpainting-Qwen 2512*: the Qwen-Image 2512 set it shares with *Inpaint & Outpaint*
  (nothing is downloaded twice), the node packs *ComfyUI_Simple_Qwen3-VL-gguf* and
  *ComfyUI-Custom-Scripts*, and the model *Qwen3-VL-8B-Abliterated-Caption-it* (GGUF plus projector,
  picked by VRAM tier, 5–9 GB).
- **Image Editor → Outpaint → Generate (Vision)** works with the Engine selected as the server. Until the
  row is installed the button is greyed out, the first missing item is shown under it, and an
  *Install Outpaint Vision* link opens Features with the row ticked.
- The Unified Console shows the description Qwen3-VL wrote (`Vision description: …`), and the status
  line shows *Describing the surroundings (Qwen3-VL)* while it runs.
- Custom URL mode gets the same workflow, so a user's own ComfyUI needs the same node packs and
  model. The *Outpainting-Qwen 2512* workload in Installer Manager installs them there.

## Why the workflow changed

The Vision workflow used the `Qwen3_VQA` node (ComfyUI_Qwen3-VL-Instruct), which runs Qwen3-VL through
transformers in Python. Measured on an RTX 5090: 4 tokens per second with the FP8 weights (12 with
BF16), because the GPU waits on the Python loop for about 90 % of the time. One description took
minutes, and a looping description ran to the node's 2048-token limit.

The same model as a GGUF through llama.cpp (what the app's own captioner uses) runs at 75 tokens per
second on the same card. The workflow therefore swaps `Qwen3_VQA` for KLL535's `SimpleQwenVLggufV2`
node, an existing custom node, pointed at the abliterated Qwen3-VL 8B GGUF. The description is capped
at 400 tokens. ShowText stays so the description lands in the job's outputs.

## Pieces

1. **Workflow** `Qwen-Image-2512-outpaint-Vision.json`: node 256 is `SimpleQwenVLggufV2` in
   `direct_clean` mode (runs inside the ComfyUI process, where torch has put the CUDA runtime on the DLL
   search path; the node's default subprocess mode falls back to the CPU), ShowText (257) feeds
   CLIPTextEncode (5). The app sets the node's `config_override` at queue time: the absolute paths of
   the GGUF and projector, `chat_handler: qwen3`, 400 output tokens, temperature 0.3.
2. **Model paths**: the node takes files as paths, and the files may live in any model folder
   (`D:\Models\Captioning` on the owner's machine). `WorkloadCheckSummary` and `FeatureReadinessResult`
   gain `ModelPaths` (catalog model name → path found), filled by both the ComfyUI and the Engine
   backends from the checker's `FoundAtPath`; `FeatureReadinessViewModel` exposes it and
   `OutpaintingViewModel` reads the two Vision entries. No paths → Generate (Vision) stops with the
   install hint instead of queuing.
3. **llama-cpp-python wheel**: the node pack's requirements list `llama-cpp-python`; without a
   prebuilt wheel pip would compile it (needs a C++ toolchain, usually fails on Windows). The catalog
   gets a CUDA 13 wheel (JamePeng 0.4.2+cu130, Python 3.12) next to the existing cu128 one.
   `IWorkloadInstallService.EnsureLlamaCppWheelAsync` runs before the node packs on both install
   paths (the Engine's Features dialog and Installer Manager for a user's own ComfyUI, whenever the
   workload has `installLamaCpp`): one Python probe reads the target venv's Python, torch's CUDA and
   the installed llama-cpp-python (version and the hash of the file pip installed), the matching
   catalog wheel is picked, and pip installs it with the catalog's `#sha256=` so any other file is
   rejected. The catalog's own file in place → nothing to do; the same version from another build
   (PyPI's CPU 0.3.20) → force-reinstalled. A wheel for another CUDA loads but runs on the CPU, so no
   fallback: without a wheel for this venv the node packs install anyway with a warning, while a
   matching wheel that fails to install keeps the packs out (a pack on disk reads as installed, so
   Install would never retry) and the row stays Partial.
4. **Catalog** (`Into-The-Latent/DiffusionNexus.Catalog`, on `main`): the *Outpainting-Qwen 2512*
   workload lists the GGUF node pack and Custom-Scripts instead of Qwen3-VL-Instruct and KJNodes,
   the "Qwen 3 VL" placeholder becomes two real models with VRAM-tiered links (the same tiers the
   app's captioning manager uses), and `installLamaCpp` is on with the cu128 wheel for the
   workload's own torch.
5. **Also on this branch**: the Engine starts with `HF_HUB_USER_AGENT_ORIGIN` set (a `kernels` /
   `huggingface_hub` header bug made Hugging Face requests fail under ComfyUI's telemetry switch), and
   a node failing inside ComfyUI is reported as *Failed in the ComfyUI node X* instead of blaming the
   Engine.

## Out of scope

- Captioning on the Engine and Batch Upscale (Vision) → #608, which reuses the same node pack.
- A Vision path that does not need a GGUF node (the local captioner) was built and discarded: it would
  keep the editor on the local backend the milestone retires.
