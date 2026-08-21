using System.Diagnostics;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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
}
