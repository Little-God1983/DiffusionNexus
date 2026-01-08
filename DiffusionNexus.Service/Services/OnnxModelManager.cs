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
    private const string Rmbg14ModelFileName = "rmbg-1.4.onnx";
    private const string Rmbg14ModelUrl = "https://huggingface.co/briaai/RMBG-1.4/resolve/main/onnx/model.onnx";
    private const long ExpectedModelSizeBytes = 176_000_000; // ~176MB

    private readonly string _modelsBasePath;
    private readonly HttpClient _httpClient;
    private readonly object _downloadLock = new();
    private bool _isDownloading;

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
    /// Gets the status of the RMBG-1.4 model.
    /// </summary>
    public ModelStatus GetRmbg14Status()
    {
        lock (_downloadLock)
        {
            if (_isDownloading)
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
                ExpectedModelSizeBytes,
                ExpectedModelSizeBytes,
                "Model already downloaded"));
            return true;
        }

        lock (_downloadLock)
        {
            if (_isDownloading)
            {
                Log.Warning("RMBG-1.4 model download already in progress");
                return false;
            }
            _isDownloading = true;
        }

        try
        {
            progress?.Report(new ModelDownloadProgress(0, ExpectedModelSizeBytes, "Starting download..."));

            var tempPath = Rmbg14ModelPath + ".download";

            using var response = await _httpClient.GetAsync(
                Rmbg14ModelUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? ExpectedModelSizeBytes;

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
            if (File.Exists(Rmbg14ModelPath))
                File.Delete(Rmbg14ModelPath);

            File.Move(tempPath, Rmbg14ModelPath);

            progress?.Report(new ModelDownloadProgress(
                bytesRead,
                totalBytes,
                "Download complete"));

            Log.Information("RMBG-1.4 model downloaded successfully: {Path}", Rmbg14ModelPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            progress?.Report(new ModelDownloadProgress(0, ExpectedModelSizeBytes, "Download cancelled"));
            CleanupPartialDownload();
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download RMBG-1.4 model");
            progress?.Report(new ModelDownloadProgress(0, ExpectedModelSizeBytes, $"Download failed: {ex.Message}"));
            CleanupPartialDownload();
            return false;
        }
        finally
        {
            lock (_downloadLock)
            {
                _isDownloading = false;
            }
        }
    }

    private void CleanupPartialDownload()
    {
        var tempPath = Rmbg14ModelPath + ".download";
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
}
