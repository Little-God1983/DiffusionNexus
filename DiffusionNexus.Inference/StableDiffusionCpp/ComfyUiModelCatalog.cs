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
        TryAddQwenImageInpaint(found);

        // TODO(v2-models): add SDXL checkpoint discovery, Qwen-Image-Edit (incl. mmproj), etc.

        return found;
    }

    private void TryAddQwenImageInpaint(List<ModelDescriptor> sink)
    {
        // Mirrors the ComfyUI inpaint workflow (Inpaint-Qwen-2512.json): Qwen-Image-2512 GGUF DiT +
        // Qwen2.5-VL text encoder + Qwen-Image VAE + the InstantX inpainting ControlNet. The 4-step
        // Lightning LoRA is applied per-generation (via the request's Loras), not loaded here.
        var unet = FindFileByPattern("qwen-image-2512-*.gguf");
        // Text encoder = Qwen2.5-VL-7B, loaded via WithLLMPath. Prefer a GGUF quant when present:
        // ComfyUI loads the fp8-scaled safetensors, but — as with the Qwen3-8B encoder for FLUX.2
        // (see TryAddFlux2Klein) — Comfy-Org's fp8-scaled safetensors can fail to load in
        // stable-diffusion.cpp with a tensor-shape error. The fp8 file is the fallback (it is what
        // the inpaint workload downloads), so it still works if it loads; discovery is purely
        // existence-based (no header probing), so this is a best-effort preference, not a load-time
        // retry. If the fp8 file fails to load, add a GGUF Qwen2.5-VL-7B to text_encoders/.
        var llm = FindFileByPattern("Qwen2.5-VL-7B*.gguf")
                  ?? FindFileByPattern("qwen2.5-vl-7b*.gguf")
                  ?? FindFile("qwen_2.5_vl_7b_fp8_scaled.safetensors");
        var vae = FindFile("qwen_image_vae.safetensors");
        var controlNet = FindFile("Qwen-Image-InstantX-ControlNet-Inpainting.safetensors");

        if (unet is null || llm is null || vae is null || controlNet is null)
            return;

        sink.Add(new ModelDescriptor
        {
            Key = ModelKeys.QwenImageInpaint,
            DisplayName = "Qwen-Image 2512 (Inpaint)",
            Kind = ModelKind.QwenImageInpaint,
            DiffusionModelPath = unet,
            VaePath = vae,
            ControlNetPath = controlNet,
            TextEncoders = new Dictionary<TextEncoderSlot, string>
            {
                [TextEncoderSlot.Llm] = llm,
            },
            // 4-step Lightning distillation, CFG 1 (guidance baked in), AuraFlow shift 3.1.
            DefaultSteps = 4,
            DefaultCfg = 1.0f,
            DefaultSampler = "euler",
            DefaultScheduler = "simple",
            DefaultFlowShift = 3.1f,
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

    public const string QwenImageInpaint = "qwen-image-2512-inpaint";

    // TODO(v2-models): add SDXL keys here.
}

// Bridge interface so DiffusionContextHost can stay independent of the public IModelCatalog
// while we expose only one concrete catalog today.
internal interface IDiffusionBackendCatalog : Abstractions.IModelCatalog
{
    void Invalidate();
}
