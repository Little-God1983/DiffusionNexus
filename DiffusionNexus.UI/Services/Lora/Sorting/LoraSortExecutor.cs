using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Utilities;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

public sealed record LoraSortResult(int Moved, int Copied, int Skipped, int Failed, bool Cancelled,
    string? ManifestPath);

/// <summary>
/// Executes a <see cref="LoraSortPlan"/> against disk: transfers each model file
/// and its sidecars, repoints the DB in batches, and journals progress into the
/// per-run manifest (spec §7 step 4/5) so a killed run leaves consistent state.
/// </summary>
public sealed class LoraSortExecutor
{
    private const int DbBatchSize = 20;
    private const string LogSource = "LoraSorter";

    private readonly IFileOperations _fileOperations;
    private readonly ILocalPathUpdater _pathUpdater;
    private readonly SortHistoryWriter _historyWriter;
    private readonly IUnifiedLogger? _logger;

    public LoraSortExecutor(IFileOperations fileOperations, ILocalPathUpdater pathUpdater,
        SortHistoryWriter historyWriter, IUnifiedLogger? logger)
    {
        _fileOperations = fileOperations;
        _pathUpdater = pathUpdater;
        _historyWriter = historyWriter;
        _logger = logger;
    }

    public async Task<LoraSortResult> ExecuteAsync(LoraSortPlan plan,
        IProgress<(double Fraction, string Status)>? progress = null,
        CancellationToken ct = default)
    {
        var manifestPath = _historyWriter.WritePlan(plan, DateTimeOffset.Now);
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"{plan.TransferCount} to transfer, {plan.SkippedDuplicateCount} duplicates skipped, {plan.RenamedCount} renamed");

        var moved = 0;
        var copied = 0;
        var failed = 0;
        var cancelled = false;
        var done = 0;
        var pendingDbChanges = new List<(string OldPath, string NewPath)>();

        // The whole loop sits in a try/finally: whatever unwinds out of it — an
        // exception the per-file filter does not cover, or a plain break — the
        // already-transferred files MUST have their DB rows repointed, or they are
        // physically at their new location while the library still points at the old
        // one (they show as missing in the Installed tab).
        try
        {
            foreach (var move in plan.Moves)
            {
                if (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                if (move.Action != PlannedAction.Transfer)
                    continue;

                var source = move.Candidate.FilePath;
                try
                {
                    _fileOperations.CreateDirectory(move.TargetDirectory);

                    if (plan.IsMove)
                        _fileOperations.MoveFile(source, move.TargetFilePath, overwrite: false);
                    else
                        _fileOperations.CopyFile(source, move.TargetFilePath, overwrite: false);

                    TransferSidecars(move, source, plan.IsMove);

                    if (plan.IsMove)
                    {
                        moved++;
                        pendingDbChanges.Add((source, move.TargetFilePath));
                        if (pendingDbChanges.Count >= DbBatchSize)
                        {
                            try
                            {
                                await _pathUpdater.UpdateLocalPathsAsync(pendingDbChanges, ct);
                                pendingDbChanges.Clear();
                            }
                            catch (OperationCanceledException)
                            {
                                // Files already transferred stay transferred; the pending batch
                                // rides through to the unconditional CancellationToken.None flush below.
                                cancelled = true;
                                break;
                            }
                            catch (Exception ex)
                            {
                                _logger?.Error(LogCategory.FileSystem, LogSource,
                                    $"DB batch flush failed; {pendingDbChanges.Count} rows will be retried at the end of the run.", ex);
                                continue;
                            }
                        }
                    }
                    else
                    {
                        copied++;
                    }

                    MarkCompletedSafely(manifestPath, source);
                    done++;
                    _logger?.Info(LogCategory.FileSystem, LogSource, $"{source} → {move.TargetFilePath}");
                    progress?.Report(((double)done / plan.TransferCount, Path.GetFileName(move.TargetFilePath)));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.Error(LogCategory.FileSystem, LogSource, $"Failed to transfer {source}", ex);
                    failed++;
                }
            }
        }
        finally
        {
            if (pendingDbChanges.Count > 0)
            {
                try
                {
                    await _pathUpdater.UpdateLocalPathsAsync(pendingDbChanges, CancellationToken.None);
                    pendingDbChanges.Clear();
                }
                catch (Exception ex)
                {
                    _logger?.Error(LogCategory.FileSystem, LogSource,
                        $"Final DB batch flush failed; {pendingDbChanges.Count} rows stay stale and will be re-resolved by hash on the next library sync.", ex);
                }
            }
        }

        var skipped = plan.SkippedDuplicateCount;
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Sort run finished: {moved} moved, {copied} copied, {skipped} skipped, {failed} failed, cancelled={cancelled}");

        return new LoraSortResult(moved, copied, skipped, failed, cancelled, manifestPath);
    }

    /// <summary>
    /// Moves/copies the companion files. Target derivation is inside the guarded
    /// block: a sidecar name that does not follow the {stem}{extension} convention
    /// must cost that one companion file, never the whole run.
    /// </summary>
    private void TransferSidecars(PlannedMove move, string source, bool isMove)
    {
        foreach (var sidecar in move.Candidate.SidecarPaths)
        {
            try
            {
                var sidecarTarget = SidecarLocator.DeriveSidecarTargetPath(
                    sidecar, source, move.TargetFilePath);

                if (isMove)
                    _fileOperations.MoveFile(sidecar, sidecarTarget, overwrite: false);
                else
                    _fileOperations.CopyFile(sidecar, sidecarTarget, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _logger?.Warn(LogCategory.FileSystem, LogSource,
                    $"Sidecar transfer failed for {sidecar}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// The manifest is a restore aid, not part of the transfer contract: a truncated
    /// or unreadable manifest must never fail a file that is already on disk at its
    /// new path, and must never let a transient I/O error count a moved file as failed.
    /// </summary>
    private void MarkCompletedSafely(string manifestPath, string source)
    {
        try
        {
            _historyWriter.MarkCompleted(manifestPath, source);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not journal {source} into the sort manifest: {ex.Message}");
        }
    }
}
