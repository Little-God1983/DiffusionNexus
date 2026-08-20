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

                foreach (var sidecar in move.Candidate.SidecarPaths)
                {
                    var sidecarTarget = SidecarLocator.DeriveSidecarTargetPath(
                        sidecar, source, move.TargetFilePath);
                    try
                    {
                        if (plan.IsMove)
                            _fileOperations.MoveFile(sidecar, sidecarTarget, overwrite: false);
                        else
                            _fileOperations.CopyFile(sidecar, sidecarTarget, overwrite: false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger?.Warn(LogCategory.FileSystem, LogSource,
                            $"Sidecar transfer failed for {sidecar}: {ex.Message}");
                    }
                }

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

                _historyWriter.MarkCompleted(manifestPath, source);
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

        var skipped = plan.SkippedDuplicateCount;
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"Sort run finished: {moved} moved, {copied} copied, {skipped} skipped, {failed} failed, cancelled={cancelled}");

        return new LoraSortResult(moved, copied, skipped, failed, cancelled, manifestPath);
    }
}
