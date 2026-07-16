using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;

namespace DiffusionNexus.Inference.StableDiffusionCpp;

/// <summary>
/// Walks a ComfyUI-layout models root and reports every <see cref="ModelDescriptor"/>
/// the v1 backend can serve. Discovery is purely file-existence-based — no GGUF
/// header inspection, no metadata parsing — to keep startup fast and predictable.
/// </summary>
/// <remarks>
/// Expected ComfyUI subfolders:
/// <list type="bullet">
///   <item><description><c>DiffusionModels/</c> — UNET-only / DiT-only files (Z-Image-Turbo, Flux UNETs, Qwen-Image, …).</description></item>
///   <item><description><c>TextEncoders/</c> — CLIP / T5 / LLM text encoder files.</description></item>
///   <item><description><c>VAE/</c> — autoencoder weights.</description></item>
///   <item><description><c>StableDiffusion/</c> — single-file SDXL/SD1.5 checkpoints (future).</description></item>
/// </list>
/// </remarks>
public sealed class ComfyUiModelCatalog : IDiffusionBackendCatalog
{
    private readonly IReadOnlyList<string> _modelsRoots;
    private List<ModelDescriptor>? _cached;
    private int _searchedLocationCount;

    /// <summary>Convenience constructor for a single root.</summary>
    public ComfyUiModelCatalog(string modelsRoot) : this(new[] { modelsRoot })
    {
    }

    /// <summary>
    /// Construct a catalog that searches every supplied models root recursively. Roots earlier in
    /// the list win when the same filename exists in multiple installations (matches the user's
    /// preference order — typically default ComfyUI first).
    /// </summary>
    public ComfyUiModelCatalog(IEnumerable<string> modelsRoots)
    {
        ArgumentNullException.ThrowIfNull(modelsRoots);
        _modelsRoots = modelsRoots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (_modelsRoots.Count == 0)
            throw new ArgumentException("At least one models root is required.", nameof(modelsRoots));
    }

    public IReadOnlyList<ModelDescriptor> ListAvailable() => _cached ??= Discover();

    public ModelDescriptor? TryGet(string key) =>
        ListAvailable().FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.Ordinal));

    /// <summary>Forces a fresh disk scan on the next call. Use after the user changes their models folder.</summary>
    public void Invalidate()
    {
        _cached = null;
        _searchedLocationCount = 0;
    }

    /// <summary>The number of folders scanned during the last discovery pass (diagnostic).</summary>
    public int SearchedLocationCount => _searchedLocationCount;

    /// <summary>Read-only view of the configured model roots (diagnostic).</summary>
    public IReadOnlyList<string> ModelsRoots => _modelsRoots;

    private List<ModelDescriptor> Discover()
    {
        var found = new List<ModelDescriptor>();

        TryAddZImageTurbo(found);
        TryAddFlux2Klein(found);
        TryAddQwenImage2512(found);
        TryAddQwenImageEdit2511(found);

        // TODO(v2-models): add SDXL checkpoint discovery, etc.

        return found;
    }

    /// <summary>
    /// Finds a Qwen-Image diffusion GGUF, picking only a quant that renders correctly in
    /// stable-diffusion.cpp. Per leejet/stable-diffusion.cpp#1385, several k-quants (Q2_K, Q4_K_M,
    /// Q5_K_M, Q5_K_S, …) produce fully BLACK images for Qwen-Image (activation-dequant overflow), while
    /// Q8_0 / Q5_1 / Q5_0 / Q4_1 / Q4_K_S render normally. We try the known-good quants in order and
    /// never select a black-listed one — so a stray k-quant on disk can't silently ruin every render.
    /// Smaller safe quants are preferred over Q8_0 since Q8_0 won't fit a typical 24–32 GB card here.
    /// </summary>
    private string? FindBlackSafeQwenDiffusionGguf(string baseName)
    {
        string[] safeQuants = ["Q5_1", "Q5_0", "Q4_1", "Q4_K_S", "Q8_0", "BF16", "F16"];
        foreach (var quant in safeQuants)
        {
            var hit = FindFileByPattern($"{baseName}-{quant}.gguf");
            if (hit is not null) return hit;
        }
        return null;
    }

    private void TryAddQwenImageEdit2511(List<ModelDescriptor> sink)
    {
        // Image-editing sibling of Qwen-Image-2512: the input image is fed as a reference (VAE-encoded
        // conditioning) and edited per the prompt. Only a black-safe quant is selected (see #1385).
        var unet = FindBlackSafeQwenDiffusionGguf("qwen-image-edit-2511");
        // Text encoder = Qwen2.5-VL-7B, GGUF only (the fp8-scaled safetensors render black in sd.cpp —
        // same trap as Qwen-Image-2512's encoder).
        var llm = FindFileByPattern("Qwen2.5-VL-7B*.gguf");
        var vae = FindFile("qwen_image_vae.safetensors");

        if (unet is null || llm is null || vae is null)
            return;

        var encoders = new Dictionary<TextEncoderSlot, string>
        {
            [TextEncoderSlot.Llm] = llm,
        };

        // The edit model needs the Qwen2.5-VL vision projector (mmproj) to "see" the input image.
        // Optional at discovery so the model still appears if it's absent (a GPU run will tell us
        // whether it's strictly required); when present it's wired into the LlmVision slot. Prefer a
        // Qwen2.5-VL-named mmproj, else the unsloth repo's generic mmproj-F16/BF16.gguf.
        var mmproj = FindFileByPattern("mmproj*Qwen2.5*.gguf")
            ?? FindFileByPattern("mmproj*qwen2.5*.gguf")
            ?? FindFileByPattern("mmproj-F16.gguf")
            ?? FindFileByPattern("mmproj-BF16.gguf");
        if (mmproj is not null)
            encoders[TextEncoderSlot.LlmVision] = mmproj;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.QwenImageEdit2511,
            DisplayName = "Qwen-Image-Edit-2511",
            Kind = ModelKind.QwenImageEdit2511,
            DiffusionModelPath = unet,
            VaePath = vae,
            TextEncoders = encoders,
            // Run with the 4-step Edit Lightning LoRA (applied by the run screen as a mandatory row):
            // 4 steps, CFG 1, euler/simple, flow shift 3.0. The reference (input) image is passed
            // per-generation via DiffusionRequest.ReferenceImages.
            DefaultSteps = 4,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DefaultFlowShift = 3.0f,
            TileVae = true,
            // NOTE: WithClipNetOnCpu(true) hard-crashes sd.cpp 6.0.0's native conditioner for Qwen
            // (process dies right after "CLIP: Using CPU backend"), so the text encoder stays on GPU.
            OffloadTextEncoderToCpu = false,
            DimensionAlignment = 16,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        });
    }

    private void TryAddQwenImage2512(List<ModelDescriptor> sink)
    {
        // The diffusion model ships as one GGUF per VRAM/quant tier; only a black-safe quant is
        // selected (k-quants like Q2_K/Q4_K_M render black in sd.cpp — see #1385).
        var unet = FindBlackSafeQwenDiffusionGguf("qwen-image-2512");
        // Text encoder = Qwen2.5-VL-7B and it MUST be a GGUF. The Comfy-Org fp8-scaled safetensors
        // (qwen_2.5_vl_7b_fp8_scaled.safetensors) loses its per-tensor dequant scales in
        // stable-diffusion.cpp and renders a fully BLACK image, so we deliberately do NOT accept it
        // here (same fp8 trap as FLUX.2-klein's Qwen3-8B encoder). Match any Qwen2.5-VL-7B GGUF quant.
        var llm = FindFileByPattern("Qwen2.5-VL-7B*.gguf");
        var vae = FindFile("qwen_image_vae.safetensors");
        // The model is wired to run in just 4 steps, which REQUIRES the Lightning 4-step LoRA. Treat
        // the LoRA as a mandatory part of the model: if it isn't on disk, don't offer the model at all
        // (matches "4-step Lightning" being intrinsic, not optional). "4steps" in the pattern avoids
        // grabbing the 8-step Lightning variant.
        var lightningLora = FindFileByPattern("Qwen-Image-Lightning-4steps-*.safetensors");

        if (unet is null || llm is null || vae is null || lightningLora is null)
            return;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.QwenImage2512,
            DisplayName = "Qwen-Image-2512",
            Kind = ModelKind.QwenImage2512,
            DiffusionModelPath = unet,
            VaePath = vae,
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = llm,
            },
            // Qwen-Image is a flow model run with the 4-step Lightning LoRA: 4 steps, CFG 1,
            // euler/simple, flow shift 3.1. The Lightning LoRA is baked in via DefaultLoras so it is
            // applied on every generation (incl. the Diffusion Canvas, which passes no LoRAs).
            DefaultLoras = [new LoraReference(lightningLora, 1.0f)],
            DefaultSteps = 4,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DefaultFlowShift = 3.1f,
            TileVae = true,
            // WithClipNetOnCpu crashes sd.cpp 6.0.0 for Qwen (see Qwen-Image-Edit note); keep on GPU.
            OffloadTextEncoderToCpu = false,
            DimensionAlignment = 16,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        });
    }

    private void TryAddFlux2Klein(List<ModelDescriptor> sink)
    {
        // The diffusion model ships as one GGUF per VRAM/quant tier (e.g.
        // flux-2-klein-9b-Q4_K_M.gguf … flux-2-klein-9b-BF16.gguf) — any one present satisfies it.
        var unet = FindFileByPattern("flux-2-klein-9b-*.gguf");
        // Text encoder = Qwen3-8B. stable-diffusion.cpp loads it via WithLLMPath and needs a GGUF or
        // plain bf16 — NOT Comfy-Org's fp8-scaled "fp8mixed" safetensors (those fail to load with a
        // tensor-shape error). Match any Qwen3-8B GGUF quant (unsloth/Qwen3-8B-GGUF naming).
        var llm = FindFileByPattern("Qwen3-8B-*.gguf");
        var vae = FindFile("flux2-vae.safetensors");

        if (unet is null || llm is null || vae is null)
            return;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.Flux2Klein,
            DisplayName = "FLUX.2-klein",
            Kind = ModelKind.Flux2Klein,
            DiffusionModelPath = unet,
            VaePath = vae,
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = llm,
            },
            // FLUX.2-klein is a distilled flow model; CFG=1 (guidance is baked in). Tune as needed.
            DefaultSteps = 20,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DimensionAlignment = 16,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        });
    }

    private void TryAddZImageTurbo(List<ModelDescriptor> sink)
    {
        // Z-Image-Turbo requires three files. Find each by name across every configured root.
        var unet = FindFile("z_image_turbo_bf16.safetensors");
        var clip = FindFile("qwen_3_4b.safetensors");
        var vae = FindFile("ae.safetensors");

        if (unet is null || clip is null || vae is null)
            return;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.ZImageTurbo,
            DisplayName = "Z-Image-Turbo",
            Kind = ModelKind.ZImageTurbo,
            DiffusionModelPath = unet,
            VaePath = vae,
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = clip,
            },
            DefaultSteps = 9,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DimensionAlignment = 64,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        });
    }

    /// <summary>
    /// Walks every configured models root recursively looking for a file with the given name.
    /// Returns the first hit (roots are searched in user-supplied order). Returns <c>null</c>
    /// if not found anywhere. Increments <see cref="SearchedLocationCount"/> for every
    /// directory visited so the UI can surface "searched N locations" feedback.
    /// </summary>
    private string? FindFile(string fileName)
    {
        foreach (var root in _modelsRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var hit = SearchRecursive(root, fileName);
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="FindFile"/> but matches a wildcard search pattern (e.g.
    /// <c>flux-2-klein-9b-*.gguf</c>). Returns the first match across all roots, or null.
    /// </summary>
    private string? FindFileByPattern(string searchPattern)
    {
        foreach (var root in _modelsRoots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            var hit = SearchRecursivePattern(root, searchPattern);
            if (hit is not null) return hit;
        }
        return null;
    }

    private string? SearchRecursivePattern(string directory, string searchPattern)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            _searchedLocationCount++;

            try
            {
                var match = Directory.EnumerateFiles(current, searchPattern).FirstOrDefault();
                if (match is not null) return match;

                foreach (var subDir in Directory.EnumerateDirectories(current))
                    stack.Push(subDir);
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible folders */ }
            catch (DirectoryNotFoundException) { /* race: folder removed mid-scan */ }
            catch (IOException) { /* skip transient I/O errors */ }
        }

        return null;
    }

    private string? SearchRecursive(string directory, string fileName)
    {
        var stack = new Stack<string>();
        stack.Push(directory);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            _searchedLocationCount++;

            try
            {
                var direct = Path.Combine(current, fileName);
                if (File.Exists(direct)) return direct;

                foreach (var subDir in Directory.EnumerateDirectories(current))
                    stack.Push(subDir);
            }
            catch (UnauthorizedAccessException) { /* skip inaccessible folders */ }
            catch (DirectoryNotFoundException) { /* race: folder removed mid-scan */ }
            catch (IOException) { /* skip transient I/O errors */ }
        }

        return null;
    }
}

/// <summary>Stable string identifiers for the models the v1 backend understands.</summary>
public static class ModelKeys
{
    public const string ZImageTurbo = "z-image-turbo";

    public const string Flux2Klein = "flux2-klein";

    public const string QwenImage2512 = "qwen-image-2512";

    public const string QwenImageEdit2511 = "qwen-image-edit-2511";

    // TODO(v2-models): add SDXL keys here.
}

// Bridge interface so DiffusionContextHost can stay independent of the public IModelCatalog
// while we expose only one concrete catalog today.
internal interface IDiffusionBackendCatalog : Abstractions.IModelCatalog
{
    void Invalidate();
}
