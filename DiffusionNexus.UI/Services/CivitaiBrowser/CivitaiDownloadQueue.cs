using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Lora;
using DiffusionNexus.UI.Services.Download;
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

    /// <summary>
    /// Overrides where the queue snapshot is persisted/restored. Tests use this so
    /// they never read or clobber the real LocalAppData queue file.
    /// </summary>
    private readonly string? _persistPathOverride;

    private readonly ICivitaiModelDownloader? _downloader;
    private readonly IUnifiedLogger? _logger;
    private readonly ICivitaiClient? _civitaiClient;
    private SemaphoreSlim _gate = new(2);
    private int _maxConcurrency = 2;
    private CancellationTokenSource? _runCts;

    public CivitaiDownloadQueue(ICivitaiModelDownloader? downloader)
        : this(downloader, null, null, null)
    {
    }

    public CivitaiDownloadQueue(
        ICivitaiModelDownloader? downloader,
        IUnifiedLogger? logger,
        ICivitaiClient? civitaiClient,
        DownloadDestinationViewModel? destination,
        string? persistPathOverride = null)
    {
        _persistPathOverride = persistPathOverride;
        _downloader = downloader;
        _logger = logger;
        _civitaiClient = civitaiClient;
        Destination = destination ?? new DownloadDestinationViewModel();
        // Suppress the preview path inside the picker — each queued job may resolve
        // to a different folder, so the per-job expected path is rendered on the tile.
        Destination.ShowPreviewPath = false;
        Destination.PropertyChanged += (_, _) =>
        {
            RefreshExpectedTargets();
            RecomputeSpaceWarning();
        };
        Jobs.CollectionChanged += OnJobsChanged;
        TryRestore();
        foreach (var j in Jobs) HookJob(j);
        RecomputeSpaceWarning();
    }

    private void OnJobsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (CivitaiDownloadJob j in e.NewItems) HookJob(j);
        if (e.OldItems is not null)
            foreach (CivitaiDownloadJob j in e.OldItems) UnhookJob(j);
        RecomputeSpaceWarning();
        OnPropertyChanged(nameof(TotalQueuedBytes));
        OnPropertyChanged(nameof(TotalQueuedBytesDisplay));
    }

    private void HookJob(CivitaiDownloadJob j) => j.PropertyChanged += OnJobPropertyChanged;
    private void UnhookJob(CivitaiDownloadJob j) => j.PropertyChanged -= OnJobPropertyChanged;

    private void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Recompute when anything that affects per-drive totals changes.
        if (e.PropertyName is nameof(CivitaiDownloadJob.Status)
                          or nameof(CivitaiDownloadJob.ExpectedTargetDir)
                          or nameof(CivitaiDownloadJob.CustomTargetDirectory))
        {
            RecomputeSpaceWarning();
            OnPropertyChanged(nameof(TotalQueuedBytes));
            OnPropertyChanged(nameof(TotalQueuedBytesDisplay));
        }
    }

    /// <summary>Sum of bytes for jobs still queued (excludes downloading/done/failed).</summary>
    public long TotalQueuedBytes => Jobs.Where(j => j.Status == JobStatus.Queued).Sum(j => j.SizeBytes);

    public string TotalQueuedBytesDisplay => FormatBytes(TotalQueuedBytes);

    private string? _spaceWarning;
    public string? SpaceWarning
    {
        get => _spaceWarning;
        private set
        {
            if (SetProperty(ref _spaceWarning, value))
            {
                OnPropertyChanged(nameof(HasSpaceWarning));
            }
        }
    }

    public bool HasSpaceWarning => !string.IsNullOrEmpty(SpaceWarning);

    /// <summary>
    /// Groups queued jobs by the drive root of their resolved target directory and
    /// flags any drive where the required bytes exceed the live <c>AvailableFreeSpace</c>.
    /// Per-drive reporting handles the case where a per-job override sends some
    /// downloads to a different drive than the global destination.
    /// </summary>
    private void RecomputeSpaceWarning()
    {
        try
        {
            var queued = Jobs
                .Where(j => j.Status == JobStatus.Queued && j.SizeBytes > 0)
                .ToList();
            if (queued.Count == 0)
            {
                SpaceWarning = null;
                return;
            }

            var groups = queued
                .Select(j => new
                {
                    Job = j,
                    Root = SafeGetPathRoot(j.CustomTargetDirectory ?? j.ExpectedTargetDir)
                })
                .Where(x => !string.IsNullOrEmpty(x.Root))
                .GroupBy(x => x.Root!, StringComparer.OrdinalIgnoreCase);

            var lines = new List<string>();
            foreach (var g in groups)
            {
                var needed = g.Sum(x => x.Job.SizeBytes);
                long? available = null;
                try
                {
                    var drive = new DriveInfo(g.Key);
                    if (drive.IsReady) available = drive.AvailableFreeSpace;
                }
                catch { /* not a real drive */ }

                if (available is long avail && needed > avail)
                {
                    lines.Add($"Need {FormatBytes(needed)} on {g.Key} — only {FormatBytes(avail)} free");
                }
            }

            SpaceWarning = lines.Count > 0 ? string.Join("\n", lines) : null;
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue", $"Disk-space check failed: {ex.Message}");
            SpaceWarning = null;
        }
    }

    private static string? SafeGetPathRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetPathRoot(path); }
        catch { return null; }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KB";
        return $"{bytes} B";
    }

    /// <summary>
    /// Recomputes each queued job's expected target directory whenever the destination
    /// settings change (e.g. the user picked a different source folder).
    /// </summary>
    private void RefreshExpectedTargets()
    {
        foreach (var job in Jobs)
        {
            // Don't overwrite jobs that have already started or completed — their
            // TargetPath is the actual on-disk location.
            if (job.Status is JobStatus.Downloading or JobStatus.Completed) continue;
            job.ExpectedTargetDir = job.CustomTargetDirectory
                ?? Destination.BuildTargetDirectory(job.BaseModel, job.Category);
        }
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

        var primary = CivitaiVersionFiles.PickPrimary(pick.Version);
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
            SizeBytes = pick.SizeBytes,
            IsEarlyAccess = pick.IsEarlyAccess,
            ExpectedSha256 = primary?.Hashes?.SHA256,
            PreviewImageUrl = pick.Version.Images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.Url))?.Url,
            CivitaiVersion = pick.Version
        };
        job.ExpectedTargetDir = Destination.BuildTargetDirectory(job.BaseModel, job.Category);
        Jobs.Add(job);
        Persist();
        RaiseCountsChanged();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Enqueued: {result.Name} — {pick.Name} ({pick.BaseModel}, {pick.SizeDisplay}){(pick.IsEarlyAccess ? " [EA]" : "")}",
            $"VersionId: {pick.Version.Id}\nFile: {fileName}\nUrl: {url}\nExpected SHA256: {primary?.Hashes?.SHA256 ?? "(none)"}\nEarly Access: {pick.IsEarlyAccess} (deadline={pick.Version.EarlyAccessDeadline?.ToString("u") ?? "(none)"}, paidAccess permanent={pick.Version.PaidAccess?.Permanent?.ToString() ?? "(null)"} endsAt={pick.Version.PaidAccess?.EndsAt?.ToString("u") ?? "(null)"}, availability={pick.Version.Availability ?? "(null)"})");
        return job;
    }

    /// <summary>
    /// Enqueues a waitlist entry whose early-access gate has lapsed. Prefers file
    /// metadata from <paramref name="freshVersion"/> (the just-re-verified API
    /// response) and falls back to the data captured at waitlist-add time —
    /// <paramref name="freshVersion"/> is null only in headless/no-client runs.
    /// Returns null when the version is already queued (same dedup as Enqueue).
    /// </summary>
    public CivitaiDownloadJob? EnqueueFromWaitlist(CivitaiWaitlistEntry entry, CivitaiModelVersion? freshVersion)
    {
        if (Jobs.Any(j => j.VersionId == entry.VersionId))
        {
            _logger?.Debug(LogCategory.Download, "CivitaiQueue",
                $"Waitlist move skipped duplicate: {entry.ModelName} ({entry.VersionName}) — version {entry.VersionId} already in queue");
            return null;
        }

        var primary = CivitaiVersionFiles.PickPrimary(freshVersion);
        var job = new CivitaiDownloadJob
        {
            ModelId = entry.ModelId,
            VersionId = entry.VersionId,
            ModelName = entry.ModelName,
            VersionName = entry.VersionName,
            BaseModel = entry.BaseModel,
            Category = entry.Category,
            FileName = primary?.Name ?? entry.FileName,
            DownloadUrl = primary?.DownloadUrl ?? freshVersion?.DownloadUrl ?? entry.DownloadUrl,
            SizeDisplay = entry.SizeDisplay,
            SizeBytes = entry.SizeBytes,
            IsEarlyAccess = false, // verified free, or trusted local countdown (no client to verify with)
            ExpectedSha256 = primary?.Hashes?.SHA256 ?? entry.ExpectedSha256,
            PreviewImageUrl = entry.PreviewImageUrl,
            CivitaiVersion = freshVersion
        };
        job.ExpectedTargetDir = Destination.BuildTargetDirectory(job.BaseModel, job.Category);
        Jobs.Add(job);
        Persist();
        RaiseCountsChanged();
        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Enqueued from waitlist: {entry.ModelName} — {entry.VersionName} ({entry.BaseModel}, {entry.SizeDisplay})",
            $"VersionId: {entry.VersionId}\nFile: {job.FileName}\nUrl: {job.DownloadUrl}");
        return job;
    }

    public void Remove(CivitaiDownloadJob job)
    {
        // If the tile is removed mid-download, cancel the in-flight transfer first
        // so we don't keep streaming bytes for a job no one is watching.
        if (job.Status == JobStatus.Downloading)
        {
            job.CancelByUser();
        }
        Jobs.Remove(job);
        Persist();
        RaiseCountsChanged();
    }

    /// <summary>
    /// Cancels a single in-flight download. The job's HTTP read aborts within one
    /// buffer fill and its <see cref="CivitaiDownloadJob.Status"/> moves to <c>Cancelled</c>.
    /// </summary>
    public void CancelJob(CivitaiDownloadJob job)
    {
        if (job.Status != JobStatus.Downloading) return;
        job.CancelByUser();
    }

    /// <summary>
    /// Re-queues a single failed/cancelled job and runs it through the worker pool.
    /// Doesn't disturb other queued jobs the user hasn't started yet.
    /// </summary>
    public async Task RetryJobAsync(CivitaiDownloadJob job)
    {
        if (job.Status is JobStatus.Downloading or JobStatus.Completed) return;
        if (_downloader is null) return;

        job.ResetForRetry();
        Persist();
        RaiseCountsChanged();

        _runCts ??= new CancellationTokenSource();
        var ct = _runCts.Token;

        _logger?.Info(LogCategory.Download, "CivitaiQueue",
            $"Retrying: {job.ModelName} — {job.VersionName}");
        await RunGatedAsync(job, ct);
        RaiseCountsChanged();
        Persist();
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
    /// Aborts every currently-downloading job without removing anything from the
    /// queue. In-flight HTTP transfers abort within one buffer fill; the jobs land
    /// at <c>Cancelled</c> status and can be re-run via the per-tile Retry button
    /// or by hitting Start again.
    /// </summary>
    public void AbortAllActive()
    {
        var stoppedCount = 0;

        // Mark downloading jobs so the worker's catch block knows this was a
        // user cancel (status -> Cancelled, not Failed).
        foreach (var job in Jobs.Where(j => j.Status == JobStatus.Downloading))
        {
            job.CancelByUser();
            stoppedCount++;
        }

        // Cancel the run-wide CTS too so any queued jobs still waiting on a
        // semaphore slot exit cleanly. They keep their Queued status (the worker
        // never started touching them) and can be resumed via Start.
        _runCts?.Cancel();
        _runCts = null;

        if (stoppedCount > 0)
        {
            _logger?.Info(LogCategory.Download, "CivitaiQueue",
                $"Abort: stopped {stoppedCount} active download(s); queue preserved.");
        }
        Persist();
        RaiseCountsChanged();
    }

    /// <summary>
    /// Runs all queued jobs through the worker pool. Subsequent calls coalesce — already-
    /// running jobs aren't restarted; only Queued ones are picked up.
    /// </summary>
    public async Task StartAllAsync()
    {
        if (_downloader is null)
        {
            _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                "StartAll aborted: ICivitaiModelDownloader unavailable. Marking queued jobs as failed.");
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
        var failedCount = ErrorCount;
        var summary = $"Batch complete — {CompletedCount} done, {failedCount} failed, {ActiveCount} still active.";
        // A batch with failures must not read as a routine Info line in the
        // Unified Console — the per-job errors are elsewhere in the scrollback.
        if (failedCount > 0)
            _logger?.Warn(LogCategory.Download, "CivitaiQueue", summary);
        else
            _logger?.Info(LogCategory.Download, "CivitaiQueue", summary);
    }

    private async Task RunGatedAsync(CivitaiDownloadJob job, CancellationToken runCt)
    {
        // Link the run-wide cancel (queue Start cycle / Clear all) with the per-job
        // cancel (user clicked Cancel on this tile). Either firing aborts only this job.
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(runCt, job.CancellationToken);
        var ct = linkedCts.Token;

        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled while waiting for a slot. Don't release the gate (we never took it).
            if (job.WasCancelledByUser)
            {
                job.Status = JobStatus.Cancelled;
                job.StatusMessage = "Cancelled";
            }
            return;
        }

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
            // The last hand-rolled path build in the download stack, now routed through the one
            // builder the sorter uses: job.BaseModel arrives straight from Civitai, so it needs
            // sanitising (an invalid filename character used to throw out of the directory create
            // below) and the same Unknown\ bucket the sorter would pick — otherwise the sorter
            // spends every run moving these files out of a folder it never agrees with.
            targetDir = LoraPathBuilder.BuildTargetDirectory(
                folders[0], job.BaseModel, null, includeBaseModel: true, includeCategory: false);
        }

        // No Directory.CreateDirectory here: it sat outside the try below, so an unwritable or
        // disconnected destination threw straight through RunGatedAsync (finally-only) into
        // Task.WhenAll — skipping the counts, the persist and the batch summary, and stranding
        // every remaining job as Queued. The downloader's step 3 creates the same directory and
        // turns those exact exceptions into a Failed outcome for this job alone.

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

            // The one Civitai download path (spec §4.4 / D3): collision policy, coordinator
            // enqueue, SHA256 verification, persistence and the Tags+Thumbnails completion
            // sync all live inside the downloader now — the queue only reports outcomes.
            // File is passed explicitly (rather than left for the downloader to re-pick) so
            // the enqueue-time ExpectedSha256/FileName snapshot on the job and the file the
            // downloader actually fetches agree by construction — after a restart-rehydration
            // the two picks could otherwise diverge if Civitai's file list changed in between.
            var primaryFile = CivitaiVersionFiles.PickPrimary(civVersion);
            var request = new DownloadRequest(civVersion!, targetDir, DownloadTrigger.BrowseQueue)
            {
                File = primaryFile,
                FileNameOverride = job.FileName,
                TaskName = $"Download {job.ModelName} ({job.VersionName})",
            };
            // One hop to the dispatcher, not two. Progress<T> would post the handler to its
            // captured SynchronizationContext first and only reach the dispatcher from there, so a
            // report issued just before the download returns could be enqueued AFTER the terminal
            // post below and leave the tile frozen at 99%. See UiThreadProgress.
            var progressAdapter = new UiThreadProgress<DownloadProgress>(p =>
            {
                // Belt to the ordering brace: a report that somehow arrives after the job reached a
                // terminal state must not repaint it as "Downloading".
                if (job.Status != JobStatus.Downloading) return;
                job.ProgressPercent = p.Percent;
                job.StatusMessage = p.Message;
            });
            var outcome = await _downloader!.DownloadAsync(request, progressAdapter, ct).ConfigureAwait(false);

            if (outcome.RenamedForCollision)
            {
                _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                    $"File name collision in {targetDir}: '{job.FileName}' already belongs to a different download — saved as '{Path.GetFileName(outcome.FinalPath)}' instead.");
            }

            // DownloadAsync is awaited with ConfigureAwait(false), so everything from here runs on a
            // thread-pool thread — and TargetPath/Status/StatusMessage/ProgressPercent are all
            // [ObservableProperty] behind live Avalonia bindings. Every one of those writes goes
            // through the dispatcher, TargetPath included (it drives the bound DisplayPath, so
            // raising its PropertyChanged off-thread was the same hazard the rest of this block was
            // already avoiding). The adapter above posts onto this same queue in one hop, so a
            // progress update still in flight cannot be processed after — and clobber — this
            // terminal state.
            Dispatcher.UIThread.Post(() =>
            {
                job.TargetPath = outcome.FinalPath;

                if (outcome.Status == DownloadStatus.Cancelled || job.WasCancelledByUser)
                {
                    job.Status = JobStatus.Cancelled;
                    job.StatusMessage = "Cancelled";
                    return;
                }

                switch (outcome.Status)
                {
                    case DownloadStatus.Completed:
                        job.ProgressPercent = 100; // the throttled progress adapter's last report may sit below 100
                        job.ActualSha256 ??= job.ExpectedSha256; // verified inside the downloader
                        job.Status = JobStatus.Completed;
                        job.StatusMessage = "Done";
                        break;

                    case DownloadStatus.CompletedMetadataIncomplete:
                        // The file can land perfectly while its Civitai metadata fetch fails. That
                        // used to finish as a plain "Done", leaving the LoRA with no description,
                        // tags or preview and no hint of why.
                        job.ProgressPercent = 100;
                        job.ActualSha256 ??= job.ExpectedSha256; // verified inside the downloader
                        job.Status = JobStatus.Completed;
                        job.StatusMessage = "Done — no metadata";
                        _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                            $"{job.ModelName} — {job.VersionName} downloaded, but its Civitai metadata could not be fetched. "
                            + "Use Download Metadata on the model in the Installed tab to fill it in.",
                            $"VersionId: {job.VersionId}\nFile: {job.TargetPath}");
                        break;

                    case DownloadStatus.ReusedExisting:
                        // No transfer happened, so the throttled adapter never reported anything —
                        // without this the tile would sit at 0% next to "Already downloaded".
                        job.ProgressPercent = 100;
                        job.Status = JobStatus.Completed;
                        job.StatusMessage = "Already downloaded";
                        break;

                    case DownloadStatus.HashMismatch:
                        job.Status = JobStatus.Failed;
                        job.StatusMessage = job.ExpectedSha256 is { Length: >= 8 }
                            ? $"Hash mismatch (expected {job.ExpectedSha256[..8]}…)"
                            : "Hash mismatch";
                        _logger?.Warn(LogCategory.Download, "CivitaiQueue",
                            $"SHA256 mismatch for {job.TargetPath}: {outcome.Error}");
                        break;

                    case DownloadStatus.Failed:
                    default:
                        job.Status = JobStatus.Failed;
                        if (string.IsNullOrEmpty(job.StatusMessage) || job.StatusMessage == "Connecting...")
                            job.StatusMessage = outcome.Error ?? "Failed";
                        break;
                }
            });
        }
        // These two catches write terminal state synchronously, unlike the happy path above which
        // posts through the dispatcher — that's fine here because an exception this far out means
        // `_downloader.DownloadAsync` itself threw, which happens before any progress report could
        // still be in flight on the dispatcher queue (OperationCanceledException in particular is
        // swallowed inside the downloader itself and surfaces as a Cancelled/Failed outcome, not a
        // throw, in the ordinary case), so there is nothing racing this write to clobber.
        catch (OperationCanceledException)
        {
            job.Status = job.WasCancelledByUser ? JobStatus.Cancelled : JobStatus.Failed;
            job.StatusMessage = job.WasCancelledByUser ? "Cancelled" : "Stopped";
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.StatusMessage = ex.Message;
        }
    }

    #region Persistence

    private string GetPersistPath()
    {
        if (_persistPathOverride is not null) return _persistPathOverride;

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
            var snapshot = Jobs.Select(j =>
            {
                // Mid-download jobs come back as Queued so they auto-resume on Start;
                // discard their in-flight status text/progress in that case.
                var persistStatus = j.Status == JobStatus.Downloading ? JobStatus.Queued : j.Status;
                var persistMessage = j.Status == JobStatus.Downloading ? null : j.StatusMessage;
                var persistPercent = j.Status == JobStatus.Downloading ? 0 : j.ProgressPercent;
                return new PersistedJob
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
                    SizeBytes = j.SizeBytes,
                    IsEarlyAccess = j.IsEarlyAccess,
                    ExpectedSha256 = j.ExpectedSha256,
                    ActualSha256 = j.ActualSha256,
                    PreviewImageUrl = j.PreviewImageUrl,
                    CustomTargetDirectory = j.CustomTargetDirectory,
                    TargetPath = j.TargetPath,
                    Status = persistStatus,
                    StatusMessage = persistMessage,
                    ProgressPercent = persistPercent
                };
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
                var job = new CivitaiDownloadJob
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
                    SizeBytes = p.SizeBytes,
                    IsEarlyAccess = p.IsEarlyAccess,
                    ExpectedSha256 = p.ExpectedSha256,
                    ActualSha256 = p.ActualSha256,
                    PreviewImageUrl = p.PreviewImageUrl,
                    CustomTargetDirectory = p.CustomTargetDirectory,
                    TargetPath = p.TargetPath,
                    Status = p.Status,
                    CivitaiVersion = null // Rehydrated lazily on resume.
                };
                // Restore the human-readable status text and the final progress so the
                // tile still reads "Done" / "Failed" / "Cancelled" after a restart
                // instead of looking blank.
                job.StatusMessage = p.StatusMessage;
                job.ProgressPercent = p.ProgressPercent;
                Jobs.Add(job);
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
        public long SizeBytes { get; set; }
        public bool IsEarlyAccess { get; set; }
        public string? ExpectedSha256 { get; set; }
        public string? ActualSha256 { get; set; }
        public string? PreviewImageUrl { get; set; }
        public string? CustomTargetDirectory { get; set; }
        public string? TargetPath { get; set; }

        [JsonConverter(typeof(JsonStringEnumConverter))]
        public JobStatus Status { get; set; }

        /// <summary>
        /// Human-readable status text last shown on the tile ("Done", "Failed",
        /// "Cancelled", error detail, etc.). Restored on next session so the user
        /// sees what happened to past downloads.
        /// </summary>
        public string? StatusMessage { get; set; }

        /// <summary>Final progress percent — 100 for Completed, intermediate for in-flight
        /// snapshots that aren't currently being saved as Downloading.</summary>
        public double ProgressPercent { get; set; }
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
    Failed,
    Cancelled
}

public partial class CivitaiDownloadJob : ObservableObject
{
    /// <summary>
    /// Per-job cancellation source. The queue links this with its run-wide CTS so
    /// either source firing aborts the download. Recreated on Retry so a fresh
    /// run starts with an un-cancelled token.
    /// </summary>
    private CancellationTokenSource _jobCts = new();

    public CancellationToken CancellationToken => _jobCts.Token;

    /// <summary>True when the user clicked Cancel on this specific job (vs. the queue
    /// being cleared or app shutting down). Lets the worker distinguish "user-cancelled"
    /// from "stopped for some other reason" when picking the final <see cref="Status"/>.</summary>
    public bool WasCancelledByUser { get; private set; }

    public void CancelByUser()
    {
        WasCancelledByUser = true;
        try { _jobCts.Cancel(); } catch { /* already disposed */ }
        StatusMessage = "Cancelling...";
    }

    /// <summary>Throws away the cancelled token and prepares for a fresh run.</summary>
    public void ResetForRetry()
    {
        try { _jobCts.Dispose(); } catch { /* best-effort */ }
        _jobCts = new CancellationTokenSource();
        WasCancelledByUser = false;
        ProgressPercent = 0;
        StatusMessage = null;
        Status = JobStatus.Queued;
    }

    public int ModelId { get; init; }
    public int VersionId { get; init; }
    public string ModelName { get; init; } = string.Empty;
    public string VersionName { get; init; } = string.Empty;
    public string BaseModel { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string DownloadUrl { get; init; } = string.Empty;
    public string SizeDisplay { get; init; } = string.Empty;
    public long SizeBytes { get; init; }

    /// <summary>
    /// True when the picked version was early-access gated at enqueue time (see
    /// <see cref="DiffusionNexus.Civitai.Models.CivitaiEarlyAccessExtensions.IsEarlyAccessActive"/>).
    /// Drives the EA badge on the tile and lets the download service surface a
    /// precise cause when a 401 lands on this job.
    /// </summary>
    public bool IsEarlyAccess { get; init; }
    public string? ExpectedSha256 { get; init; }
    public string? PreviewImageUrl { get; init; }
    public string? ActualSha256 { get; set; }
    public string? CustomTargetDirectory { get; set; }

    [JsonIgnore]
    public CivitaiModelVersion? CivitaiVersion { get; set; }

    [ObservableProperty]
    private string? _targetPath;

    [ObservableProperty]
    private JobStatus _status = JobStatus.Queued;

    [ObservableProperty]
    private double _progressPercent;

    [ObservableProperty]
    private string? _statusMessage;

    /// <summary>
    /// Where the job is expected to land on disk, resolved from the shared destination
    /// at enqueue time (and recomputed when destination settings change). Superseded by
    /// <see cref="TargetPath"/> once the downloader reports where the file actually landed —
    /// that only happens once, when <c>DownloadAsync</c> returns, not progressively while
    /// downloading is in flight.
    /// </summary>
    [ObservableProperty]
    private string? _expectedTargetDir;

    /// <summary>
    /// Final-or-planned path to show on the queue tile. Prefers the on-disk
    /// <see cref="TargetPath"/> once the downloader has reported it, falling back to
    /// <see cref="ExpectedTargetDir"/> while the job is still queued or downloading.
    /// </summary>
    public string? DisplayPath => TargetPath ?? ExpectedTargetDir;

    // Status-colored brushes for the tile's status text. Static so we allocate once
    // and reuse across every job. The default brush mimics the existing 70%-opacity
    // foreground used elsewhere in the queue tile.
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#22C55E"));
    private static readonly IBrush FailedBrush = new SolidColorBrush(Color.Parse("#F87171"));
    private static readonly IBrush DefaultStatusBrush = new SolidColorBrush(Color.Parse("#B3B3B3"));

    /// <summary>
    /// Foreground brush for the tile's status text. Green for Completed, red for
    /// Failed/Cancelled, neutral for Queued / Downloading.
    /// </summary>
    public IBrush StatusForeground => Status switch
    {
        JobStatus.Completed => DoneBrush,
        JobStatus.Failed => FailedBrush,
        JobStatus.Cancelled => FailedBrush,
        _ => DefaultStatusBrush
    };

    /// <summary>True while the worker is actively running — Cancel is available.</summary>
    public bool CanCancel => Status == JobStatus.Downloading;

    /// <summary>True after a terminal failure or user cancel — Retry is available.</summary>
    public bool CanRetry => Status is JobStatus.Failed or JobStatus.Cancelled;

    partial void OnTargetPathChanged(string? value) => OnPropertyChanged(nameof(DisplayPath));
    partial void OnExpectedTargetDirChanged(string? value) => OnPropertyChanged(nameof(DisplayPath));
    partial void OnStatusChanged(JobStatus value)
    {
        OnPropertyChanged(nameof(StatusForeground));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CanRetry));
    }
}
