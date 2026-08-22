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
    /// at all, so it is not expressed as a prefix comparison: its membership is resolved in memory
    /// by <see cref="ModelIdsUnderAsync"/> and joined in as an id set. That costs one two-column
    /// scan of the visible file rows, which is why it is paid only for the roots that need it — and
    /// it is what keeps the tag and image selections correct, as neither carries a path to re-check.
    /// </para>
    /// <para>
    /// None of it is an indexed lookup: <c>lower(LocalPath)</c> is a function of the column, so no
    /// index on <c>LocalPath</c> can serve it and SQLite scans the file rows either way. What the
    /// SQL buys is that the rows are discarded inside the engine instead of being materialised into
    /// the process. And because EF renders a captured-variable <c>StartsWith</c> as
    /// <c>LIKE @p ESCAPE '\'</c> with <c>%</c>, <c>_</c> and <c>\</c> escaped into the parameter at
    /// runtime, those occurring in a source folder's name are ordinary characters here, not wildcards.
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

        // A composed OR over the roots rather than `roots.Any(...)` inside the predicate: one
        // prefix comparison per spelling, OR-ed, which does not depend on the provider translating
        // LINQ operators over a captured collection.
        return inLibrary ?? (_ => false);
    }

    /// <summary>
    /// Ids of models owning a visible local file under one of <paramref name="roots"/>, decided in
    /// memory by <see cref="LocalPathRoots.IsUnder"/> — the authoritative predicate. Only called
    /// for roots SQL cannot fold (see <see cref="HasFileUnderAnyAsync"/>); it materialises two
    /// columns per visible file row and no BLOBs.
    /// </summary>
    /// <remarks>
    /// SQL still does what it can. A root's leading run of ASCII characters is a prefix every path
    /// under it must also start with — <c>E:\ÖFFENTLICH\Loras</c> yields <c>e:\</c>,
    /// <c>E:\Loras\Öffentlich</c> yields <c>e:\loras\</c> — and that much <c>lower()</c> can fold,
    /// so it narrows the rows the engine hands back. Without it every visible file row in the
    /// library came into the process, once per selection, six to eight times per sync. The in-memory
    /// predicate stays authoritative: the pre-filter can only ever be wider than the answer.
    /// </remarks>
    private async Task<HashSet<int>> ModelIdsUnderAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var files = Context.ModelFiles.AsNoTracking()
            .Where(f => f.LocalPath != null && (f.IsLocalFileValid || f.LocalFileVerifiedAt == null));

        var asciiHead = AsciiHeadFilter(roots);
        if (asciiHead is not null) files = files.Where(asciiHead);

        var rows = await (from f in files
                          join v in Context.ModelVersions on f.ModelVersionId equals v.Id
                          select new { v.ModelId, f.LocalPath })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Where(r => LocalPathRoots.IsUnderAny(r.LocalPath, roots))
            .Select(r => r.ModelId)
            .ToHashSet();
    }

    /// <summary>
    /// A widening SQL pre-filter for <see cref="ModelIdsUnderAsync"/>: the OR of each root's
    /// ASCII head as a lower-cased prefix. Returns <c>null</c> — no pre-filter, every visible row
    /// is a candidate — when any root has no ASCII head at all, i.e. begins with a non-ASCII
    /// character, because there is then nothing SQLite can fold to narrow by.
    /// </summary>
    private static Expression<Func<ModelFile, bool>>? AsciiHeadFilter(IReadOnlyList<string> roots)
    {
        Expression<Func<ModelFile, bool>>? filter = null;

        foreach (var root in roots)
        {
            var head = AsciiLower(AsciiHead(root));
            if (head.Length == 0) return null;

            Expression<Func<ModelFile, bool>> underHead =
                f => f.LocalPath != null && f.LocalPath.ToLower().StartsWith(head);

            filter = filter is null ? underHead : OrElse(filter, underHead);
        }

        return filter;
    }

    /// <summary>The leading run of ASCII characters of <paramref name="root"/>, trailing separators trimmed first.</summary>
    private static string AsciiHead(string root)
    {
        var trimmed = root.TrimEnd('\\', '/');

        var length = 0;
        while (length < trimmed.Length && char.IsAscii(trimmed[length])) length++;

        return trimmed[..length];
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

    /// <summary>
    /// <see cref="ModelImage.UserThumbnailScheme"/> as a SQL <c>LIKE</c> prefix pattern. Built from
    /// the constant so the two can never drift apart.
    /// </summary>
    private const string UserThumbnailUrlPattern = ModelImage.UserThumbnailScheme + "%";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ThumbnailCandidate>> SelectThumbnailCandidatesAsync(
        SyncScope scope, IReadOnlyList<string> enabledSourceRoots, CancellationToken ct = default)
    {
        // No CivitaiId filter, deliberately unlike SelectImageCandidatesAsync: a local-only model
        // whose preview is a file:// sibling on disk has a thumbnail to make and never had a
        // Civitai id to lose.
        var models = await ApplyScopeAsync(Context.Models, scope, enabledSourceRoots, ct).ConfigureAwait(false);

        // Joined from the leaf table rather than SelectMany-ed off the navigations: see
        // SelectIdentifyCandidatesAsync. Every column here is small; the BLOB appears only as the
        // flag, and only ever inside SQLite.
        var rows = await (from i in Context.ModelImages.AsNoTracking()
                          join v in Context.ModelVersions on i.ModelVersionId equals v.Id
                          join m in models on v.ModelId equals m.Id
                          // Both exclusions are from the RANKING, not merely from the result. A
                          // user's own thumbnail is not ours to replace, and a row with no URL has
                          // nothing to fetch — but either one left in the ranking could WIN its
                          // version and thereby suppress the real image behind it. A blank-URL row
                          // flagged "video" would do it every run, failing soft as VideoNoPoster
                          // forever while the version's actual image stayed blank.
                          //
                          // They execute HERE, in SQLite, rather than over the materialised rows,
                          // and the result is identical: both run before the GroupBy either way, so
                          // exactly the same rows leave exactly the same ranking. What changes is
                          // the price — an excluded row is no longer transferred, and its correlated
                          // LocalPath subquery is never evaluated. On a library with tens of
                          // thousands of image rows this query runs twice per sync (plan, then
                          // execute), so that is two full sweeps saved rather than one.
                          //
                          // LIKE, not StartsWith(StringComparison): EF Core cannot translate the
                          // latter. SQLite's LIKE is case-insensitive for ASCII, which is a wider
                          // net than the OrdinalIgnoreCase it replaces only in the sense that it
                          // matches the same strings — the scheme is machine-written, always this
                          // exact lowercase literal, and it contains no LIKE wildcard to escape.
                          where !string.IsNullOrWhiteSpace(i.Url)
                                && !EF.Functions.Like(i.Url, UserThumbnailUrlPattern)
                          select new
                          {
                              ModelId = m.Id,
                              VersionId = v.Id,
                              ImageId = i.Id,
                              v.Name,
                              i.Url,
                              i.MediaType,
                              i.IsNsfw,
                              HasThumbnail = i.ThumbnailData != null && i.ThumbnailData != ThumbnailBlobs.Empty,
                              i.ThumbnailAttemptedAt,
                              i.ThumbnailFailure,
                              // ModelVersion.PrimaryFile, as a correlated subquery. Only the path is
                              // wanted, and only so the provider can probe that directory for a
                              // sibling preview when a recorded file:// path has moved.
                              LocalPath = v.Files
                                  .OrderByDescending(f => f.IsPrimary)
                                  .ThenBy(f => f.Id)
                                  .Select(f => f.LocalPath)
                                  .FirstOrDefault(),
                          })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows
            .GroupBy(r => r.VersionId)
            .Select(g => g.OrderBy(r => PrimaryImageRank(r.IsNsfw, r.MediaType, r.Url)).ThenBy(r => r.ImageId).First())
            // Asked of the primary only. The other images of the version are not work: the tile
            // shows the primary, so bytes for the rest render nowhere and would multiply a
            // library-wide run by the image count per version.
            .Where(r => !r.HasThumbnail)
            .OrderBy(r => r.ModelId).ThenBy(r => r.VersionId)
            .Select(r => new ThumbnailCandidate(
                r.ModelId, r.VersionId, r.ImageId, r.Name, r.Url, r.MediaType, r.LocalPath,
                r.ThumbnailAttemptedAt, r.ThumbnailFailure))
            .ToList();
    }

    /// <summary>
    /// <see cref="ModelVersion.PrimaryImage"/>'s preference order as a sortable rank: a clean still
    /// (0), then any still (1), then a clean video (2), then whatever is left (3).
    /// </summary>
    /// <remarks>
    /// The property is four sequential <c>FirstOrDefault</c> passes over <c>Images</c>; taking the
    /// lowest rank and, within a rank, the first element of the collection is the same answer,
    /// because each pass is exactly one rank's membership test.
    /// <para>
    /// "First element of the collection" is ascending <c>Id</c> — nothing orders <c>Images</c>.
    /// There is no configured ordering, and the SQL EF generates for the include orders by the
    /// principal key alone, so the rows arrive in the order SQLite yields them from the
    /// <c>ModelVersionId</c> index, i.e. by rowid. <c>SortOrder</c> is deliberately NOT a
    /// tie-break here even though it reads like the natural one: the property does not use it, and
    /// this selection has to name the same image the property does or the sync fills in a
    /// thumbnail for something the tile never shows.
    /// <c>SyncStateRepositoryThumbnailTests.ThumbnailCandidates_ParityWithPrimaryImageProperty</c>
    /// pins that against the property itself.
    /// </para>
    /// </remarks>
    private static int PrimaryImageRank(bool isNsfw, string? mediaType, string? url)
        => (isNsfw ? 1 : 0) + (ModelImage.IsVideoLike(mediaType, url) ? 2 : 0);
}
