using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.CivitaiBrowser;

/// <summary>
/// Phase 2 download queue: concurrent worker pool, JSON persistence across restart,
/// SHA256 verification, and A1111-style sidecar writing
/// (<c>&lt;file&gt;.civitai.json</c> + <c>&lt;file&gt;.preview.png</c>).
/// </summary>
public sealed class CivitaiDownloadQueue : ObservableObject
{
    private const string PersistFileName = "civitai-download-queue.json";

    private readonly LoraDownloadService? _downloadService;
    private readonly IUnifiedLogger? _logger;
    private readonly ICivitaiClient? _civitaiClient;
    private SemaphoreSlim _gate = new(2);
    private int _maxConcurrency = 2;
    private CancellationTokenSource? _runCts;

    public CivitaiDownloadQueue(LoraDownloadService? downloadService)
        : this(downloadService, null, null, null)
    {
    }

    public CivitaiDownloadQueue(
        LoraDownloadService? downloadService,
        IUnifiedLogger? logger,
        ICivitaiClient? civitaiClient,
        DownloadDestinationViewModel? destination)
    {
        _downloadService = downloadService;
        _logger = logger;
        _civitaiClient = civitaiClient;
        Destination = destination ?? new DownloadDestinationViewModel();
        TryRestore();
    }

    /// <summary>
    /// Shared destination picker that drives where each job lands. Bound to the
    /// queue side panel via <c>Queue.Destination</c>.
    /// </summary>
    public DownloadDestinationViewModel Destination { get; }

    public ObservableCollection<CivitaiDownloadJob> Jobs { get; } = [];

    public int MaxConcurrency
    {
        get => _maxConcurrency;
        set
        {
            if (value < 1) value = 1;
            if (value == _maxConcurrency) return;
            _maxConcurrency = value;
            // Swap the gate; in-flight workers keep the old gate's slot.
            _gate = new SemaphoreSlim(value);
            OnPropertyChanged();
        }
    }

    public int ActiveCount => Jobs.Count(j => j.Status is JobStatus.Queued or JobStatus.Downloading);
    public int CompletedCount => Jobs.Count(j => j.Status == JobStatus.Completed);
    public int ErrorCount => Jobs.Count(j => j.Status == JobStatus.Failed);

    public CivitaiDownloadJob? Enqueue(CivitaiResultViewModel result, CivitaiVersionPickItemViewModel pick)
    {
        if (result.Model is null) return null;

        var primary = pick.Version.Files.FirstOrDefault(f => f.Primary == true) ?? pick.Version.Files.FirstOrDefault();
        var url = primary?.DownloadUrl ?? pick.Version.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url)) return null;

        var fileName = primary?.Name ?? $"{result.Name}_{pick.Version.Id}.safetensors";

        if (Jobs.Any(j => j.VersionId == pick.Version.Id))
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue",
                $"Duplicate enqueue skipped: {result.Name} ({pick.Name}) — version {pick.Version.Id} already in queue");
            return null;
        }

        var job = new CivitaiDownloadJob
        {
            ModelId = result.Model.Id,
            VersionId = pick.Version.Id,
            ModelName = result.Name,
            VersionName = pick.Name,
            BaseModel = pick.BaseModel,
            Category = result.Category,
            FileName = fileName,
            DownloadUrl = url,
            SizeDisplay = pick.SizeDisplay,
            ExpectedSha256 = primary?.Hashes?.SHA256,
            PreviewImageUrl = pick.Version.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url,
            CivitaiVersion = pick.Version
        };
        Jobs.Add(job);
        Persist();
        RaiseCountsChanged();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Enqueued: {result.Name} — {pick.Name} ({pick.BaseModel}, {pick.SizeDisplay})",
            $"VersionId: {pick.Version.Id}\nFile: {fileName}\nUrl: {url}\nExpected SHA256: {primary?.Hashes?.SHA256 ?? "(none)"}");
        return job;
    }

    public void Remove(CivitaiDownloadJob job)
    {
        Jobs.Remove(job);
        Persist();
        RaiseCountsChanged();
    }

    public void ClearCompleted()
    {
        var done = Jobs.Where(j => j.Status == JobStatus.Completed).ToList();
        foreach (var j in done) Jobs.Remove(j);
        Persist();
        RaiseCountsChanged();
    }

    /// <summary>
    /// Drops every job regardless of status without downloading. Active runs are
    /// cancelled via the shared run-CTS so workers exit promptly.
    /// </summary>
    public void ClearAll()
    {
        var removed = Jobs.Count;
        _runCts?.Cancel();
        Jobs.Clear();
        Persist();
        RaiseCountsChanged();
        if (removed > 0)
        {
            _logger?.Info(LogCategory.Download, "CivitaiQueue",
                $"Queue cleared: {removed} job(s) removed (active downloads cancelled).");
        }
    }

    /// <summary>
    /// Runs all queued jobs through the worker pool. Subsequent calls coalesce — already-
    /// running jobs aren't restarted; only Queued ones are picked up.
    /// </summary>
    public async Task StartAllAsync()
    {
        if (_downloadService is null)
        {
            _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                "StartAll aborted: LoraDownloadService unavailable. Marking queued jobs as failed.");
            foreach (var job in Jobs.Where(j => j.Status == JobStatus.Queued))
            {
                job.Status = JobStatus.Failed;
                job.StatusMessage = "Download service unavailable.";
            }
            return;
        }

        _runCts?.Cancel();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;

        var pending = Jobs.Where(j => j.Status == JobStatus.Queued).ToList();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Starting {pending.Count} download(s) (max concurrency: {_maxConcurrency})");
        var tasks = pending.Select(job => RunGatedAsync(job, ct)).ToList();
        await Task.WhenAll(tasks);
        RaiseCountsChanged();
        Persist();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Batch complete — {CompletedCount} done, {ErrorCount} failed, {ActiveCount} still active.");
    }

    private async Task RunGatedAsync(CivitaiDownloadJob job, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await RunJobAsync(job, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            RaiseCountsChanged();
            Persist();
        }
    }

    private async Task RunJobAsync(CivitaiDownloadJob job, CancellationToken ct)
    {
        // Destination resolution order:
        //   1. Per-job override (CustomTargetDirectory) — set via per-row override UI
        //   2. Shared Destination picker (BaseModel + Category sub-folders honored)
        //   3. Hard fallback: first configured source folder
        string? targetDir = job.CustomTargetDirectory;
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            targetDir = Destination.BuildTargetDirectory(job.BaseModel, job.Category);
        }
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            var settings = App.Services?.GetService<IAppSettingsService>();
            var folders = settings is null
                ? new List<string>()
                : (await settings.GetEnabledLoraSourcesAsync(ct)).ToList();
            if (folders.Count == 0)
            {
                job.Status = JobStatus.Failed;
                job.StatusMessage = "No download destination set. Configure one in the Destination panel.";
                return;
            }
            targetDir = Path.Combine(folders[0], string.IsNullOrWhiteSpace(job.BaseModel) ? "Unsorted" : job.BaseModel);
        }
        Directory.CreateDirectory(targetDir);
        var target = Path.Combine(targetDir, job.FileName);
        job.TargetPath = target;

        try
        {
            job.Status = JobStatus.Downloading;
            job.StatusMessage = "Connecting...";

            // CivitaiVersion is null after restart-restore; rehydrate from the API.
            var civVersion = job.CivitaiVersion;
            if (civVersion is null && _civitaiClient is not null)
            {
                civVersion = await _civitaiClient.GetModelVersionAsync(job.VersionId, cancellationToken: ct);
                job.CivitaiVersion = civVersion;
            }
            if (civVersion is null)
            {
                job.Status = JobStatus.Failed;
                job.StatusMessage = "Could not resolve Civitai version metadata.";
                return;
            }

            var tcs = new TaskCompletionSource<bool>();

            await _downloadService!.DownloadFileAsync(
                downloadUrl: job.DownloadUrl,
                targetPath: target,
                civitaiVersion: civVersion,
                taskName: $"Download {job.ModelName} ({job.VersionName})",
                reportProgress: (pct, msg) => Dispatcher.UIThread.Post(() =>
                {
                    job.ProgressPercent = pct * 100;
                    job.StatusMessage = msg;
                }),
                completed: () => Dispatcher.UIThread.Post(() =>
                {
                    job.ProgressPercent = 100;
                    job.StatusMessage = "Verifying...";
                    tcs.TrySetResult(true);
                }),
                failed: () => Dispatcher.UIThread.Post(() =>
                {
                    job.Status = JobStatus.Failed;
                    if (string.IsNullOrEmpty(job.StatusMessage) || job.StatusMessage == "Connecting...")
                        job.StatusMessage = "Failed";
                    tcs.TrySetResult(false);
                }));

            var ok = await tcs.Task.ConfigureAwait(false);
            if (!ok) return;

            // SHA256 verification — non-fatal: log warning, mark as completed-with-warning.
            if (!string.IsNullOrWhiteSpace(job.ExpectedSha256) && File.Exists(target))
            {
                try
                {
                    var actual = await ComputeSha256Async(target, ct);
                    if (!string.Equals(actual, job.ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        job.Status = JobStatus.Failed;
                        job.StatusMessage = $"Hash mismatch (got {actual[..8]}…, expected {job.ExpectedSha256[..8]}…)";
                        _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                            $"SHA256 mismatch for {target} — got {actual}, expected {job.ExpectedSha256}");
                        return;
                    }
                    job.ActualSha256 = actual;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(LogCategory.Download, "CivitaiQueue", $"SHA256 verification failed: {ex.Message}");
                }
            }

            // Sidecar files: <file>.civitai.json + <file>.preview.png
            try
            {
                await WriteSidecarsAsync(target, civVersion, job, ct);
            }
            catch (Exception ex)
            {
                _logger?.Warn(LogCategory.Download, "CivitaiQueue", $"Sidecar write failed: {ex.Message}");
            }

            job.Status = JobStatus.Completed;
            job.StatusMessage = "Done";
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Failed;
            job.StatusMessage = "Cancelled";
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.StatusMessage = ex.Message;
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(bytes);
    }

    private static async Task WriteSidecarsAsync(string filePath, CivitaiModelVersion version, CivitaiDownloadJob job, CancellationToken ct)
    {
        var baseName = Path.Combine(Path.GetDirectoryName(filePath)!, Path.GetFileNameWithoutExtension(filePath));

        // <file>.civitai.json — A1111-style sidecar carrying the full version DTO.
        var jsonPath = baseName + ".civitai.json";
        var payload = new
        {
            modelId = job.ModelId,
            modelName = job.ModelName,
            id = version.Id,
            name = version.Name,
            baseModel = version.BaseModel,
            trainedWords = version.TrainedWords,
            description = version.Description,
            downloadUrl = version.DownloadUrl,
            files = version.Files.Select(f => new
            {
                name = f.Name,
                sizeKB = f.SizeKB,
                hashes = new
                {
                    AutoV2 = f.Hashes?.AutoV2,
                    SHA256 = f.Hashes?.SHA256,
                    CRC32 = f.Hashes?.CRC32,
                    BLAKE3 = f.Hashes?.BLAKE3
                }
            }),
            images = version.Images.Select(i => new { url = i.Url, width = i.Width, height = i.Height, nsfw = i.Nsfw })
        };
        await File.WriteAllTextAsync(jsonPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        // <file>.preview.png — first example image.
        if (!string.IsNullOrWhiteSpace(job.PreviewImageUrl))
        {
            var pngPath = baseName + ".preview.png";
            if (!File.Exists(pngPath))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var data = await http.GetByteArrayAsync(job.PreviewImageUrl, ct);
                if (data.Length > 0)
                {
                    await File.WriteAllBytesAsync(pngPath, data, ct);
                }
            }
        }
    }

    #region Persistence

    private static string GetPersistPath()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiffusionNexus");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, PersistFileName);
    }

    private void Persist()
    {
        try
        {
            var path = GetPersistPath();
            // Don't persist transient transitions ("Verifying...") — only stable states.
            var snapshot = Jobs.Select(j => new PersistedJob
            {
                ModelId = j.ModelId,
                VersionId = j.VersionId,
                ModelName = j.ModelName,
                VersionName = j.VersionName,
                BaseModel = j.BaseModel,
                Category = j.Category,
                FileName = j.FileName,
                DownloadUrl = j.DownloadUrl,
                SizeDisplay = j.SizeDisplay,
                ExpectedSha256 = j.ExpectedSha256,
                ActualSha256 = j.ActualSha256,
                PreviewImageUrl = j.PreviewImageUrl,
                CustomTargetDirectory = j.CustomTargetDirectory,
                TargetPath = j.TargetPath,
                Status = j.Status == JobStatus.Downloading ? JobStatus.Queued : j.Status
            }).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue", $"Persist failed: {ex.Message}");
        }
    }

    private void TryRestore()
    {
        try
        {
            var path = GetPersistPath();
            if (!File.Exists(path)) return;
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return;
            var snapshot = JsonSerializer.Deserialize<List<PersistedJob>>(json);
            if (snapshot is null) return;
            foreach (var p in snapshot)
            {
                Jobs.Add(new CivitaiDownloadJob
                {
                    ModelId = p.ModelId,
                    VersionId = p.VersionId,
                    ModelName = p.ModelName,
                    VersionName = p.VersionName,
                    BaseModel = p.BaseModel,
                    Category = p.Category,
                    FileName = p.FileName,
                    DownloadUrl = p.DownloadUrl,
                    SizeDisplay = p.SizeDisplay,
                    ExpectedSha256 = p.ExpectedSha256,
                    ActualSha256 = p.ActualSha256,
                    PreviewImageUrl = p.PreviewImageUrl,
                    CustomTargetDirectory = p.CustomTargetDirectory,
                    TargetPath = p.TargetPath,
                    Status = p.Status,
                    CivitaiVersion = null // Rehydrated lazily on resume.
                });
            }
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue", $"Restore failed: {ex.Message}");
        }
    }

    private sealed class PersistedJob
    {
        public int ModelId { get; set; }
        public int VersionId { get; set; }
        public string ModelName { get; set; } = string.Empty;
        public string VersionName { get; set; } = string.Empty;
        public string BaseModel { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string SizeDisplay { get; set; } = string.Empty;
        public string? ExpectedSha256 { get; set; }
        public string? ActualSha256 { get; set; }
        public string? PreviewImageUrl { get; set; }
        public string? CustomTargetDirectory { get; set; }
        public string? TargetPath { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public JobStatus Status { get; set; }
    }

    #endregion

    private void RaiseCountsChanged()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(ErrorCount));
    }
}

public enum JobStatus
{
    Queued,
    Downloading,
    Completed,
    Failed
}

public partial class CivitaiDownloadJob : ObservableObject
{
    public int ModelId { get; init; }
    public int VersionId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string BaseModel { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string SizeDisplay { get; init; } = string.Empty;
    public string? ExpectedSha256 { get; init; }
    public string? PreviewImageUrl { get; init; }
    public string? ActualSha256 { get; set; }
    public string? CustomTargetDirectory { get; set; }
    public string? TargetPath { get; set; }

    [JsonIgnore]
    public CivitaiModelVersion? CivitaiVersion { get; set; }

    [ObservableProperty]
    private JobStatus _status = JobStatus.Queued;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string? _statusMessage;
}
