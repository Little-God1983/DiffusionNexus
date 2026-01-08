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

    private readonly string _modelsBasePath;
    private readonly HttpClient _httpClient;
    private readonly object _downloadLock = new();
    private bool _isDownloadingRmbg14;
    private bool _isDownloadingUltraSharp4x;

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
}
