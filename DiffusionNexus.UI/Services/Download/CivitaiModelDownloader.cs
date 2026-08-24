using DiffusionNexus.Civitai.Models;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Lora;
using DiffusionNexus.Service.Services.Sync;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Download;

/// <summary>
/// The one Civitai download path (spec §4.4). Before this existed, five callers — the download
/// dialog, the Browse queue, the detail panel, the waitlist and the pipeline installer — each
/// carried their own partial copy of the same eight steps, and every copy was missing a different
/// one: the queue never notified the Installed tab, the dialog never applied the collision policy,
/// the detail panel never verified hashes. Everything a Civitai download must do now lives here,
/// once.
/// </summary>
public sealed class CivitaiModelDownloader : ICivitaiModelDownloader
{
    private const string LogSource = "CivitaiDownload";

    /// <summary>
    /// Serializes the post-download completion sync. Static because the guarded resource —
    /// <see cref="ILibrarySyncService"/>'s single-flight run slot — is process-wide, while this
    /// service is scoped: two queue jobs finishing on different scopes would otherwise both see
    /// <c>IsRunning == false</c> and the second <c>ExecuteAsync</c> would throw.
    /// </summary>
    private static readonly SemaphoreSlim CompletionGate = new(1, 1);

    private readonly ILoraDownloadService? _downloadService;
    private readonly IDownloadCoordinator? _coordinator;
    private readonly ILibrarySyncService? _librarySync;
    private readonly ILibraryChangeNotifier? _notifier;
    private readonly IServiceScopeFactory? _scopeFactory;
    private readonly IUnifiedLogger? _logger;

    public CivitaiModelDownloader(
        ILoraDownloadService? downloadService,
        IDownloadCoordinator? coordinator = null,
        ILibrarySyncService? librarySync = null,
        ILibraryChangeNotifier? notifier = null,
        IServiceScopeFactory? scopeFactory = null,
        IUnifiedLogger? logger = null)
    {
        _downloadService = downloadService;
        _coordinator = coordinator;
        _librarySync = librarySync;
        _notifier = notifier;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<DownloadOutcome> DownloadAsync(
        DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 1 — pick the file and the URL.
        var file = request.File ?? CivitaiVersionFiles.PickPrimary(request.Version);
        var url = Present(file?.DownloadUrl) ?? Present(request.Version.DownloadUrl);

        // 2 — name the file and the coordinator task.
        var fileName = Present(request.FileNameOverride)
            ?? Present(file?.Name)
            ?? $"model_{request.Version.Id}.safetensors";
        var taskName = Present(request.TaskName) ?? $"Download {fileName}";

        _logger?.Info(LogCategory.Download, LogSource,
            $"Download requested: {fileName} → {request.TargetDirectory} [{request.Trigger}]");
        _logger?.Debug(LogCategory.Download, LogSource,
            $"Step 1: version {request.Version.Id}, file '{file?.Name ?? "(none)"}' " +
            $"({(request.File is null ? "picked" : "caller-supplied")}), url {url ?? "(none)"}");
        _logger?.Debug(LogCategory.Download, LogSource, $"Step 2: file name '{fileName}', task '{taskName}'");

        if (url is null)
            return Finish(new DownloadOutcome(DownloadStatus.Failed, null, null, false, "no download URL"), fileName);
        if (_downloadService is null)
            return Finish(new DownloadOutcome(DownloadStatus.Failed, null, null, false, "no download service"), fileName);

        // 3 — make sure the destination exists.
        _logger?.Debug(LogCategory.Download, LogSource, $"Step 3: ensuring target directory {request.TargetDirectory}");
        try
        {
            Directory.CreateDirectory(request.TargetDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // A bad or unwritable destination is the caller's problem to report, not an exception
            // five call sites would each have to remember to catch.
            _logger?.Warn(LogCategory.Download, LogSource,
                $"Cannot create target directory {request.TargetDirectory}: {ex.Message}");
            return Finish(new DownloadOutcome(DownloadStatus.Failed, null, null, false, "target directory unavailable"), fileName);
        }

        // 4 — collision policy: reuse our own bytes, never overwrite someone else's (S4).
        var expectedSha256 = Present(file?.Hashes?.SHA256);
        var resolution = await DownloadCollisionPolicy
            .ResolveAsync(request.TargetDirectory, fileName, request.Version.Id, expectedSha256, ct)
            .ConfigureAwait(false);
        var finalName = Path.GetFileName(resolution.TargetPath);
        var renamed = !string.Equals(finalName, fileName, StringComparison.OrdinalIgnoreCase);
        if (renamed)
        {
            _logger?.Warn(LogCategory.Download, LogSource,
                $"File name collision in {request.TargetDirectory}: '{fileName}' already belongs to a different " +
                $"download — saving as '{finalName}' instead.");
        }
        else
        {
            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 4: no collision — target stays {resolution.TargetPath} " +
                $"(existing bytes match: {resolution.ExistingContentMatches})");
        }

        DownloadStatus status;
        string? error = null;

        if (resolution.ExistingContentMatches)
        {
            // 5 — the bytes are already here. Persist anyway: the file can predate the DB row.
            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 5: byte-identical file already on disk — skipping transfer for {resolution.TargetPath}");
            var reusedPersist = await _downloadService
                .PersistDownloadedModelAsync(resolution.TargetPath, request.Version, request.ExistingModelId)
                .ConfigureAwait(false);
            if (reusedPersist != MetadataPersistOutcome.Complete)
            {
                _logger?.Debug(LogCategory.Download, LogSource,
                    $"Step 5: metadata for the reused file came back {reusedPersist}");
            }

            status = DownloadStatus.ReusedExisting;
        }
        else
        {
            // 6 — the ONE coordinator/TCS wrap. Callers must not add their own (D3).
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var metadataComplete = true;

            // The coordinator runs the work against a token LINKED to ours, which the flyout's
            // per-task Cancel button and app shutdown also signal — and it swallows the resulting
            // OperationCanceledException and just returns false. So the caller's ct alone cannot
            // tell a user-initiated cancel from a real failure. Capture the run token's state here,
            // inside the work, because the coordinator disposes its CTS the moment it returns.
            var runCancelled = false;
            var transferStarted = false;

            async Task<bool> RunAsync(IProgress<DownloadTaskProgress>? coordinatorProgress, CancellationToken runCt)
            {
                transferStarted = true;
                try
                {
                    return await TransferAsync(coordinatorProgress, runCt).ConfigureAwait(false);
                }
                finally
                {
                    runCancelled |= runCt.IsCancellationRequested;
                }
            }

            async Task<bool> TransferAsync(IProgress<DownloadTaskProgress>? coordinatorProgress, CancellationToken runCt)
            {
                await _downloadService.DownloadFileAsync(
                    url, resolution.TargetPath, request.Version, taskName,
                    reportProgress: (pct, msg) =>
                    {
                        progress?.Report(new DownloadProgress((int)(pct * 100), msg));
                        coordinatorProgress?.Report(new DownloadTaskProgress((int)(pct * 100), msg));
                    },
                    completed: () => tcs.TrySetResult(true),
                    failed: () => tcs.TrySetResult(false),
                    existingModelId: request.ExistingModelId,
                    externalCancellationToken: runCt,
                    reportToActivityLog: _coordinator is null,
                    metadataIncomplete: () => metadataComplete = false).ConfigureAwait(false);
                return await tcs.Task.ConfigureAwait(false);
            }

            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 6: transferring to {resolution.TargetPath} ({(_coordinator is null ? "inline" : "queued")})");

            bool ok;
            try
            {
                ok = _coordinator is not null
                    ? await _coordinator.EnqueueAsync(taskName, RunAsync, ct).ConfigureAwait(false)
                    : await RunAsync(null, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ok = false;
            }
            catch (Exception ex)
            {
                _logger?.Warn(LogCategory.Download, LogSource, $"Transfer of {fileName} threw: {ex.Message}");
                ok = false;
            }

            if (!ok)
            {
                // `!transferStarted` means the coordinator gave up before it ever called the work —
                // its only pre-work await is the slot wait on the linked token, i.e. a task the user
                // cancelled while it was still queued.
                var cancelled = ct.IsCancellationRequested || runCancelled || !transferStarted;
                return Finish(
                    new DownloadOutcome(
                        cancelled ? DownloadStatus.Cancelled : DownloadStatus.Failed,
                        null, null, renamed, cancelled ? "cancelled" : "transfer failed"),
                    fileName);
            }

            status = metadataComplete ? DownloadStatus.Completed : DownloadStatus.CompletedMetadataIncomplete;

            // 7 — verify. Non-fatal by queue parity: a mismatched file is kept for inspection.
            if (expectedSha256 is null)
            {
                _logger?.Debug(LogCategory.Download, LogSource, "Step 7: no expected SHA256 — verification skipped");
            }
            else if (File.Exists(resolution.TargetPath))
            {
                try
                {
                    var actual = await FileHasher.Sha256UpperAsync(resolution.TargetPath, ct).ConfigureAwait(false);
                    if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger?.Warn(LogCategory.Download, LogSource,
                            $"SHA256 mismatch for {resolution.TargetPath} — got {actual}, expected {expectedSha256}");
                        status = DownloadStatus.HashMismatch;
                        error = "hash mismatch";
                    }
                    else
                    {
                        _logger?.Debug(LogCategory.Download, LogSource,
                            $"Step 7: SHA256 verified for {resolution.TargetPath} ({actual})");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.Warn(LogCategory.Download, LogSource, $"SHA256 verification failed: {ex.Message}");
                }
            }

            if (status == DownloadStatus.HashMismatch)
                return Finish(new DownloadOutcome(status, resolution.TargetPath, null, renamed, error), fileName);
        }

        // 8 — resolve the local model id the persister just wrote (or that was already there).
        var modelId = await ResolveModelIdAsync(resolution.TargetPath, ct).ConfigureAwait(false);

        // 9 — completion sync: tags + thumbnails for just this model.
        if (modelId is not null)
            await RunCompletionSyncAsync(modelId.Value, ct).ConfigureAwait(false);

        // 10 — tell the rest of the app the library changed, even when the sync was skipped.
        if (modelId is not null)
        {
            _logger?.Debug(LogCategory.Download, LogSource, $"Step 10: notifying model {modelId.Value} downloaded");
            _notifier?.NotifyModelDownloaded(modelId.Value);
        }

        // 11 — done.
        _logger?.Debug(LogCategory.Download, LogSource,
            $"Step 11: {status} at {resolution.TargetPath} (model id {modelId?.ToString() ?? "(none)"}, " +
            $"renamed: {renamed})");
        return Finish(new DownloadOutcome(status, resolution.TargetPath, modelId, renamed, error), fileName);
    }

    private async Task<int?> ResolveModelIdAsync(string targetPath, CancellationToken ct)
    {
        if (_scopeFactory is null)
        {
            _logger?.Debug(LogCategory.Download, LogSource, "Step 8: no scope factory — model id unresolved");
            return null;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var model = await unitOfWork.Models.FindByLocalFilePathAsync(targetPath, ct).ConfigureAwait(false);
            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 8: {targetPath} resolved to model id {model?.Id.ToString() ?? "(none)"}");
            return model?.Id;
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, LogSource, $"Could not resolve the downloaded model id: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Fills in the two things a download cannot: Civitai tags and a thumbnail. Never fatal — a
    /// download that reached disk has succeeded whatever the follow-up does.
    /// </summary>
    private async Task RunCompletionSyncAsync(int modelId, CancellationToken ct)
    {
        if (_librarySync is null) return;

        var acquired = false;
        try
        {
            await CompletionGate.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;

            if (_librarySync.IsRunning)
            {
                _logger?.Debug(LogCategory.Download, LogSource,
                    $"Step 9: a library sync is already running — completion for model {modelId} skipped");
                return;
            }

            _logger?.Debug(LogCategory.Download, LogSource, $"Step 9: completing metadata for model {modelId}");
            var options = new SyncOptions(new HashSet<SyncStepKind> { SyncStepKind.FetchTags, SyncStepKind.Thumbnails });
            var plan = await _librarySync.PlanAsync(SyncScope.ForModels(modelId), options, ct).ConfigureAwait(false);
            await _librarySync.ExecuteAsync(plan, progress: null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, LogSource, $"post-download completion skipped: {ex.Message}");
        }
        finally
        {
            if (acquired) CompletionGate.Release();
        }
    }

    private DownloadOutcome Finish(DownloadOutcome outcome, string fileName)
    {
        _logger?.Info(LogCategory.Download, LogSource, $"Download {outcome.Status}: {fileName}");
        return outcome;
    }

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
