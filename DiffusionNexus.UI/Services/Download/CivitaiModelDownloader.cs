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

        // Steps 4–11 run under one cancellation guard. Step 4 alone can take minutes: the
        // collision probe hashes whatever multi-GB file is already sitting on the target name,
        // and MatchesAsync deliberately catches only IOException/UnauthorizedAccessException — so
        // cancelling mid-hash threw OperationCanceledException straight out of DownloadAsync,
        // contradicting the promise made at step 3 that a bad outcome is reported, "not an
        // exception five call sites would each have to remember to catch". Both migrated call
        // sites wrap this in a bare Task.Run with only a finally, so it surfaced as an unobserved
        // task fault with zero user feedback. Cancellation ONLY: any other exception escaping
        // here is a bug, and a bug must not be laundered into a download status.
        try
        {
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

            // Only ever set from a hash this method PROVED against the bytes on disk. Callers stamp
            // their records from it, so an expectation that was never checked must leave it null.
            string? verifiedSha256 = null;

            if (resolution.ExistingContentMatches)
            {
                // The policy only reports a content match after hashing the file and finding it
                // equal to expectedSha256 — so for this branch the expectation IS proven.
                verifiedSha256 = expectedSha256?.ToUpperInvariant();

                // 5 — the bytes are already here. Persist anyway: the file can predate the DB row.
                _logger?.Debug(LogCategory.Download, LogSource,
                    $"Step 5: byte-identical file already on disk — skipping transfer for {resolution.TargetPath}");
                var reusedPersist = await _downloadService
                    .PersistDownloadedModelAsync(resolution.TargetPath, request.Version, request.ExistingModelId)
                    .ConfigureAwait(false);

                // Reuse only claims full success when the metadata actually landed. A failed model-page
                // fetch leaves a library row with no description, tags or preview — the transfer path
                // reports exactly that as CompletedMetadataIncomplete ("Done — no metadata"), and a
                // reused file has no better claim to silence than a transferred one does.
                if (reusedPersist == MetadataPersistOutcome.Complete)
                {
                    status = DownloadStatus.ReusedExisting;
                }
                else
                {
                    status = DownloadStatus.CompletedMetadataIncomplete;
                    _logger?.Warn(LogCategory.Download, LogSource,
                        $"Step 5: {fileName} was already on disk, but its Civitai metadata came back " +
                        $"{reusedPersist}. Use Download Metadata on the model in the Installed tab to fill it in.",
                        $"VersionId: {request.Version.Id}\nFile: {resolution.TargetPath}");
                }
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
                    // cancelled while it was still queued. (A throwing IActivityLogService used to be a
                    // second way to land here — UpdateActivityLog could fail before the work ran and get
                    // reported as this same term. DownloadCoordinator now guards that call, so the term
                    // is once again equivalent to "cancelled while queued".)
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
                            verifiedSha256 = actual;
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
                {
                    await InvalidateFileRecordAsync(resolution.TargetPath).ConfigureAwait(false);
                    return Finish(new DownloadOutcome(status, resolution.TargetPath, null, renamed, error), fileName);
                }
            }

            // 8 — resolve the local model id the persister just wrote (or that was already there).
            var modelId = await ResolveModelIdAsync(resolution.TargetPath, ct).ConfigureAwait(false);

            // 9 + 10 — completion sync then the library-changed signal, DETACHED. The caller still
            // holds its download slot (the queue's own gate AND the coordinator slot) until this method
            // returns, and step 9 does network work — Civitai tag fetch plus thumbnail downloads —
            // behind a process-wide gate. Awaiting it here meant that with two concurrent slots, job
            // N+2's *transfer* could not start until job N's *metadata* had finished: every download's
            // throughput bounded by the slowest thumbnail fetch ahead of it. Order between the two is
            // preserved inside the detached work.
            if (modelId is not null)
                LastCompletionTask = Task.Run(() => RunCompletionThenNotifyAsync(modelId.Value, fileName));

            // 11 — done.
            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 11: {status} at {resolution.TargetPath} (model id {modelId?.ToString() ?? "(none)"}, " +
                $"renamed: {renamed})");
            return Finish(
                new DownloadOutcome(status, resolution.TargetPath, modelId, renamed, error, verifiedSha256),
                fileName);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Finish(new DownloadOutcome(DownloadStatus.Cancelled, null, null, false, "cancelled"), fileName);
        }
    }

    /// <summary>
    /// The detached tail of the last <see cref="DownloadAsync"/> call — steps 9 and 10 — or null
    /// when there was no model id to complete. A test seam: production never awaits it (that is the
    /// whole point), so a test that asserts on the completion sync or the notifier has to.
    /// </summary>
    internal Task? LastCompletionTask { get; private set; }

    /// <summary>
    /// Steps 9 and 10 in their original order, off the caller's download slot.
    /// </summary>
    /// <remarks>
    /// <see cref="CancellationToken.None"/> deliberately: the bytes are already on disk and the row
    /// is already written, so a job token that fires the instant the transfer finishes — the queue's
    /// Start cycle ending, the user hitting Cancel a beat late — must not leave that model
    /// permanently without its tags and thumbnail. Nothing is allowed to escape either: this task is
    /// unobserved by design, so a throwing notifier subscriber would otherwise vanish.
    /// </remarks>
    private async Task RunCompletionThenNotifyAsync(int modelId, string fileName)
    {
        try
        {
            await RunCompletionSyncAsync(modelId, CancellationToken.None).ConfigureAwait(false);

            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 10: notifying model {modelId} downloaded ({fileName})");
            _notifier?.NotifyModelDownloaded(modelId);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, LogSource,
                $"post-download completion for {fileName} failed: {ex.Message}");
        }
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
    /// Clears <c>IsLocalFileValid</c> on the row(s) pointing at a file whose SHA256 did not match.
    /// The transport persists Model/ModelVersion/ModelFile — with <c>IsLocalFileValid = true</c> —
    /// BEFORE step 7 can reject the bytes, so without this a truncated or tampered transfer stayed
    /// permanently registered as a valid library entry: invisible on the Installed tab only until
    /// the next refresh, after which it looked like any other installed LoRA. Detecting the problem
    /// and leaving the evidence in place is worse than not detecting it.
    /// <para>
    /// Never fatal: a failed invalidation is logged and the HashMismatch outcome still stands.
    /// Deliberately NOT on the caller's token — a job cancelled in the same breath as the mismatch
    /// must still get its row marked bad.
    /// </para>
    /// </summary>
    private async Task InvalidateFileRecordAsync(string targetPath)
    {
        if (_scopeFactory is null) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var files = await unitOfWork.ModelFiles.GetByLocalPathAsync(targetPath, CancellationToken.None)
                .ConfigureAwait(false);
            if (files.Count == 0) return;

            foreach (var file in files)
            {
                file.IsLocalFileValid = false;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
            }

            await unitOfWork.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            _logger?.Warn(LogCategory.Download, LogSource,
                $"hash mismatch — file record marked invalid: {targetPath}");
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.Download, LogSource,
                $"Could not mark the mismatched file record invalid: {ex.Message}");
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
            var options = await BuildCompletionOptionsAsync(ct).ConfigureAwait(false);
            var plan = await _librarySync.PlanAsync(SyncScope.ForModels(modelId), options, ct).ConfigureAwait(false);
            var report = await _librarySync.ExecuteAsync(plan, progress: null, ct).ConfigureAwait(false);

            // ExecuteAsync is total now (#535): what used to surface here as an exception (and be
            // logged by the catch below) comes back as a report. Same visibility, same non-fatality.
            if (report.AbortReason is not null)
                _logger?.Warn(LogCategory.Download, LogSource, $"post-download completion aborted: {report.AbortReason}");
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

    /// <summary>
    /// The completion sync's options, carrying the user's retry windows and thumbnail fan-out.
    /// </summary>
    /// <remarks>
    /// Before this, the completion sync was the one thumbnail path that honoured neither: it judged
    /// due-ness by <see cref="SyncRetryPolicy.Default"/> rather than the user's error window, and
    /// fetched at the record's default width of four. A model can finish with a dozen due images,
    /// so someone on a metered connection who set "Thumbnail downloads in parallel = 1" still got
    /// four concurrent CDN GETs after every download — while the documentation said all three paths
    /// were bounded by that number.
    /// <para>
    /// Read per completion rather than cached: this service is scoped, a download takes minutes,
    /// and the setting may have changed since the last one. Its own scope, because this runs on the
    /// detached completion tail alongside whatever else holds the shared <c>DbContext</c>.
    /// </para>
    /// <para>
    /// Never fatal: a completion sync that cannot read settings still runs, on the defaults. A
    /// download that reached disk has succeeded whatever the follow-up does.
    /// </para>
    /// </remarks>
    private async Task<SyncOptions> BuildCompletionOptionsAsync(CancellationToken ct)
    {
        var steps = new HashSet<SyncStepKind> { SyncStepKind.FetchTags, SyncStepKind.Thumbnails };

        if (_scopeFactory is null) return new SyncOptions(steps);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<IAppSettingsService>();
            var settings = await settingsService.GetSettingsAsync(ct).ConfigureAwait(false);
            if (settings is null) return new SyncOptions(steps);

            return new SyncOptions(
                steps,
                RetryPolicy: SyncRetryPolicy.FromDays(
                    settings.SyncNotIdentifiedRetryDays, settings.SyncErrorRetryDays),
                ThumbnailConcurrency: settings.SyncThumbnailConcurrency);
        }
        catch (Exception ex)
        {
            _logger?.Debug(LogCategory.Download, LogSource,
                $"Step 9: could not read the sync settings; using the defaults: {ex.Message}");
            return new SyncOptions(steps);
        }
    }

    private DownloadOutcome Finish(DownloadOutcome outcome, string fileName)
    {
        _logger?.Info(LogCategory.Download, LogSource, $"Download {outcome.Status}: {fileName}");
        return outcome;
    }

    private static string? Present(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
