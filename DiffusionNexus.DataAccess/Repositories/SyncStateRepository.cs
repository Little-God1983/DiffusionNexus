using System.Linq.Expressions;
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
        // ModelId is the primary key, so FindAsync resolves it against the identity map first —
        // including a row Added earlier in this unit of work, which is not queryable yet — and only
        // hits the database on a miss.
        var existing = await DbSet.FindAsync([modelId], ct).ConfigureAwait(false);
        if (existing is not null) return existing;

        var created = new ModelSyncState { ModelId = modelId, UpdatedAt = DateTimeOffset.UtcNow };
        await DbSet.AddAsync(created, ct).ConfigureAwait(false);
        return created;
    }

    /// <summary>Same set as <c>ModelFileSyncService.IsLoraFamily</c>.</summary>
    private static readonly ModelType[] LoraFamily = [ModelType.LORA, ModelType.LoCon, ModelType.DoRA, ModelType.Unknown];

    /// <summary>
    /// Narrows <paramref name="q"/> to the run's target. Library and SourceFolder are both
    /// restricted to the library — a visible local file under an enabled source root — because a
    /// row whose source the user has disabled or removed is no longer something they can see;
    /// SourceFolder narrows further to the one folder. Explicit ids bypass the library predicate:
    /// the user pointed at those models.
    /// </summary>
    private static IQueryable<Model> ApplyScope(IQueryable<Model> q, SyncScope scope, IReadOnlyList<string> enabledSourceRoots)
    {
        switch (scope.Kind)
        {
            case SyncScopeKind.Models:
                var ids = scope.ModelIds ?? Array.Empty<int>();
                return q.Where(m => ids.Contains(m.Id));
            case SyncScopeKind.SourceFolder:
                return ApplyLibrary(q.Where(HasFileUnder(PrefixOf(scope.SourceFolder))), enabledSourceRoots);
            default:
                return ApplyLibrary(q, enabledSourceRoots);
        }
    }

    /// <summary>
    /// Restricts to models with a visible local file under any enabled source root (see
    /// <see cref="HasFileUnder"/>). With no enabled root nothing is in the library, so nothing is
    /// selected.
    /// </summary>
    private static IQueryable<Model> ApplyLibrary(IQueryable<Model> q, IReadOnlyList<string> enabledSourceRoots)
    {
        Expression<Func<Model, bool>>? inLibrary = null;

        foreach (var root in enabledSourceRoots.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var underRoot = HasFileUnder(PrefixOf(root));
            inLibrary = inLibrary is null ? underRoot : OrElse(inLibrary, underRoot);
        }

        // A composed OR over the roots rather than `roots.Any(...)` inside the predicate: this is
        // plain `LIKE 'root%' OR LIKE 'root%'` that SQLite optimises, and it does not depend on
        // the provider's translation of LINQ operators over a parameter collection.
        return q.Where(inLibrary ?? (_ => false));
    }

    /// <summary>
    /// The lower-cased prefix a stored path must start with to lie under <paramref name="root"/>.
    /// The trailing separator makes it boundary-aware: "E:\Loras\" must not match "E:\Loras_backup\...".
    /// </summary>
    // TODO: Linux Implementation for Task 13: case-sensitive paths and '/' separator.
    private static string PrefixOf(string? root)
        => AsciiLower((root ?? string.Empty).TrimEnd('\\', '/') + Path.DirectorySeparatorChar);

    /// <summary>
    /// Lower-cases the ASCII letters and nothing else, because the other side of the comparison is
    /// <c>f.LocalPath.ToLower()</c> — SQLite's own <c>lower()</c>, and the bundled e_sqlite3 has no
    /// ICU, so it folds ASCII only. .NET's full-Unicode <c>ToLowerInvariant</c> turned a root like
    /// <c>E:\Öffentlich\Loras</c> into <c>e:\öffentlich\loras\</c> while the column still read
    /// <c>e:\Öffentlich\loras\…</c>: no match, and a silently empty Library plan.
    /// </summary>
    private static string AsciiLower(string value)
        => string.Create(value.Length, value, static (destination, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                destination[i] = char.IsAsciiLetterUpper(c) ? (char)(c + 32) : c;
            }
        });

    /// <summary>
    /// Models owning at least one local file the user can actually see whose path starts with
    /// <paramref name="prefixLower"/>. "Can see" mirrors <c>ModelFileSyncService.LoadCachedFilesAsync</c>:
    /// a row is kept unless it was verified and found missing, so a legacy row that predates
    /// verification (<c>IsLocalFileValid == false</c> because it defaulted there, never checked)
    /// stays in the sync library exactly as it stays in the viewer (issue #380). The identify step
    /// still probes <c>File.Exists</c> before hashing, so a file that really is gone costs nothing.
    /// </summary>
    private static Expression<Func<Model, bool>> HasFileUnder(string prefixLower)
        => m => m.Versions.Any(v => v.Files.Any(f =>
               f.LocalPath != null
               && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null)
               && f.LocalPath.ToLower().StartsWith(prefixLower)));

    /// <summary>Combines two predicates over the same parameter with <c>||</c>, keeping them translatable.</summary>
    private static Expression<Func<T, bool>> OrElse<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = Expression.Parameter(typeof(T), "e");
        return Expression.Lambda<Func<T, bool>>(
            Expression.OrElse(Rebind(left, parameter), Rebind(right, parameter)),
            parameter);
    }

    private static Expression Rebind<T>(Expression<Func<T, bool>> lambda, ParameterExpression parameter)
        => new ParameterRebinder(lambda.Parameters[0], parameter).Visit(lambda.Body);

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<IdentifyCandidate>> SelectIdentifyCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, bool includeMatched, CancellationToken ct = default)
    {
        var models = ApplyScope(Context.Models, scope, enabledSourceRoots);

        // A model that already carries a Civitai id has nothing to identify — unless the caller
        // asked for it anyway, which is what a forced re-fetch (the per-tile "Download Metadata"
        // button) is. Selecting it is not the same as re-checking it: the retry policy still
        // decides due-ness, and only a force makes a Matched row due.
        //
        // A hand-edited model is excluded from the same bulk run for a different reason: nothing
        // upstream is more authoritative than what the user typed, so there is nothing to gain by
        // offering it and a name/description/trigger-word overwrite to lose. Under includeMatched
        // it IS offered — the user pointed at it — and the appliers protect the authored fields.
        if (!includeMatched) models = models.Where(m => m.CivitaiId == null && !m.IsUserEdited);

        // The type filter keeps a library-wide run off checkpoints and the like. Explicit ids are
        // the user pointing at models, so it has no business discarding what they pointed at.
        if (scope.Kind != SyncScopeKind.Models) models = models.Where(m => LoraFamily.Contains(m.Type));

        // Flattened as joins from the leaf table on purpose: the equivalent
        // m.Versions.SelectMany(v => v.Files.Where(...)) needs SQL APPLY, which SQLite has not got.
        var rows = await (from f in Context.ModelFiles.AsNoTracking()
                          // Same "the user can see this file" rule as HasFileUnder.
                          where f.LocalPath != null && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null)
                          join v in Context.ModelVersions on f.ModelVersionId equals v.Id
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

        // One candidate per model: prefer the primary file, then the lowest version/file id. Done in
        // memory (tiny). SQLite guarantees no row order without an ORDER BY, so the tie-breakers and
        // the final ordering are what make repeated runs return the same candidates in the same order.
        return rows.GroupBy(r => r.Id)
            .Select(g => g.OrderByDescending(r => r.IsPrimary).ThenBy(r => r.VersionId).ThenBy(r => r.FileId).First())
            .OrderBy(r => r.Id)
            .Select(r => new IdentifyCandidate(r.Id, r.VersionId, r.FileId, r.Name, r.LocalPath!, r.HashSHA256, r.BaseModelRaw, r.Outcome, r.CheckedAt, r.Attempts, r.Signature))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TagCandidate>> SelectTagCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default)
    {
        var rows = await ApplyScope(Context.Models.AsNoTracking(), scope, enabledSourceRoots)
            .Where(m => m.CivitaiId != null && !m.IsUserEdited && !m.Tags.Any())
            .Select(m => new
            {
                m.Id,
                CivitaiModelId = m.CivitaiId!.Value,
                m.Name,
                TagsCheckedAt = m.SyncState != null ? m.SyncState.TagsCheckedAt : null,
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.OrderBy(r => r.Id)
            .Select(r => new TagCandidate(r.Id, r.CivitaiModelId, r.Name, r.TagsCheckedAt))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ImageCandidate>> SelectImageCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default)
    {
        var models = ApplyScope(Context.Models, scope, enabledSourceRoots)
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

        return rows.OrderBy(r => r.ModelId).ThenBy(r => r.VersionId)
            .Select(r => new ImageCandidate(r.ModelId, r.VersionId, r.CivitaiVersionId, r.Name, r.ImagesCheckedAt))
            .ToList();
    }
}
