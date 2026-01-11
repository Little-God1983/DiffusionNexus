using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Captioning;

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

    // Qwen 2.5 VL 7B Model
    private const string Qwen25VLModelFileName = "Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf";
    private const string Qwen25VLModelUrl = "https://huggingface.co/bartowski/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/Qwen2.5-VL-7B-Instruct-Q4_K_M.gguf";
    private const long ExpectedQwen25VLSizeBytes = 5_000_000_000; // ~5GB

    // Qwen 2.5 VL CLIP Projector
    private const string Qwen25VLClipProjectorFileName = "Qwen2.5-VL-7B-Instruct-mmproj-f16.gguf";
    private const string Qwen25VLClipProjectorUrl = "https://huggingface.co/bartowski/Qwen2.5-VL-7B-Instruct-GGUF/resolve/main/Qwen2.5-VL-7B-Instruct-mmproj-f16.gguf";
    private const long ExpectedQwen25VLClipSizeBytes = 1_500_000_000; // ~1.5GB

    // Qwen 3 VL 8B Model (Official Qwen repo)
    private const string Qwen3VLModelFileName = "Qwen3-VL-8B-Instruct-Q4_K_M.gguf";
    private const string Qwen3VLModelUrl = "https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/main/Qwen3-VL-8B-Instruct-Q4_K_M.gguf";
    private const long ExpectedQwen3VLSizeBytes = 5_500_000_000; // ~5.5GB

    // Qwen 3 VL CLIP Projector (mmproj)
    private const string Qwen3VLClipProjectorFileName = "Qwen3-VL-8B-Instruct-mmproj-f16.gguf";
    private const string Qwen3VLClipProjectorUrl = "https://huggingface.co/Qwen/Qwen3-VL-8B-Instruct-GGUF/resolve/main/Qwen3-VL-8B-Instruct-mmproj-f16.gguf";
    private const long ExpectedQwen3VLClipSizeBytes = 1_600_000_000; // ~1.6GB

    private readonly string _modelsBasePath;
    private readonly HttpClient _httpClient;
    private readonly object _downloadLock = new();
    private readonly Dictionary<CaptioningModelType, bool> _downloadingModels = new();

    /// <summary>
    /// Creates a new CaptioningModelManager with the default models directory.
    /// </summary>
    public CaptioningModelManager() : this(null, null) { }

    /// <summary>
    /// Creates a new CaptioningModelManager.
    /// </summary>
    /// <param name="modelsBasePath">Optional custom path for model storage.</param>
    /// <param name="httpClient">Optional HttpClient for downloads.</param>
    public CaptioningModelManager(string? modelsBasePath, HttpClient? httpClient)
    {
        _modelsBasePath = modelsBasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            ModelDirectory);

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromHours(2); // Large model download timeout

        Directory.CreateDirectory(_modelsBasePath);
    }

    /// <summary>
    /// Gets the full path to the model file for a given model type.
    /// </summary>
    public string GetModelPath(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => Path.Combine(_modelsBasePath, LLaVaModelFileName),
        CaptioningModelType.Qwen2_5_VL_7B => Path.Combine(_modelsBasePath, Qwen25VLModelFileName),
        CaptioningModelType.Qwen3_VL_8B => Path.Combine(_modelsBasePath, Qwen3VLModelFileName),
        _ => throw new ArgumentOutOfRangeException(nameof(modelType))
    };

    /// <summary>
    /// Gets the full path to the CLIP projector file for a given model type.
    /// </summary>
    public string GetClipProjectorPath(CaptioningModelType modelType) => modelType switch
    {
        CaptioningModelType.LLaVA_v1_6_34B => Path.Combine(_modelsBasePath, LLaVaClipProjectorFileName),
        CaptioningModelType.Qwen2_5_VL_7B => Path.Combine(_modelsBasePath, Qwen25VLClipProjectorFileName),
        CaptioningModelType.Qwen3_VL_8B => Path.Combine(_modelsBasePath, Qwen3VLClipProjectorFileName),
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
        _ => "Unknown model."
    };

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
