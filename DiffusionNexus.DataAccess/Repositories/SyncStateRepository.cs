using System.Linq.Expressions;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.Sync;
using DiffusionNexus.Domain.Utilities;
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<SyncDerivationInput>> GetDerivationInputsAsync(
        IReadOnlyList<int> modelIds, CancellationToken ct = default)
    {
        if (modelIds.Count == 0) return [];

        // A projection, not an Include: EXISTS subqueries for the two "has any" questions and two
        // scalars, so the images' ThumbnailData BLOBs never leave the database. That is the whole
        // point of the method — see the interface remarks.
        return await Context.Models.AsNoTracking()
            .Where(m => modelIds.Contains(m.Id))
            .Select(m => new SyncDerivationInput(
                m.Id,
                m.CivitaiId,
                m.LastSyncedAt,
                m.Source,
                m.Tags.Any(),
                m.Versions.Any(v => v.Images.Any()),
                // The in-memory twin is SyncStateDeriver.IsPlaceholder. Trim() is SQLite's, which
                // strips spaces rather than every Unicode space character, so a base model written
                // as a lone tab would count as real here and as a placeholder there. Nothing writes
                // one, and the alternative is hauling the strings back to compare them in memory.
                m.Versions.Any(v => v.BaseModelRaw != null
                                    && v.BaseModelRaw.Trim() != ""
                                    && v.BaseModelRaw != "???")))
            .ToListAsync(ct).ConfigureAwait(false);
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
    private async Task<IQueryable<Model>> ApplyScopeAsync(
        IQueryable<Model> q, SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct)
    {
        switch (scope.Kind)
        {
            case SyncScopeKind.Models:
                var ids = scope.ModelIds ?? Array.Empty<int>();
                return q.Where(m => ids.Contains(m.Id));
            case SyncScopeKind.SourceFolder:
                var inFolder = await HasFileUnderAnyAsync([scope.SourceFolder], ct).ConfigureAwait(false);
                return await ApplyLibraryAsync(q.Where(inFolder), enabledSourceRoots, ct).ConfigureAwait(false);
            default:
                return await ApplyLibraryAsync(q, enabledSourceRoots, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Restricts to models with a visible local file under any enabled source root. With no enabled
    /// root nothing is in the library, so nothing is selected.
    /// </summary>
    private async Task<IQueryable<Model>> ApplyLibraryAsync(
        IQueryable<Model> q, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct)
        => q.Where(await HasFileUnderAnyAsync(enabledSourceRoots, ct).ConfigureAwait(false));

    /// <summary>
    /// "Owns a visible local file inside one of <paramref name="roots"/>" — the same question the
    /// viewer asks per file (<see cref="LocalPathRoots.IsUnder"/>), expressed as something SQLite
    /// can run. An empty or blank-only root list yields <c>false</c>: nothing enabled means nothing
    /// in the library, never everything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two spellings of the boundary separator are emitted per root (<c>root\%</c> and
    /// <c>root/%</c>), because the viewer accepts either and a stored path written by code that
    /// joined with a slash was otherwise invisible to every sync selection while still showing in
    /// the grid.
    /// </para>
    /// <para>
    /// The comparison is <c>lower(LocalPath)</c> against an ASCII-folded prefix, and the bundled
    /// e_sqlite3 has no ICU — so <c>lower()</c> folds ASCII and nothing else. For a root made only
    /// of ASCII that is exactly case-insensitive prefix matching and the SQL is authoritative. A
    /// root containing any non-ASCII character (<c>E:\ÖFFENTLICH\Loras</c>) cannot be folded in SQL
    /// at all, so it is not expressed as a <c>LIKE</c>: its membership is resolved in memory by
    /// <see cref="ModelIdsUnderAsync"/> and joined in as an id set. That costs one two-column scan
    /// of the visible file rows, which is why it is paid only for the roots that need it — and it
    /// is what keeps the tag and image selections correct, as neither carries a path to re-check.
    /// </para>
    /// </remarks>
    private async Task<Expression<Func<Model, bool>>> HasFileUnderAnyAsync(
        IReadOnlyList<string?> roots, CancellationToken ct)
    {
        var usable = roots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToList();

        Expression<Func<Model, bool>>? inLibrary = null;
        var unfoldable = new List<string>();

        foreach (var root in usable)
        {
            if (!IsAsciiOnly(root))
            {
                unfoldable.Add(root);
                continue;
            }

            foreach (var prefix in PrefixesOf(root))
            {
                var underRoot = HasFileUnder(prefix);
                inLibrary = inLibrary is null ? underRoot : OrElse(inLibrary, underRoot);
            }
        }

        if (unfoldable.Count > 0)
        {
            var modelIds = await ModelIdsUnderAsync(unfoldable, ct).ConfigureAwait(false);
            Expression<Func<Model, bool>> byId = m => modelIds.Contains(m.Id);
            inLibrary = inLibrary is null ? byId : OrElse(inLibrary, byId);
        }

        // A composed OR over the roots rather than `roots.Any(...)` inside the predicate: this is
        // plain `LIKE 'root%' OR LIKE 'root%'` that SQLite optimises, and it does not depend on
        // the provider's translation of LINQ operators over a parameter collection.
        return inLibrary ?? (_ => false);
    }

    /// <summary>
    /// Ids of models owning a visible local file under one of <paramref name="roots"/>, decided in
    /// memory by <see cref="LocalPathRoots.IsUnder"/> — the authoritative predicate. Only called
    /// for roots SQL cannot fold (see <see cref="HasFileUnderAnyAsync"/>); it materialises two
    /// columns per visible file row and no BLOBs.
    /// </summary>
    private async Task<HashSet<int>> ModelIdsUnderAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var rows = await (from f in Context.ModelFiles.AsNoTracking()
                          where f.LocalPath != null && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null)
                          join v in Context.ModelVersions on f.ModelVersionId equals v.Id
                          select new { v.ModelId, f.LocalPath })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Where(r => LocalPathRoots.IsUnderAny(r.LocalPath, roots))
            .Select(r => r.ModelId)
            .ToHashSet();
    }

    /// <summary>Whether every character of <paramref name="value"/> is ASCII, i.e. whether SQLite's <c>lower()</c> can fold it.</summary>
    private static bool IsAsciiOnly(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAscii(c)) return false;
        }

        return true;
    }

    /// <summary>
    /// The lower-cased prefixes a stored path must start with to lie under <paramref name="root"/>,
    /// one per separator spelling. The trailing separator makes them boundary-aware: "E:\Loras\"
    /// must not match "E:\Loras_backup\...".
    /// </summary>
    // TODO: Linux Implementation for Task 13: case-sensitive paths.
    private static string[] PrefixesOf(string root)
    {
        var trimmed = AsciiLower(root.TrimEnd('\\', '/'));
        return [trimmed + '\\', trimmed + '/'];
    }

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
        var models = await ApplyScopeAsync(Context.Models, scope, enabledSourceRoots, ct).ConfigureAwait(false);

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

        // The authoritative library predicate, applied to the rows themselves and BEFORE the
        // grouping (R6). The SQL above is a pre-filter: it answers "does this MODEL own a file
        // under a root", which is not the same question as "is THIS file under a root". Filtering
        // here means a model with copies both inside and outside an enabled source is identified
        // through the copy the user can actually see, exactly as the viewer fans its tiles out per
        // location — rather than through whichever row won the primary/id tie-break.
        //
        // Explicit ids are the user pointing at models, so no root applies to them at all.
        if (scope.Kind != SyncScopeKind.Models)
        {
            rows = rows
                .Where(r => LocalPathRoots.IsUnderAny(r.LocalPath, enabledSourceRoots))
                .Where(r => scope.Kind != SyncScopeKind.SourceFolder
                            || LocalPathRoots.IsUnder(r.LocalPath, scope.SourceFolder))
                .ToList();
        }

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
        var scoped = await ApplyScopeAsync(Context.Models.AsNoTracking(), scope, enabledSourceRoots, ct).ConfigureAwait(false);

        var rows = await scoped
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
        var models = (await ApplyScopeAsync(Context.Models, scope, enabledSourceRoots, ct).ConfigureAwait(false))
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
