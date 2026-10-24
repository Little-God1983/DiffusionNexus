using System.Diagnostics;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.Lora;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Repoints ModelFile.LocalPath rows inside a fresh UoW scope per batch —
/// same pattern as LoraViewerViewModel's local-metadata writes.</summary>
public sealed class DbLocalPathUpdater : ILocalPathUpdater
{
    private const string LogSource = "LoraSorter";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnifiedLogger? _logger;

    /// <param name="logger">Optional: each batch is timed into the Unified Console, so a
    /// slow DB and a hung one are distinguishable from an exported log.</param>
    public DbLocalPathUpdater(IServiceScopeFactory scopeFactory, IUnifiedLogger? logger = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task UpdateLocalPathsAsync(
        IReadOnlyList<(string OldPath, string NewPath)> changes, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rows = 0;
        foreach (var (oldPath, newPath) in changes)
        {
            // One file may be owned by multiple rows (historic dedup edge) — update all.
            var owners = await unitOfWork.ModelFiles.GetByLocalPathAsync(oldPath, ct);
            foreach (var file in owners)
            {
                file.LocalPath = newPath;
                file.IsLocalFileValid = true;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                rows++;
            }
        }
        await unitOfWork.SaveChangesAsync(ct);
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"DB batch: {changes.Count} paths repointed ({rows} rows) in {stopwatch.ElapsedMilliseconds} ms");
    }

    public async Task RegisterCopiesAsync(IReadOnlyList<(string SourcePath, string CopyPath)> copies,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var created = 0;
        var released = 0;
        foreach (var (sourcePath, copyPath) in copies)
        {
            // The record whose file is really there wins over a stale twin on the same path, and one
            // that knows its base model over a bare "???" twin (a historic duplicate).
            var original = (await unitOfWork.ModelFiles.GetByLocalPathWithMetadataAsync(sourcePath, ct))
                .Where(f => f.ModelVersion?.Model is not null)
                .OrderByDescending(f => f.IsLocalFileValid)
                .ThenByDescending(f => !LoraPathBuilder.IsPlaceholderBaseModel(f.ModelVersion!.BaseModelRaw))
                .ThenByDescending(f => !string.IsNullOrEmpty(f.HashSHA256))
                .FirstOrDefault();
            if (original is null)
                continue; // nothing known about the original: the next scan gives the copy a record

            var claimants = await unitOfWork.ModelFiles.GetByLocalPathAsync(copyPath, ct);

            // Already registered — an earlier run, or a scan the sync has since identified — when a
            // record on the path carries the original's hash, or, for an original never hashed, its
            // size. That record is the copy's: the file is there again, whatever a verify said.
            var registered = claimants.FirstOrDefault(c => SameHash(c.HashSHA256, original.HashSHA256)
                || (string.IsNullOrEmpty(original.HashSHA256) && original.FileSizeBytes is { } size
                    && c.FileSizeBytes == size));
            if (registered is not null)
            {
                registered.IsLocalFileValid = true;
                registered.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                continue;
            }

            // The copy was written with overwrite off, so no file sat on this path before it:
            // whatever record still points here is stale — a file deleted outside the app, or a
            // bare record a scan made after a failed flush. It lets go of the path, or the library
            // would show the copy under that record's name and base model.
            foreach (var stale in claimants)
            {
                stale.LocalPath = null;
                stale.IsLocalFileValid = false;
                stale.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
                released++;
            }

            await unitOfWork.Models.AddAsync(SortCopyRecords.CreateFor(original, copyPath), ct);
            created++;
        }
        await unitOfWork.SaveChangesAsync(ct);
        _logger?.Info(LogCategory.FileSystem, LogSource,
            $"DB batch: {copies.Count} copies, {created} registered with the original's metadata"
            + (released > 0 ? $", {released} stale records on their paths released" : string.Empty)
            + $" in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static bool SameHash(string? left, string? right)
        => !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
