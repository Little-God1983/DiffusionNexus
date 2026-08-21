using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using Microsoft.EntityFrameworkCore;

namespace DiffusionNexus.DataAccess.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ISyncStateRepository"/>.
/// </summary>
/// <remarks>
/// Candidate selections are read-only projections (<c>AsNoTracking</c>) into the Domain
/// candidate records, so the sync service never sees an EF entity. Timestamps are only
/// ever <i>projected</i> — never compared inside <c>Where</c>, because SQLite cannot
/// translate <see cref="DateTimeOffset"/> comparisons. Retry/staleness decisions are the
/// caller's job (<see cref="SyncRetryPolicy"/>).
/// </remarks>
internal sealed class SyncStateRepository : RepositoryBase<ModelSyncState>, ISyncStateRepository
{
    public SyncStateRepository(DiffusionNexusCoreDbContext context) : base(context)
    {
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> GetModelIdsWithoutStateAsync(CancellationToken ct = default)
        => await Context.Models.Where(m => m.SyncState == null).Select(m => m.Id).ToListAsync(ct).ConfigureAwait(false);

    /// <inheritdoc />
    public Task<ModelSyncState?> GetByModelIdAsync(int modelId, CancellationToken ct = default)
        => DbSet.FirstOrDefaultAsync(s => s.ModelId == modelId, ct);

    /// <inheritdoc />
    public async Task<ModelSyncState> GetOrCreateAsync(int modelId, CancellationToken ct = default)
    {
        // A row added earlier in this unit of work is not queryable yet — check the tracker first.
        var local = Context.ChangeTracker.Entries<ModelSyncState>().FirstOrDefault(e => e.Entity.ModelId == modelId)?.Entity;
        if (local is not null) return local;

        var existing = await DbSet.FirstOrDefaultAsync(s => s.ModelId == modelId, ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        var created = new ModelSyncState { ModelId = modelId, UpdatedAt = DateTimeOffset.UtcNow };
        await DbSet.AddAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Same set as <c>ModelFileSyncService.IsLoraFamily</c>.</summary>
    private static readonly ModelType[] LoraFamily = [ModelType.LORA, ModelType.LoCon, ModelType.DoRA, ModelType.Unknown];

    private static IQueryable<Model> ApplyScope(IQueryable<Model> q, SyncScope scope)
    {
        switch (scope.Kind)
        {
            case SyncScopeKind.Models:
                var ids = scope.ModelIds ?? Array.Empty<int>();
                return q.Where(m => ids.Contains(m.Id));
            case SyncScopeKind.SourceFolder:
                // TODO: Linux Implementation for Task 5: case-sensitive comparison and '/' separator.
                // The trailing separator makes the prefix boundary-aware: "E:\Loras\" must not
                // match "E:\Loras_backup\...".
                var prefix = (scope.SourceFolder ?? string.Empty).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                var prefixLower = prefix.ToLowerInvariant();
                return q.Where(m => m.Versions.Any(v => v.Files.Any(f => f.LocalPath != null && f.LocalPath.ToLower().StartsWith(prefixLower))));
            default:
                return q;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(SyncScope scope, CancellationToken ct = default)
    {
        var models = ApplyScope(Context.Models.AsNoTracking(), scope)
            .Where(m => m.CivitaiId == null && LoraFamily.Contains(m.Type));

        // Flattened as joins from the leaf table on purpose: the equivalent
        // m.Versions.SelectMany(v => v.Files.Where(...)) needs SQL APPLY, which SQLite has not got.
        var rows = await (from f in Context.ModelFiles.AsNoTracking()
                          where f.LocalPath != null && f.IsLocalFileValid
                          join v in Context.ModelVersions.AsNoTracking() on f.ModelVersionId equals v.Id
                          join m in models on v.ModelId equals m.Id
                          select new
                          {
                              m.Id, VersionId = v.Id, FileId = f.Id, m.Name, f.LocalPath, f.HashSHA256, v.BaseModelRaw,
                              f.IsPrimary,
                              Outcome = m.SyncState != null ? m.SyncState.MetadataOutcome : SyncOutcome.None,
                              CheckedAt = m.SyncState != null ? m.SyncState.MetadataCheckedAt : null,
                              Attempts = m.SyncState != null ? m.SyncState.MetadataAttempts : 0,
                              Signature = m.SyncState != null ? m.SyncState.SidecarSignature : null,
                          })
            .ToListAsync(ct).ConfigureAwait(false);

        // One candidate per model: prefer the primary file, then the first. Done in memory (tiny).
        return rows.GroupBy(r => r.Id)
            .Select(g => g.OrderByDescending(r => r.IsPrimary).First())
            .Select(r => new IdentifyCandidate(r.Id, r.VersionId, r.FileId, r.Name, r.LocalPath!, r.HashSHA256, r.BaseModelRaw, r.Outcome, r.CheckedAt, r.Attempts, r.Signature))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCandidate>> SelectTagCandidatesAsync(SyncScope scope, CancellationToken ct = default)
    {
        var rows = await ApplyScope(Context.Models.AsNoTracking(), scope)
            .Where(m => m.CivitaiId != null && !m.IsUserEdited && !m.Tags.Any())
            .Select(m => new
            {
                m.Id,
                CivitaiModelId = m.CivitaiId!.Value,
                m.Name,
                TagsCheckedAt = m.SyncState != null ? m.SyncState.TagsCheckedAt : null,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new TagCandidate(r.Id, r.CivitaiModelId, r.Name, r.TagsCheckedAt)).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageCandidate>> SelectImageCandidatesAsync(SyncScope scope, CancellationToken ct = default)
    {
        var models = ApplyScope(Context.Models.AsNoTracking(), scope)
            .Where(m => m.CivitaiId != null);

        // Joined from ModelVersions rather than SelectMany-ed off the navigation: see SelectIdentifyCandidatesAsync.
        var rows = await (from v in Context.ModelVersions.AsNoTracking()
                          where v.CivitaiId != null && !v.Images.Any()
                          join m in models on v.ModelId equals m.Id
                          select new
                          {
                              ModelId = m.Id,
                              VersionId = v.Id,
                              CivitaiVersionId = v.CivitaiId!.Value,
                              m.Name,
                              ImagesCheckedAt = m.SyncState != null ? m.SyncState.ImagesCheckedAt : null,
                          })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(r => new ImageCandidate(r.ModelId, r.VersionId, r.CivitaiVersionId, r.Name, r.ImagesCheckedAt)).ToList();
    }
}
