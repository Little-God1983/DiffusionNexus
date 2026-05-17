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
    /// Gets the full path to the model file for a given model type. The file is
    /// resolved against the configured search paths so user-supplied GGUFs in a
    /// custom directory are picked up without copying.
    /// </summary>
    public string GetModelPath(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => ResolveFile(LLaVaModelFileName),
        CaptioningModelType.Qwen2_5_VL_7B => ResolveFile(Qwen25VLModelFileName),
        CaptioningModelType.Qwen3_VL_8B => ResolveFile(Qwen3VLModelFileName),
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ResolveFile(Qwen3VLAbliteratedModelFileName),
        _ => throw new ArgumentOutOfRangeException(nameof(modelType))
    };

    /// <summary>
    /// Gets the full path to the CLIP/mmproj projector file for a given model type.
    /// </summary>
    public string GetClipProjectorPath(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => ResolveFile(LLaVaClipProjectorFileName),
        CaptioningModelType.Qwen2_5_VL_7B => ResolveFile(Qwen25VLClipProjectorFileName),
        CaptioningModelType.Qwen3_VL_8B => ResolveFile(Qwen3VLClipProjectorFileName),
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ResolveFile(Qwen3VLAbliteratedClipProjectorFileName),
        _ => throw new ArgumentOutOfRangeException(nameof(modelType))
    };

    /// <summary>
    /// Gets the expected size of the model file in bytes.
    /// </summary>
    public long GetExpectedModelSize(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => ExpectedLLaVaSizeBytes,
        CaptioningModelType.Qwen2_5_VL_7B => ExpectedQwen25VLSizeBytes,
        CaptioningModelType.Qwen3_VL_8B => ExpectedQwen3VLSizeBytes,
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => ExpectedQwen3VLAbliteratedSizeBytes,
        _ => throw new ArgumentOutOfRangeException(nameof(modelType))
    };

    /// <summary>
    /// Gets the display name for a model type.
    /// </summary>
    public static string GetDisplayName(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => "LLaVA v1.6 34B",
        CaptioningModelType.Qwen2_5_VL_7B => "Qwen 2.5 VL 7B",
        CaptioningModelType.Qwen3_VL_8B => "Qwen 3 VL 8B",
        CaptioningModelType.Qwen3_VL_8B_Abliterated_Q8 => "Qwen 3 VL 8B — Abliterated v2 (Q8_0)",
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
        _ => "Unknown model."
    };

    /// <summary>
    /// Configured search paths in resolution order. Exposed so the UI can show
    /// the user where files are looked up. Includes static (default + env var)
    /// and live (extra-paths-provider) entries.
    /// </summary>
    public IReadOnlyList<string> SearchPaths => GetCurrentSearchPaths();

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
