using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Inference.Captioning;

/// <summary>
/// Manages vision-language model files for AI image captioning.
/// Handles model downloading, path management, and status checking.
/// </summary>
public sealed class CaptioningModelManager
{
    private const string ModelDirectory = "CaptioningModels";

    // LLaVA v1.6 34B Model
    private const string LLaVaModelFileName = "llava-v1.6-34b.Q4_K_M.gguf";
    private const string LLaVaModelUrl = "https://huggingface.co/cjpais/llava-v1.6-34b-gguf/resolve/main/llava-v1.6-34b.Q4_K_M.gguf";
    private const long ExpectedLLaVaSizeBytes = 20_000_000_000; // ~20GB

    // LLaVA CLIP Projector (required for vision)
    private const string LLaVaClipProjectorFileName = "mmproj-model-f16.gguf";
    private const string LLaVaClipProjectorUrl = "https://huggingface.co/cjpais/llava-v1.6-34b-gguf/resolve/main/mmproj-model-f16.gguf";
    private const long ExpectedLLaVaClipSizeBytes = 600_000_000; // ~600MB

    // Qwen 2.5 VL 7B Model (unsloth public GGUF repo)
    private const string Qwen25VLModelFileName = "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf";
    private const string Qwen25VLModelUrl = "https://huggingface.co/unsloth/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf";
    private const long ExpectedQwen25VLSizeBytes = 4_683_072_384; // ~4.4GB

    // Qwen 2.5 VL CLIP Projector
    private const string Qwen25VLClipProjectorFileName = "mmproj-Qwen2.5-VL-7B-F16.gguf";
    private const string Qwen25VLClipProjectorUrl = "https://huggingface.co/unsloth/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/mmproj-F16.gguf";
    private const long ExpectedQwen25VLClipSizeBytes = 1_354_163_040; // ~1.3GB

    // Qwen 3 VL 8B Model (Official Qwen repo)
    private const string Qwen3VLModelFileName = "Qwen3VL-8B-Instruct-Q4_K_M.gguf";
    private const string Qwen3VLModelUrl = "https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/main/Qwen3VL-8B-Instruct-Q4_K_M.gguf";
    private const long ExpectedQwen3VLSizeBytes = 5_027_784_800; // ~4.7GB

    // Qwen 3 VL CLIP Projector (mmproj)
    private const string Qwen3VLClipProjectorFileName = "mmproj-Qwen3VL-8B-Instruct-F16.gguf";
    private const string Qwen3VLClipProjectorUrl = "https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/main/mmproj-Qwen3VL-8B-Instruct-F16.gguf";
    private const long ExpectedQwen3VLClipSizeBytes = 1_159_029_824; // ~1.1GB

    // Qwen 3 VL 8B Abliterated v2 (mradermacher GGUF — Q8_0). No upstream download
    // URL is hardcoded because abliterated variants are user-supplied; the model is
    // resolved by scanning the configured search paths for these filenames.
    private const string Qwen3VLAbliteratedModelFileName = "Qwen3-VL-8B-Instruct-abliterated-v2.0.Q8_0.gguf";
    private const string Qwen3VLAbliteratedClipProjectorFileName = "Qwen3-VL-8B-Instruct-abliterated-v2.0.mmproj-Q8_0.gguf";
    private const long ExpectedQwen3VLAbliteratedSizeBytes = 8_700_000_000; // ~8.7GB Q8_0

    /// <summary>
    /// One row in the VRAM tier table for a downloadable captioning model.
    /// Mirrors the pair of files (base GGUF + matching mmproj) that need to
    /// land on disk for a given VRAM budget. Sizes are nominal HuggingFace
    /// figures used for status checks and progress totals; actual on-disk
    /// sizes may differ by a few percent due to LFS metadata.
    /// </summary>
    private sealed record VramTier(
        int VramGb,
        string ModelFileName, string ModelUrl, long ModelSizeBytes,
        string MmprojFileName, string MmprojUrl, long MmprojSizeBytes);

    private const string AbliteratedCaptionBase =
        "https://huggingface.co/mradermacher/Qwen3-VL-8B-Abliterated-Caption-it-GGUF/resolve/main/";
    private const string NsfwCaptionV4Base =
        "https://huggingface.co/mradermacher/Qwen3-VL-8B-NSFW-Caption-V4-GGUF/resolve/main/";

    /// <summary>
    /// VRAM tier lookup. Conservative quant picks per tier, leaving ~30% VRAM
    /// headroom for KV cache + Qwen3-VL's image-token budget. 32 GB stays on
    /// Q8_0 because Q8 is near-lossless and the jump to f16 doubles memory
    /// for negligible quality gain.
    /// </summary>
    private static readonly Dictionary<(CaptioningModelType, int), VramTier> _vramTiers = new()
    {
        // ── Abliterated-Caption-it ──────────────────────────────────────
        { (CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption,  8), new VramTier( 8,
            "Qwen3-VL-8B-Abliterated-Caption-it.Q4_K_M.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.Q4_K_M.gguf",
            5_030_000_000,
            "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-Q8_0.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-Q8_0.gguf",
            752_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, 12), new VramTier(12,
            "Qwen3-VL-8B-Abliterated-Caption-it.Q5_K_M.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.Q5_K_M.gguf",
            5_850_000_000,
            "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            1_160_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, 16), new VramTier(16,
            "Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf",
            6_730_000_000,
            "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            1_160_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, 24), new VramTier(24,
            "Qwen3-VL-8B-Abliterated-Caption-it.Q8_0.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.Q8_0.gguf",
            8_710_000_000,
            "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            1_160_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption, 32), new VramTier(32,
            "Qwen3-VL-8B-Abliterated-Caption-it.Q8_0.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.Q8_0.gguf",
            8_710_000_000,
            "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            AbliteratedCaptionBase + "Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            1_160_000_000) },

        // ── NSFW-Caption-V4 ────────────────────────────────────────────
        { (CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4,  8), new VramTier( 8,
            "Qwen3-VL-8B-NSFW-Caption-V4.Q4_K_M.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.Q4_K_M.gguf",
            4_680_000_000,
            "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-Q8_0.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-Q8_0.gguf",
            701_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, 12), new VramTier(12,
            "Qwen3-VL-8B-NSFW-Caption-V4.Q5_K_M.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.Q5_K_M.gguf",
            5_450_000_000,
            "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            1_080_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, 16), new VramTier(16,
            "Qwen3-VL-8B-NSFW-Caption-V4.Q6_K.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.Q6_K.gguf",
            6_260_000_000,
            "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            1_080_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, 24), new VramTier(24,
            "Qwen3-VL-8B-NSFW-Caption-V4.Q8_0.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.Q8_0.gguf",
            8_110_000_000,
            "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            1_080_000_000) },
        { (CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4, 32), new VramTier(32,
            "Qwen3-VL-8B-NSFW-Caption-V4.Q8_0.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.Q8_0.gguf",
            8_110_000_000,
            "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            NsfwCaptionV4Base + "Qwen3-VL-8B-NSFW-Caption-V4.mmproj-f16.gguf",
            1_080_000_000) },
    };

    /// <summary>Canonical VRAM tiers offered for downloadable captioning models.</summary>
    private static readonly int[] DefaultVramTiers = { 8, 12, 16, 24, 32 };

    /// <summary>
    /// Default download/install directory, also the first search path.
    /// </summary>
    private readonly string _modelsBasePath;

    /// <summary>
    /// Static search paths known at construction time: the default base path
    /// plus anything from the <c>DIFFUSION_NEXUS_CAPTIONING_MODELS_DIR</c>
    /// environment variable. ComfyUI installation paths are added lazily via
    /// <see cref="_extraSearchPathsProvider"/>.
    /// </summary>
    private readonly IReadOnlyList<string> _staticSearchPaths;

    /// <summary>
    /// Lazily evaluated callback that returns additional roots to scan — used
    /// to wire ComfyUI installation directories (including paths from
    /// <c>extra_model_paths.yaml</c>) without making this project depend on
    /// the UI/data access layers.
    /// </summary>
    private readonly Func<IReadOnlyList<string>>? _extraSearchPathsProvider;

    private readonly HttpClient _httpClient;
    private readonly object _downloadLock = new();
    private readonly Dictionary<CaptioningModelType, bool> _downloadingModels = new();

    /// <summary>
    /// Environment variable read at construction to add extra search paths.
    /// Separator is the platform's <see cref="Path.PathSeparator"/> (';' on Windows).
    /// </summary>
    public const string ExtraSearchPathsEnvVar = "DIFFUSION_NEXUS_CAPTIONING_MODELS_DIR";

    /// <summary>
    /// Creates a new CaptioningModelManager with the default models directory.
    /// </summary>
    public CaptioningModelManager() : this(null, null, null) { }

    /// <summary>
    /// Creates a new CaptioningModelManager.
    /// </summary>
    /// <param name="modelsBasePath">Optional custom path for model storage.</param>
    /// <param name="httpClient">Optional HttpClient for downloads.</param>
    /// <param name="extraSearchPathsProvider">
    /// Optional callback invoked at each resolution to obtain additional root
    /// directories to scan recursively. Wire this to a ComfyUI installation
    /// path provider so users' existing GGUF/mmproj files (including paths
    /// declared in <c>extra_model_paths.yaml</c>) are discovered automatically.
    /// The callback is invoked lazily, so it can return live results without
    /// requiring the manager to be reconstructed when installations change.
    /// </param>
    public CaptioningModelManager(
        string? modelsBasePath,
        HttpClient? httpClient,
        Func<IReadOnlyList<string>>? extraSearchPathsProvider = null)
    {
        _modelsBasePath = modelsBasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            ModelDirectory);

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromHours(2); // Large model download timeout

        Directory.CreateDirectory(_modelsBasePath);

        _staticSearchPaths = BuildStaticSearchPaths(_modelsBasePath);
        _extraSearchPathsProvider = extraSearchPathsProvider;
    }

    /// <summary>
    /// Builds the ordered list of directories to scan when resolving a model
    /// file. The base path comes first, followed by any directories listed in
    /// the <see cref="ExtraSearchPathsEnvVar"/> environment variable (separated
    /// by <see cref="Path.PathSeparator"/>). Duplicates are removed; missing
    /// directories are kept in the list so the order of preference stays
    /// stable if the user later creates them.
    /// </summary>
    private static IReadOnlyList<string> BuildStaticSearchPaths(string basePath)
    {
        var paths = new List<string> { basePath };

        var envValue = Environment.GetEnvironmentVariable(ExtraSearchPathsEnvVar);
        if (!string.IsNullOrWhiteSpace(envValue))
        {
            foreach (var raw in envValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!paths.Contains(raw, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(raw);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// All directories currently scanned when resolving a model — static paths
    /// plus the live result from <see cref="_extraSearchPathsProvider"/>.
    /// </summary>
    private IReadOnlyList<string> GetCurrentSearchPaths()
    {
        if (_extraSearchPathsProvider is null)
        {
            return _staticSearchPaths;
        }

        var merged = new List<string>(_staticSearchPaths);
        try
        {
            foreach (var extra in _extraSearchPathsProvider())
            {
                if (!string.IsNullOrWhiteSpace(extra) &&
                    !merged.Contains(extra, StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(extra);
                }
            }
        }
        catch (Exception ex)
        {
            // A misbehaving provider should never block the default resolution.
            Log.Warning(ex, "Extra captioning search-paths provider threw — falling back to static paths only.");
        }
        return merged;
    }

    /// <summary>
    /// Walks every search path looking for <paramref name="fileName"/>. Each
    /// path is checked as both a direct parent (file sits at <c>dir/fileName</c>)
    /// and as a root to scan recursively — that recursive walk is what lets us
    /// find a user-supplied GGUF stashed in ComfyUI's nested model tree (e.g.
    /// <c>ComfyUI/models/text_encoders/.../*.gguf</c>) without hardcoding every
    /// possible subfolder. Returns the first hit; falls back to the default
    /// base path so download targets remain stable when nothing exists yet.
    /// </summary>
    private string ResolveFile(string fileName)
    {
        foreach (var dir in GetCurrentSearchPaths())
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                continue;
            }

            var direct = Path.Combine(dir, fileName);
            if (File.Exists(direct))
            {
                return direct;
            }

            // EnumerateFiles with a top-level pattern + AllDirectories is far
            // cheaper than computing every subfolder. The pattern matches the
            // exact filename so we get O(matches) work, not O(files).
            try
            {
                var match = Directory.EnumerateFiles(dir, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (match is not null)
                {
                    return match;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Skip directories we can't read — common for partial ComfyUI layouts.
            }
            catch (IOException)
            {
                // Same — transient/locked subtrees should not abort discovery.
            }
        }

        return Path.Combine(_modelsBasePath, fileName);
    }

    /// <summary>
    /// True for models that support VRAM-tier-aware downloads (a row exists in
    /// <see cref="_vramTiers"/>). The legacy upstream-URL models and the
    /// user-supplied abliterated entry return false here.
    /// </summary>
    public static bool IsTieredDownloadable(CaptioningModelType modelType)
        => modelType is CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption
                     or CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4;

    /// <summary>
    /// VRAM tiers (in GB) available for a downloadable model. Empty array for
    /// models that don't support tiered downloads.
    /// </summary>
    public static int[] GetSupportedVramTiers(CaptioningModelType modelType)
        => IsTieredDownloadable(modelType) ? (int[])DefaultVramTiers.Clone() : Array.Empty<int>();

    /// <summary>
    /// Picks the largest VRAM tier whose budget is &lt;= <paramref name="requestedGb"/>.
    /// Mirrors the ComfyUI <c>VramProfileHelper</c> "best fitting profile"
    /// behaviour so a card with more VRAM than any defined tier still maps
    /// onto our top tier instead of erroring out.
    /// </summary>
    private static VramTier? PickFittingTier(CaptioningModelType modelType, int requestedGb)
    {
        VramTier? best = null;
        foreach (var ((t, gb), tier) in _vramTiers)
        {
            if (t != modelType || gb > requestedGb) continue;
            if (best is null || gb > best.VramGb) best = tier;
        }
        return best;
    }

    /// <summary>
    /// Returns the on-disk path to the tier-specific GGUF for a tiered model.
    /// </summary>
    public string GetModelPath(CaptioningModelType modelType, int vramGb)
    {
        if (!IsTieredDownloadable(modelType))
            return GetModelPath(modelType);

        var tier = PickFittingTier(modelType, vramGb)
            ?? throw new ArgumentOutOfRangeException(nameof(vramGb),
                $"No VRAM tier defined for {modelType} at or below {vramGb} GB.");
        return ResolveFile(tier.ModelFileName);
    }

    /// <summary>
    /// Returns the on-disk path to the tier-specific mmproj for a tiered model.
    /// </summary>
    public string GetClipProjectorPath(CaptioningModelType modelType, int vramGb)
    {
        if (!IsTieredDownloadable(modelType))
            return GetClipProjectorPath(modelType);

        var tier = PickFittingTier(modelType, vramGb)
            ?? throw new ArgumentOutOfRangeException(nameof(vramGb),
                $"No VRAM tier defined for {modelType} at or below {vramGb} GB.");
        return ResolveFile(tier.MmprojFileName);
    }

    /// <summary>
    /// For tiered models, finds the largest tier whose GGUF + mmproj pair is
    /// already present in the search paths. Lets the captioning service load
    /// whatever the user actually downloaded without forcing them to re-pick
    /// the VRAM tier every time.
    /// </summary>
    private VramTier? FindLargestPresentTier(CaptioningModelType modelType)
    {
        VramTier? best = null;
        foreach (var ((t, _), tier) in _vramTiers)
        {
            if (t != modelType) continue;
            var modelPath = ResolveFile(tier.ModelFileName);
            var mmprojPath = ResolveFile(tier.MmprojFileName);
            if (!File.Exists(modelPath) || !File.Exists(mmprojPath)) continue;
            if (best is null || tier.ModelSizeBytes > best.ModelSizeBytes) best = tier;
        }
        return best;
    }

    /// <summary>
    /// Gets the full path to the model file for a given model type. The file is
    /// resolved against the configured search paths so user-supplied GGUFs in a
    /// custom directory are picked up without copying. For tiered models,
    /// returns whichever quantization is already on disk (largest preferred),
    /// or the default 8 GB tier path as the download target if nothing exists.
    /// </summary>
    public string GetModelPath(CaptioningModelType modelType)
    {
        if (IsTieredDownloadable(modelType))
        {
            var present = FindLargestPresentTier(modelType);
            if (present is not null) return ResolveFile(present.ModelFileName);
            var defaultTier = PickFittingTier(modelType, DefaultVramTiers[0])!;
            return ResolveFile(defaultTier.ModelFileName);
        }

        return modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => ResolveFile(LLaVaModelFileName),
            CaptioningModelType.Qwen2_5_VL_7B => ResolveFile(Qwen25VLModelFileName),
            CaptioningModelType.Qwen3_VL_8B => ResolveFile(Qwen3VLModelFileName),
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ResolveFile(Qwen3VLAbliteratedModelFileName),
            _ => throw new ArgumentOutOfRangeException(nameof(modelType))
        };
    }

    /// <summary>
    /// Gets the full path to the CLIP/mmproj projector file for a given model type.
    /// </summary>
    public string GetClipProjectorPath(CaptioningModelType modelType)
    {
        if (IsTieredDownloadable(modelType))
        {
            var present = FindLargestPresentTier(modelType);
            if (present is not null) return ResolveFile(present.MmprojFileName);
            var defaultTier = PickFittingTier(modelType, DefaultVramTiers[0])!;
            return ResolveFile(defaultTier.MmprojFileName);
        }

        return modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => ResolveFile(LLaVaClipProjectorFileName),
            CaptioningModelType.Qwen2_5_VL_7B => ResolveFile(Qwen25VLClipProjectorFileName),
            CaptioningModelType.Qwen3_VL_8B => ResolveFile(Qwen3VLClipProjectorFileName),
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ResolveFile(Qwen3VLAbliteratedClipProjectorFileName),
            _ => throw new ArgumentOutOfRangeException(nameof(modelType))
        };
    }

    /// <summary>
    /// Gets the expected size of the model file in bytes. For tiered models we
    /// report the size of whichever tier is currently on disk (so the corruption
    /// check has a sensible target) or the smallest tier's size as a download
    /// estimate when nothing is present yet.
    /// </summary>
    public long GetExpectedModelSize(CaptioningModelType modelType)
    {
        if (IsTieredDownloadable(modelType))
        {
            var present = FindLargestPresentTier(modelType);
            if (present is not null) return present.ModelSizeBytes;
            var defaultTier = PickFittingTier(modelType, DefaultVramTiers[0])!;
            return defaultTier.ModelSizeBytes;
        }

        return modelType switch
        {
            CaptioningModelType.LLaVA_v1_6_34B => ExpectedLLaVaSizeBytes,
            CaptioningModelType.Qwen2_5_VL_7B => ExpectedQwen25VLSizeBytes,
            CaptioningModelType.Qwen3_VL_8B => ExpectedQwen3VLSizeBytes,
            CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ExpectedQwen3VLAbliteratedSizeBytes,
            _ => throw new ArgumentOutOfRangeException(nameof(modelType))
        };
    }

    /// <summary>
    /// Total bytes for a specific tier (model + mmproj). Used by the download
    /// dialog to report a meaningful total before bytes start flowing.
    /// </summary>
    public long GetExpectedTierTotalBytes(CaptioningModelType modelType, int vramGb)
    {
        var tier = PickFittingTier(modelType, vramGb)
            ?? throw new ArgumentOutOfRangeException(nameof(vramGb),
                $"No VRAM tier defined for {modelType} at or below {vramGb} GB.");
        return tier.ModelSizeBytes + tier.MmprojSizeBytes;
    }

    /// <summary>
    /// Gets the display name for a model type.
    /// </summary>
    public static string GetDisplayName(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => "LLaVA v1.6 34B",
        CaptioningModelType.Qwen2_5_VL_7B => "Qwen 2.5 VL 7B",
        CaptioningModelType.Qwen3_VL_8B => "Qwen 3 VL 8B",
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => "Qwen 3 VL 8B — Abliterated v2 (Q8_0)",
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption => "Qwen 3 VL 8B — Abliterated Caption-it",
        CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4 => "Qwen 3 VL 8B — NSFW Caption V4",
        _ => modelType.ToString()
    };

    /// <summary>
    /// Gets the description for a model type.
    /// </summary>
    public static string GetDescription(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => "High quality vision-language model. Excellent for detailed descriptions. Requires ~20GB disk space and significant GPU VRAM.",
        CaptioningModelType.Qwen2_5_VL_7B => "Efficient vision-language model with strong performance. Good balance of quality and resource usage. Requires ~5GB disk space.",
        CaptioningModelType.Qwen3_VL_8B => "Most powerful Qwen VLM. Features 256K context, visual agent capabilities, 3D grounding, and 32-language OCR. Requires ~5.5GB disk space.",
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => "Uncensored Qwen3-VL 8B (Q8_0 quant). User-supplied — drop the .gguf and .mmproj files into the captioning models folder or set the DIFFUSION_NEXUS_CAPTIONING_MODELS_DIR environment variable.",
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Caption => "Uncensored Qwen3-VL 8B fine-tuned for general image captioning (mradermacher/Qwen3-VL-8B-Abliterated-Caption-it-GGUF). Picks a quantization based on your VRAM tier; 8 GB → Q4_K_M up to 24/32 GB → Q8_0.",
        CaptioningModelType.Qwen3_VL_8B_NSFW_Caption_V4 => "Qwen3-VL 8B fine-tuned specifically for NSFW image captioning (mradermacher/Qwen3-VL-8B-NSFW-Caption-V4-GGUF). Same VRAM-tier quantization picks as the Caption-it sibling.",
        _ => "Unknown model."
    };

    /// <summary>
    /// Configured search paths in resolution order. Exposed so the UI can show
    /// the user where files are looked up. Includes static (default + env var)
    /// and live (extra-paths-provider) entries.
    /// </summary>
    public IReadOnlyList<string> SearchPaths => GetCurrentSearchPaths();

    /// <summary>
    /// A potential download target for the user to pick from in the download
    /// options dialog.
    /// </summary>
    /// <param name="Path">
    /// The directory captioning files will actually be written into. For
    /// ComfyUI model roots this is a <c>Captioning</c> subfolder inside the
    /// install's <c>models/</c> directory; for the Core default it's the
    /// canonical Core folder. The caller passes this verbatim to
    /// <see cref="DownloadModelAsync(CaptioningModelType, int, string, IProgress{ModelDownloadProgress}?, CancellationToken)"/>.
    /// </param>
    /// <param name="Label">Multi-line display string for the picker row.</param>
    /// <param name="FreeBytes">Free disk space on the destination's volume.</param>
    /// <param name="IsDefault">True for the Core default folder.</param>
    public sealed record DownloadDestination(string Path, string Label, long FreeBytes, bool IsDefault);

    /// <summary>
    /// Subfolder appended under each ComfyUI <c>models</c> root so captioning
    /// GGUFs don't pollute the top level of the user's model tree. The path
    /// resolver scans recursively, so files placed here remain discoverable.
    /// </summary>
    private const string CaptioningSubfolderName = "Captioning";

    /// <summary>
    /// Enumerates writable directories the user can choose as a download target.
    /// Only the Core default and each install's <c>models</c>/<c>Models</c>
    /// root are offered — never their per-model-type subfolders — so the user
    /// isn't forced to pick between a dozen near-identical paths like
    /// <c>D:\Matrix\Models\StableDiffusion</c>, <c>...\TextEncoders</c>,
    /// <c>...\Lora</c> etc. For ComfyUI roots the actual write target is
    /// the <c>Captioning</c> subfolder of that root.
    /// </summary>
    public IReadOnlyList<DownloadDestination> GetDownloadDestinations()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<DownloadDestination>();

        // Default Core folder — always first, always offered even if empty.
        results.Add(new DownloadDestination(
            _modelsBasePath,
            $"Diffusion Nexus Core (default)\n{_modelsBasePath}",
            GetFreeBytes(_modelsBasePath),
            IsDefault: true));
        seen.Add(_modelsBasePath);

        // Walk the live search paths and keep only the canonical "models"
        // root for each install. Subfolders inside a models root (the
        // per-model-type entries declared in extra_model_paths.yaml) are
        // skipped because writing inside them is the wrong contract — those
        // folders are reserved for ComfyUI's own model-type categories.
        foreach (var rawPath in GetCurrentSearchPaths())
        {
            if (string.IsNullOrWhiteSpace(rawPath)) continue;
            if (!Directory.Exists(rawPath)) continue;

            // Trim trailing separators so basename comparison is stable.
            var path = rawPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
            var basename = System.IO.Path.GetFileName(path);

            if (!basename.Equals("models", StringComparison.OrdinalIgnoreCase))
            {
                // Skip per-type subdirs (text_encoders, clip_vision, loras, …).
                // The /models root will already be present in the same list.
                continue;
            }

            if (!seen.Add(path)) continue;

            var writeTarget = System.IO.Path.Combine(path, CaptioningSubfolderName);
            results.Add(new DownloadDestination(
                writeTarget,
                $"{path}\n→ {CaptioningSubfolderName} subfolder will be created",
                GetFreeBytes(path),
                IsDefault: false));
        }

        return results;
    }

    /// <summary>
    /// Free-space lookup with a forgiving fallback: any failure (UNC paths
    /// without a drive letter, missing root, access denied) returns 0 rather
    /// than throwing — the UI surfaces "unknown" for that destination.
    /// </summary>
    private static long GetFreeBytes(string path)
    {
        try
        {
            var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
            if (string.IsNullOrWhiteSpace(root)) return 0;
            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.AvailableFreeSpace : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Gets the current status of a model.
    /// </summary>
    public CaptioningModelStatus GetModelStatus(CaptioningModelType modelType)
    {
        lock (_downloadLock)
        {
            if (_downloadingModels.TryGetValue(modelType, out var isDownloading) && isDownloading)
                return CaptioningModelStatus.Downloading;
        }

        var modelPath = GetModelPath(modelType);
        var clipPath = GetClipProjectorPath(modelType);

        if (!File.Exists(modelPath) || !File.Exists(clipPath))
            return CaptioningModelStatus.NotDownloaded;

        var modelInfo = new FileInfo(modelPath);
        var expectedSize = GetExpectedModelSize(modelType);

        // Basic size check - model should be at least 80% of expected size
        if (modelInfo.Length < expectedSize * 0.8)
            return CaptioningModelStatus.Corrupted;

        return CaptioningModelStatus.Ready;
    }

    /// <summary>
    /// Gets information about a model.
    /// </summary>
    public CaptioningModelInfo GetModelInfo(CaptioningModelType modelType)
    {
        var modelPath = GetModelPath(modelType);
        var status = GetModelStatus(modelType);
        var fileSize = File.Exists(modelPath) ? new FileInfo(modelPath).Length : 0;
        var expectedSize = GetExpectedModelSize(modelType);

        return new CaptioningModelInfo(
            modelType,
            status,
            modelPath,
            fileSize,
            expectedSize,
            GetDisplayName(modelType),
            GetDescription(modelType));
    }

    /// <summary>
    /// Downloads a model and its CLIP projector from HuggingFace.
    /// </summary>
    public async Task<bool> DownloadModelAsync(
        CaptioningModelType modelType,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetModelStatus(modelType);
        if (status == CaptioningModelStatus.Ready)
        {
            progress?.Report(new ModelDownloadProgress(
                GetExpectedModelSize(modelType),
                GetExpectedModelSize(modelType),
                "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_downloadingModels.TryGetValue(modelType, out var isDownloading) && isDownloading)
            {
                Log.Warning("{ModelType} model download already in progress", modelType);
                return false;
            }
            _downloadingModels[modelType] = true;
        }

        try
        {
            // Abliterated builds are user-supplied; there is no canonical upstream
            // URL we trust to host them. Make the absence explicit instead of
            // letting a switch-default fall through to a confusing exception.
            if (modelType == CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8)
            {
                progress?.Report(new ModelDownloadProgress(0, ExpectedQwen3VLAbliteratedSizeBytes,
                    "Abliterated builds are user-supplied; place the .gguf and .mmproj files in the captioning models folder or set DIFFUSION_NEXUS_CAPTIONING_MODELS_DIR."));
                Log.Warning("DownloadModelAsync called for {ModelType}, which has no upstream URL — skipping.", modelType);
                return false;
            }

            // Tiered models need a VRAM budget to pick a quant; the no-tier
            // overload can't service them. Direct the caller to the (type,
            // vramGb) overload instead of guessing.
            if (IsTieredDownloadable(modelType))
            {
                progress?.Report(new ModelDownloadProgress(0, 0,
                    $"{GetDisplayName(modelType)} requires a VRAM tier — call DownloadModelAsync(type, vramGb)."));
                Log.Warning("Non-tiered DownloadModelAsync called for tiered model {ModelType}; caller must pass vramGb.", modelType);
                return false;
            }

            var (modelUrl, modelPath, modelSize) = modelType switch
            {
                CaptioningModelType.LLaVA_v1_6_34B => (LLaVaModelUrl, GetModelPath(modelType), ExpectedLLaVaSizeBytes),
                CaptioningModelType.Qwen2_5_VL_7B => (Qwen25VLModelUrl, GetModelPath(modelType), ExpectedQwen25VLSizeBytes),
                CaptioningModelType.Qwen3_VL_8B => (Qwen3VLModelUrl, GetModelPath(modelType), ExpectedQwen3VLSizeBytes),
                _ => throw new ArgumentOutOfRangeException(nameof(modelType))
            };

            var (clipUrl, clipPath, clipSize) = modelType switch
            {
                CaptioningModelType.LLaVA_v1_6_34B => (LLaVaClipProjectorUrl, GetClipProjectorPath(modelType), ExpectedLLaVaClipSizeBytes),
                CaptioningModelType.Qwen2_5_VL_7B => (Qwen25VLClipProjectorUrl, GetClipProjectorPath(modelType), ExpectedQwen25VLClipSizeBytes),
                CaptioningModelType.Qwen3_VL_8B => (Qwen3VLClipProjectorUrl, GetClipProjectorPath(modelType), ExpectedQwen3VLClipSizeBytes),
                _ => throw new ArgumentOutOfRangeException(nameof(modelType))
            };

            var totalSize = modelSize + clipSize;
            var displayName = GetDisplayName(modelType);

            // Download CLIP projector first (smaller file)
            if (!File.Exists(clipPath) || new FileInfo(clipPath).Length < clipSize * 0.8)
            {
                progress?.Report(new ModelDownloadProgress(0, totalSize, $"Downloading {displayName} CLIP projector..."));
                var clipSuccess = await DownloadFileInternalAsync(
                    clipUrl, clipPath, clipSize, $"{displayName} CLIP",
                    new Progress<ModelDownloadProgress>(p =>
                        progress?.Report(new ModelDownloadProgress(p.BytesDownloaded, totalSize, p.Status))),
                    cancellationToken);

                if (!clipSuccess)
                    return false;
            }

            // Download main model
            if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < modelSize * 0.8)
            {
                progress?.Report(new ModelDownloadProgress(clipSize, totalSize, $"Downloading {displayName} model..."));
                var modelSuccess = await DownloadFileInternalAsync(
                    modelUrl, modelPath, modelSize, displayName,
                    new Progress<ModelDownloadProgress>(p =>
                        progress?.Report(new ModelDownloadProgress(clipSize + p.BytesDownloaded, totalSize, p.Status))),
                    cancellationToken);

                if (!modelSuccess)
                    return false;
            }

            progress?.Report(new ModelDownloadProgress(totalSize, totalSize, "Download complete"));
            return true;
        }
        finally
        {
            lock (_downloadLock)
            {
                _downloadingModels[modelType] = false;
            }
        }
    }

    /// <summary>
    /// Downloads a VRAM-tiered captioning model and its matching mmproj to
    /// the default Core models folder. The tier table picks the quantization;
    /// if <paramref name="vramGb"/> exceeds any defined tier we fall back to
    /// the largest one defined (same policy as the ComfyUI
    /// <c>VramProfileHelper.SelectBestMatchingLinks</c>).
    /// </summary>
    public Task<bool> DownloadModelAsync(
        CaptioningModelType modelType,
        int vramGb,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => DownloadModelAsync(modelType, vramGb, _modelsBasePath, progress, cancellationToken);

    /// <summary>
    /// Downloads a VRAM-tiered captioning model into a specific destination
    /// directory. Use this overload to honour a user-picked target (e.g. a
    /// ComfyUI install's models folder or a path declared in extra_model_paths.yaml).
    /// The directory is created if missing; the resulting files are written
    /// directly there, not in the default Core folder.
    /// </summary>
    public async Task<bool> DownloadModelAsync(
        CaptioningModelType modelType,
        int vramGb,
        string destinationDirectory,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsTieredDownloadable(modelType))
        {
            // Forward to the non-tiered path for legacy models so callers can
            // safely use the new overload unconditionally.
            return await DownloadModelAsync(modelType, progress, cancellationToken);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        Directory.CreateDirectory(destinationDirectory);

        var tier = PickFittingTier(modelType, vramGb)
            ?? throw new ArgumentOutOfRangeException(nameof(vramGb),
                $"No VRAM tier defined for {modelType} at or below {vramGb} GB.");

        var totalSize = tier.ModelSizeBytes + tier.MmprojSizeBytes;
        var displayName = GetDisplayName(modelType);

        var modelDestPath = Path.Combine(destinationDirectory, tier.ModelFileName);
        var mmprojDestPath = Path.Combine(destinationDirectory, tier.MmprojFileName);

        // Resolve current presence via search paths (the user may already have
        // the files under a configured extra path). Only download what's
        // actually missing.
        var modelOnDisk = ResolveFile(tier.ModelFileName);
        var mmprojOnDisk = ResolveFile(tier.MmprojFileName);
        var modelPresent = File.Exists(modelOnDisk) && new FileInfo(modelOnDisk).Length >= tier.ModelSizeBytes * 0.8;
        var mmprojPresent = File.Exists(mmprojOnDisk) && new FileInfo(mmprojOnDisk).Length >= tier.MmprojSizeBytes * 0.8;

        if (modelPresent && mmprojPresent)
        {
            progress?.Report(new ModelDownloadProgress(totalSize, totalSize,
                $"{displayName} ({tier.VramGb} GB tier) already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_downloadingModels.TryGetValue(modelType, out var isDownloading) && isDownloading)
            {
                Log.Warning("{ModelType} download already in progress", modelType);
                return false;
            }
            _downloadingModels[modelType] = true;
        }

        try
        {
            // mmproj first (smaller — quicker feedback that the connection works).
            if (!mmprojPresent)
            {
                progress?.Report(new ModelDownloadProgress(0, totalSize,
                    $"Downloading {displayName} mmproj ({tier.VramGb} GB tier)..."));
                var mmprojSuccess = await DownloadFileInternalAsync(
                    tier.MmprojUrl, mmprojDestPath, tier.MmprojSizeBytes, $"{displayName} mmproj",
                    new Progress<ModelDownloadProgress>(p =>
                        progress?.Report(new ModelDownloadProgress(p.BytesDownloaded, totalSize, p.Status))),
                    cancellationToken);
                if (!mmprojSuccess) return false;
            }

            if (!modelPresent)
            {
                progress?.Report(new ModelDownloadProgress(tier.MmprojSizeBytes, totalSize,
                    $"Downloading {displayName} model ({tier.VramGb} GB tier)..."));
                var modelSuccess = await DownloadFileInternalAsync(
                    tier.ModelUrl, modelDestPath, tier.ModelSizeBytes, $"{displayName} model",
                    new Progress<ModelDownloadProgress>(p =>
                        progress?.Report(new ModelDownloadProgress(tier.MmprojSizeBytes + p.BytesDownloaded, totalSize, p.Status))),
                    cancellationToken);
                if (!modelSuccess) return false;
            }

            progress?.Report(new ModelDownloadProgress(totalSize, totalSize,
                $"{displayName} ({tier.VramGb} GB tier) download complete"));
            return true;
        }
        finally
        {
            lock (_downloadLock)
            {
                _downloadingModels[modelType] = false;
            }
        }
    }

    /// <summary>
    /// Internal method to download a file with progress reporting.
    /// </summary>
    private async Task<bool> DownloadFileInternalAsync(
        string url,
        string destinationPath,
        long expectedSize,
        string modelName,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(new ModelDownloadProgress(0, expectedSize, "Starting download..."));

            var tempPath = destinationPath + ".download";

            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSize;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var fileStream = new FileStream(
                tempPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true);

            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;
            var lastProgressUpdate = DateTime.UtcNow;

            while ((read = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                bytesRead += read;

                // Throttle progress updates
                if ((DateTime.UtcNow - lastProgressUpdate).TotalMilliseconds > 250)
                {
                    progress?.Report(new ModelDownloadProgress(
                        bytesRead,
                        totalBytes,
                        $"Downloading {modelName}... {bytesRead / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB"));
                    lastProgressUpdate = DateTime.UtcNow;
                }
            }

            await fileStream.FlushAsync(cancellationToken);
            fileStream.Close();

            // Rename temp file to final name
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(tempPath, destinationPath);

            progress?.Report(new ModelDownloadProgress(bytesRead, totalBytes, "Download complete"));

            Log.Information("{ModelName} downloaded successfully: {Path}", modelName, destinationPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ModelDownloadProgress(0, expectedSize, "Download cancelled"));
            CleanupPartialDownload(destinationPath);
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download {ModelName}", modelName);
            progress?.Report(new ModelDownloadProgress(0, expectedSize, $"Download failed: {ex.Message}"));
            CleanupPartialDownload(destinationPath);
            return false;
        }
    }

    private static void CleanupPartialDownload(string filePath)
    {
        var tempPath = filePath + ".download";
        try
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to cleanup partial download: {Path}", tempPath);
        }
    }

    /// <summary>
    /// Deletes a model and its CLIP projector files.
    /// </summary>
    public void DeleteModel(CaptioningModelType modelType)
    {
        try
        {
            var modelPath = GetModelPath(modelType);
            var clipPath = GetClipProjectorPath(modelType);

            if (File.Exists(modelPath))
            {
                File.Delete(modelPath);
                Log.Information("{ModelType} model deleted: {Path}", modelType, modelPath);
            }

            if (File.Exists(clipPath))
            {
                File.Delete(clipPath);
                Log.Information("{ModelType} CLIP projector deleted: {Path}", modelType, clipPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete {ModelType} model files", modelType);
            throw;
        }
    }
}
