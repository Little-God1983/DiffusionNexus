using DiffusionNexus.DataAccess.UnitOfWork;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>Repoints ModelFile.LocalPath rows inside a fresh UoW scope per batch —
/// same pattern as LoraViewerViewModel's local-metadata writes.</summary>
public sealed class DbLocalPathUpdater : ILocalPathUpdater
{
    private readonly IServiceScopeFactory _scopeFactory;

    public DbLocalPathUpdater(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task UpdateLocalPathsAsync(
        IReadOnlyList<(string OldPath, string NewPath)> changes, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var (oldPath, newPath) in changes)
        {
            // One file may be owned by multiple rows (historic dedup edge) — update all.
            var owners = await unitOfWork.ModelFiles.GetByLocalPathAsync(oldPath, ct);
            foreach (var file in owners)
            {
                file.LocalPath = newPath;
                file.IsLocalFileValid = true;
                file.LocalFileVerifiedAt = DateTimeOffset.UtcNow;
            }
        }
        await unitOfWork.SaveChangesAsync(ct);
    }
}
