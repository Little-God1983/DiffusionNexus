using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Manages ONNX model files for AI inference services.
/// Handles model downloading, path management, and status checking.
/// </summary>
public sealed class OnnxModelManager
{
    private const string ModelDirectory = "Models";
    
    // RMBG-1.4 Background Removal Model
    private const string Rmbg14ModelFileName = "rmbg-1.4.onnx";
    private const string Rmbg14ModelUrl = "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx";
    private const long ExpectedRmbg14SizeBytes = 176_000_000; // ~176MB
    
    // 4x-UltraSharp Upscaling Model
    private const string UltraSharp4xModelFileName = "4x-UltraSharp.onnx";
    private const string UltraSharp4xModelUrl = "https://huggingface.co/ofter/4x-UltraSharp/resolve/main/4x-UltraSharp.onnx";
    private const long ExpectedUltraSharp4xSizeBytes = 67_000_000; // ~67MB

    // WD14 ViT Tagger v3 (booru-style image tags + content rating, one ONNX pass)
    private const string Wd14TaggerModelFileName = "wd-vit-tagger-v3.onnx";
    private const string Wd14TaggerModelUrl = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main/model.onnx";
    private const long ExpectedWd14TaggerSizeBytes = 379_000_000; // ~379MB

    private const string Wd14TaggerTagsFileName = "wd-vit-tagger-v3-tags.csv";
    private const string Wd14TaggerTagsUrl = "https://huggingface.co/SmilingWolf/wd-vit-tagger-v3/resolve/main/selected_tags.csv";
    private const long ExpectedWd14TaggerTagsSizeBytes = 308_000; // ~308KB

    private readonly string _modelsBasePath;
    private readonly HttpClient _httpClient;
    private readonly object _downloadLock = new();
    private bool _isDownloadingRmbg14;
    private bool _isDownloadingUltraSharp4x;
    private bool _isDownloadingWd14Tagger;

    /// <summary>
    /// Creates a new OnnxModelManager with the default models directory.
    /// </summary>
    public OnnxModelManager() : this(null, null) { }

    /// <summary>
    /// Creates a new OnnxModelManager.
    /// </summary>
    /// <param name="modelsBasePath">Optional custom path for model storage. Uses %LocalAppData%/DiffusionNexus/Models by default.</param>
    /// <param name="httpClient">Optional HttpClient for downloads. Creates a new one if not provided.</param>
    public OnnxModelManager(string? modelsBasePath, HttpClient? httpClient)
    {
        _modelsBasePath = modelsBasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus",
            ModelDirectory);

        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30); // Large file download timeout

        Directory.CreateDirectory(_modelsBasePath);
    }

    /// <summary>
    /// Gets the full path to the RMBG-1.4 model file.
    /// </summary>
    public string Rmbg14ModelPath => Path.Combine(_modelsBasePath, Rmbg14ModelFileName);

    /// <summary>
    /// Gets the full path to the 4x-UltraSharp model file.
    /// </summary>
    public string UltraSharp4xModelPath => Path.Combine(_modelsBasePath, UltraSharp4xModelFileName);

    /// <summary>Gets the full path to the WD14 tagger ONNX model file.</summary>
    public string Wd14TaggerModelPath => Path.Combine(_modelsBasePath, Wd14TaggerModelFileName);

    /// <summary>Gets the full path to the WD14 tagger's tag list CSV.</summary>
    public string Wd14TaggerTagsPath => Path.Combine(_modelsBasePath, Wd14TaggerTagsFileName);

    /// <summary>
    /// Gets the status of the RMBG-1.4 model.
    /// </summary>
    public ModelStatus GetRmbg14Status()
    {
        lock (_downloadLock)
        {
            if (_isDownloadingRmbg14)
                return ModelStatus.Downloading;
        }

        if (!File.Exists(Rmbg14ModelPath))
            return ModelStatus.NotDownloaded;

        var fileInfo = new FileInfo(Rmbg14ModelPath);
        
        // Basic size check - model should be at least 150MB
        if (fileInfo.Length < 150_000_000)
            return ModelStatus.Corrupted;

        return ModelStatus.Ready;
    }

    /// <summary>
    /// Gets the status of the 4x-UltraSharp model.
    /// </summary>
    public ModelStatus GetUltraSharp4xStatus()
    {
        lock (_downloadLock)
        {
            if (_isDownloadingUltraSharp4x)
                return ModelStatus.Downloading;
        }

        if (!File.Exists(UltraSharp4xModelPath))
            return ModelStatus.NotDownloaded;

        var fileInfo = new FileInfo(UltraSharp4xModelPath);
        
        // Basic size check - model should be at least 60MB
        if (fileInfo.Length < 60_000_000)
            return ModelStatus.Corrupted;

        return ModelStatus.Ready;
    }

    /// <summary>
    /// Checks if the WD14 tagger model file exists and is correctly sized.
    /// </summary>
    private bool IsWd14ModelFileValid()
    {
        if (!File.Exists(Wd14TaggerModelPath))
            return false;
        return new FileInfo(Wd14TaggerModelPath).Length >= 300_000_000;
    }

    /// <summary>
    /// Checks if the WD14 tagger tags file exists and is correctly sized.
    /// </summary>
    private bool IsWd14TagsFileValid()
    {
        if (!File.Exists(Wd14TaggerTagsPath))
            return false;
        return new FileInfo(Wd14TaggerTagsPath).Length >= 100_000;
    }

    /// <summary>
    /// Gets the status of the WD14 tagger. Both the model and its tag list
    /// must be present and correctly sized — the tagger is unusable without
    /// its CSV, so a missing/corrupt CSV counts the whole entry as not ready.
    /// </summary>
    public ModelStatus GetWd14TaggerStatus()
    {
        lock (_downloadLock)
        {
            if (_isDownloadingWd14Tagger)
                return ModelStatus.Downloading;
        }

        if (!File.Exists(Wd14TaggerModelPath) || !File.Exists(Wd14TaggerTagsPath))
            return ModelStatus.NotDownloaded;

        if (!IsWd14ModelFileValid() || !IsWd14TagsFileValid())
            return ModelStatus.Corrupted;

        return ModelStatus.Ready;
    }

    /// <summary>
    /// Downloads the RMBG-1.4 model from HuggingFace.
    /// </summary>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded or model already exists.</returns>
    public async Task<bool> DownloadRmbg14ModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetRmbg14Status();
        if (status == ModelStatus.Ready)
        {
            progress?.Report(new ModelDownloadProgress(
                ExpectedRmbg14SizeBytes,
                ExpectedRmbg14SizeBytes,
                "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_isDownloadingRmbg14)
            {
                Log.Warning("RMBG-1.4 model download already in progress");
                return false;
            }
            _isDownloadingRmbg14 = true;
        }

        try
        {
            return await DownloadModelInternalAsync(
                Rmbg14ModelUrl,
                Rmbg14ModelPath,
                ExpectedRmbg14SizeBytes,
                "RMBG-1.4",
                progress,
                cancellationToken);
        }
        finally
        {
            lock (_downloadLock)
            {
                _isDownloadingRmbg14 = false;
            }
        }
    }

    /// <summary>
    /// Downloads the 4x-UltraSharp model from HuggingFace.
    /// </summary>
    /// <param name="progress">Optional progress callback.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if download succeeded or model already exists.</returns>
    public async Task<bool> DownloadUltraSharp4xModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetUltraSharp4xStatus();
        if (status == ModelStatus.Ready)
        {
            progress?.Report(new ModelDownloadProgress(
                ExpectedUltraSharp4xSizeBytes,
                ExpectedUltraSharp4xSizeBytes,
                "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_isDownloadingUltraSharp4x)
            {
                Log.Warning("4x-UltraSharp model download already in progress");
                return false;
            }
            _isDownloadingUltraSharp4x = true;
        }

        try
        {
            return await DownloadModelInternalAsync(
                UltraSharp4xModelUrl,
                UltraSharp4xModelPath,
                ExpectedUltraSharp4xSizeBytes,
                "4x-UltraSharp",
                progress,
                cancellationToken);
        }
        finally
        {
            lock (_downloadLock)
            {
                _isDownloadingUltraSharp4x = false;
            }
        }
    }

    /// <summary>
    /// Downloads the WD14 ViT Tagger v3 model and its tag list from HuggingFace.
    /// Two files, downloaded sequentially; both must succeed and pass size validation for the entry to be Ready.
    /// Only downloads missing or corrupt files — if the model is already valid, skips re-downloading it.
    /// </summary>
    public async Task<bool> DownloadWd14TaggerModelAsync(
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var status = GetWd14TaggerStatus();
        if (status == ModelStatus.Ready)
        {
            progress?.Report(new ModelDownloadProgress(
                ExpectedWd14TaggerSizeBytes, ExpectedWd14TaggerSizeBytes, "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_isDownloadingWd14Tagger)
            {
                Log.Warning("WD14 tagger model download already in progress");
                return false;
            }
            _isDownloadingWd14Tagger = true;
        }

        try
        {
            // Wrap progress to suppress "Download complete" until both files are done
            var wrappedProgress = new Wd14ProgressWrapper(progress);

            // Check model file separately — if it's already valid, skip re-downloading
            var modelNeedsDownload = !IsWd14ModelFileValid();

            if (modelNeedsDownload)
            {
                var modelOk = await DownloadModelInternalAsync(
                    Wd14TaggerModelUrl, Wd14TaggerModelPath, ExpectedWd14TaggerSizeBytes,
                    "WD14 Tagger", wrappedProgress, cancellationToken);

                if (!modelOk)
                    return false;
            }

            // Check tags file separately — if it's already valid, skip re-downloading
            var tagsNeedsDownload = !IsWd14TagsFileValid();

            if (tagsNeedsDownload)
            {
                wrappedProgress.SetFinalFile(); // Allow "Download complete" for the final file
                var tagsOk = await DownloadModelInternalAsync(
                    Wd14TaggerTagsUrl, Wd14TaggerTagsPath, ExpectedWd14TaggerTagsSizeBytes,
                    "WD14 Tagger tag list", wrappedProgress, cancellationToken);

                if (!tagsOk)
                    return false;
            }

            // Validate both files are correctly sized before returning success
            if (!IsWd14ModelFileValid() || !IsWd14TagsFileValid())
            {
                Log.Error("WD14 tagger download reported success but files are corrupted or undersized");
                return false;
            }

            return true;
        }
        finally
        {
            lock (_downloadLock)
            {
                _isDownloadingWd14Tagger = false;
            }
        }
    }

    /// <summary>
    /// Wrapper around IProgress to weight combined progress across two files and suppress "Download complete" until final.
    /// Maps the two sequential 0-100% downloads into a single continuous 0-100% progress report.
    /// </summary>
    private sealed class Wd14ProgressWrapper : IProgress<ModelDownloadProgress>
    {
        private readonly IProgress<ModelDownloadProgress>? _inner;
        private bool _isFinalFile;

        public Wd14ProgressWrapper(IProgress<ModelDownloadProgress>? inner) => _inner = inner;

        public void SetFinalFile() => _isFinalFile = true;

        public void Report(ModelDownloadProgress value)
        {
            // Weight progress: first file progress contributes to first ~99.9% (model is ~379MB, tags are ~308KB)
            // Second file completes the remaining ~0.1%
            var combinedTotal = ExpectedWd14TaggerSizeBytes + ExpectedWd14TaggerTagsSizeBytes;

            long adjustedBytes;

            if (!_isFinalFile)
            {
                // First file: map its 0-100% to 0-~99.9% of the combined total
                // Progress = (downloaded / total) * modelSize, within the combined scale
                adjustedBytes = (value.BytesDownloaded * ExpectedWd14TaggerSizeBytes) / value.TotalBytes;
            }
            else
            {
                // Second file: starts after model, so offset + scale tags progress
                // Progress = modelSize + (downloaded / total) * tagsSize
                adjustedBytes = ExpectedWd14TaggerSizeBytes +
                               (value.BytesDownloaded * ExpectedWd14TaggerTagsSizeBytes) / value.TotalBytes;
            }

            // Suppress "Download complete" status until the final file
            var status = value.Status;
            if (!_isFinalFile && value.Status == "Download complete")
            {
                // Continue reporting progress without "complete" message
                status = $"Downloading... {adjustedBytes / 1024 / 1024}MB / {combinedTotal / 1024 / 1024}MB";
            }

            _inner?.Report(new ModelDownloadProgress(adjustedBytes, combinedTotal, status));
        }
    }

    /// <summary>
    /// Internal method to download a model file with progress reporting.
    /// </summary>
    private async Task<bool> DownloadModelInternalAsync(
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

                // Throttle progress updates to avoid UI thread flooding
                if ((DateTime.UtcNow - lastProgressUpdate).TotalMilliseconds > 100)
                {
                    progress?.Report(new ModelDownloadProgress(
                        bytesRead,
                        totalBytes,
                        $"Downloading... {bytesRead / 1024 / 1024}MB / {totalBytes / 1024 / 1024}MB"));
                    lastProgressUpdate = DateTime.UtcNow;
                }
            }

            await fileStream.FlushAsync(cancellationToken);
            fileStream.Close();

            // Rename temp file to final name
            if (File.Exists(destinationPath))
                File.Delete(destinationPath);

            File.Move(tempPath, destinationPath);

            progress?.Report(new ModelDownloadProgress(
                bytesRead,
                totalBytes,
                "Download complete"));

            Log.Information("{ModelName} model downloaded successfully: {Path}", modelName, destinationPath);
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
            Log.Error(ex, "Failed to download {ModelName} model", modelName);
            progress?.Report(new ModelDownloadProgress(0, expectedSize, $"Download failed: {ex.Message}"));
            CleanupPartialDownload(destinationPath);
            return false;
        }
    }

    private static void CleanupPartialDownload(string modelPath)
    {
        var tempPath = modelPath + ".download";
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
    /// Deletes the RMBG-1.4 model file if it exists.
    /// </summary>
    public void DeleteRmbg14Model()
    {
        try
        {
            if (File.Exists(Rmbg14ModelPath))
            {
                File.Delete(Rmbg14ModelPath);
                Log.Information("RMBG-1.4 model deleted: {Path}", Rmbg14ModelPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete RMBG-1.4 model: {Path}", Rmbg14ModelPath);
            throw;
        }
    }

    /// <summary>
    /// Deletes the 4x-UltraSharp model file if it exists.
    /// </summary>
    public void DeleteUltraSharp4xModel()
    {
        try
        {
            if (File.Exists(UltraSharp4xModelPath))
            {
                File.Delete(UltraSharp4xModelPath);
                Log.Information("4x-UltraSharp model deleted: {Path}", UltraSharp4xModelPath);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete 4x-UltraSharp model: {Path}", UltraSharp4xModelPath);
            throw;
        }
    }

    /// <summary>Deletes both WD14 tagger files if they exist.</summary>
    public void DeleteWd14TaggerModel()
    {
        try
        {
            if (File.Exists(Wd14TaggerModelPath))
                File.Delete(Wd14TaggerModelPath);
            if (File.Exists(Wd14TaggerTagsPath))
                File.Delete(Wd14TaggerTagsPath);
            Log.Information("WD14 tagger model deleted: {ModelPath}, {TagsPath}", Wd14TaggerModelPath, Wd14TaggerTagsPath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to delete WD14 tagger model: {ModelPath}, {TagsPath}", Wd14TaggerModelPath, Wd14TaggerTagsPath);
            throw;
        }
    }
}
