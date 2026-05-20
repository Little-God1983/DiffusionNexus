using System.Security.Cryptography;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// One physical LoRA file that participates in a duplicate group.
/// Carries enough context for the UI to show thumbnail/name/folder/size
/// and for the deletion path to remove the matching DB rows.
/// </summary>
public sealed record LoraDuplicateFile(
    int ModelId,
    int ModelVersionId,
    int ModelFileId,
    int? ThumbnailImageId,
    string FilePath,
    string FileName,
    string FolderPath,
    long SizeBytes,
    string DisplayName);

/// <summary>
/// A set of two or more LoRA files that are byte-identical.
/// </summary>
public sealed record LoraDuplicateGroup(
    long SizeBytes,
    string Sha256,
    IReadOnlyList<LoraDuplicateFile> Files);

/// <summary>
/// Progress event raised while scanning for duplicates.
/// </summary>
public sealed record LoraDuplicateProgress(
    string Phase,
    int Processed,
    int Total,
    string? CurrentFile = null);

/// <summary>
/// Finds groups of byte-identical LoRA files across every configured
/// source folder, by walking the cached model database. Uses a
/// size-prefilter → SHA256 → byte-compare cascade so only same-size
/// candidates are ever read in full.
/// </summary>
public interface ILoraDuplicateFinder
{
    Task<IReadOnlyList<LoraDuplicateGroup>> FindAsync(
        IProgress<LoraDuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="ILoraDuplicateFinder"/> implementation that operates
/// on the cached model graph loaded via <see cref="IModelSyncService"/>.
/// Reuses cached <see cref="ModelFile.HashSHA256"/> values when present
/// and persists newly computed hashes back so a re-scan is instant.
/// </summary>
public sealed class LoraDuplicateFinder : ILoraDuplicateFinder
{
    private readonly IModelSyncService _syncService;
    private readonly IUnitOfWork _unitOfWork;

    public LoraDuplicateFinder(IModelSyncService syncService, IUnitOfWork unitOfWork)
    {
        _syncService = syncService ?? throw new ArgumentNullException(nameof(syncService));
        _unitOfWork = unitOfWork ?? throw new ArgumentNullException(nameof(unitOfWork));
    }

    public async Task<IReadOnlyList<LoraDuplicateGroup>> FindAsync(
        IProgress<LoraDuplicateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new LoraDuplicateProgress("Loading models", 0, 0));
        var models = await _syncService.LoadCachedModelsAsync(cancellationToken).ConfigureAwait(false);

        var candidates = FlattenCandidates(models);
        if (candidates.Count < 2)
            return Array.Empty<LoraDuplicateGroup>();

        // Group by file size — same size is a necessary precondition for byte-identical.
        var sizeBuckets = candidates
            .GroupBy(c => c.SizeBytes)
            .Where(g => g.Count() > 1)
            .ToList();

        if (sizeBuckets.Count == 0)
            return Array.Empty<LoraDuplicateGroup>();

        var totalToHash = sizeBuckets.Sum(b => b.Count());
        var processed = 0;
        var results = new List<LoraDuplicateGroup>();
        var hashWriteback = new List<(int FileId, string Hash)>();

        foreach (var bucket in sizeBuckets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hash every file in the bucket; reuse cached hash when present.
            var hashed = new List<(Candidate Candidate, string Hash)>(bucket.Count());
            foreach (var candidate in bucket)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LoraDuplicateProgress(
                    "Hashing", processed, totalToHash, candidate.FilePath));

                string? hash = NormalizeHash(candidate.CachedHash);
                if (hash is null)
                {
                    try
                    {
                        hash = await ComputeSha256Async(candidate.FilePath, cancellationToken).ConfigureAwait(false);
                        hashWriteback.Add((candidate.ModelFileId, hash));
                    }
                    catch (FileNotFoundException) { hash = null; }
                    catch (DirectoryNotFoundException) { hash = null; }
                    catch (UnauthorizedAccessException) { hash = null; }
                    catch (IOException) { hash = null; }
                }

                if (hash is not null)
                    hashed.Add((candidate, hash));

                processed++;
            }

            // Group by hash; each ≥2-member hash group is a candidate duplicate set.
            foreach (var hashGroup in hashed.GroupBy(h => h.Hash))
            {
                if (hashGroup.Count() < 2) continue;
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new LoraDuplicateProgress(
                    "Verifying", processed, totalToHash, hashGroup.First().Candidate.FilePath));

                var verified = await VerifyByteEqualAsync(
                    hashGroup.Select(h => h.Candidate).ToList(),
                    cancellationToken).ConfigureAwait(false);

                if (verified.Count >= 2)
                {
                    results.Add(new LoraDuplicateGroup(
                        SizeBytes: verified[0].SizeBytes,
                        Sha256: hashGroup.Key,
                        Files: verified.Select(ToDuplicateFile).ToList()));
                }
            }
        }

        if (hashWriteback.Count > 0)
        {
            await PersistNewHashesAsync(hashWriteback, cancellationToken).ConfigureAwait(false);
        }

        return results
            .OrderByDescending(g => g.SizeBytes)
            .ThenByDescending(g => g.Files.Count)
            .ToList();
    }

    private static List<Candidate> FlattenCandidates(IReadOnlyList<Model> models)
    {
        var list = new List<Candidate>();
        foreach (var model in models)
        {
            foreach (var version in model.Versions)
            {
                foreach (var file in version.Files)
                {
                    if (string.IsNullOrWhiteSpace(file.LocalPath)) continue;
                    if (!File.Exists(file.LocalPath)) continue;

                    long size;
                    if (file.FileSizeBytes is { } cached && cached > 0)
                    {
                        size = cached;
                    }
                    else
                    {
                        try { size = new FileInfo(file.LocalPath).Length; }
                        catch { continue; }
                    }
                    if (size <= 0) continue;

                    var thumbId = version.Images
                        .OrderBy(i => i.SortOrder)
                        .Select(i => (int?)i.Id)
                        .FirstOrDefault();

                    list.Add(new Candidate(
                        ModelId: model.Id,
                        ModelVersionId: version.Id,
                        ModelFileId: file.Id,
                        ThumbnailImageId: thumbId,
                        FilePath: file.LocalPath,
                        SizeBytes: size,
                        CachedHash: file.HashSHA256,
                        DisplayName: model.Name ?? file.FileName));
                }
            }
        }
        return list;
    }

    private static async Task<List<Candidate>> VerifyByteEqualAsync(
        List<Candidate> candidates, CancellationToken token)
    {
        // Defence in depth against the (vanishingly rare) SHA256 collision:
        // confirm each remaining candidate is byte-equal to the first one.
        if (candidates.Count < 2) return candidates;
        var reference = candidates[0];
        var verified = new List<Candidate> { reference };
        for (int i = 1; i < candidates.Count; i++)
        {
            try
            {
                if (await FilesEqualAsync(reference.FilePath, candidates[i].FilePath, token).ConfigureAwait(false))
                    verified.Add(candidates[i]);
            }
            catch (IOException) { /* skip unreadable file */ }
            catch (UnauthorizedAccessException) { /* skip unreadable file */ }
        }
        return verified;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken token)
    {
        await using var stream = File.OpenRead(path);
        using var sha = SHA256.Create();
        var bytes = await sha.ComputeHashAsync(stream, token).ConfigureAwait(false);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static async Task<bool> FilesEqualAsync(string a, string b, CancellationToken token)
    {
        const int buf = 81920;
        await using var fa = File.OpenRead(a);
        await using var fb = File.OpenRead(b);
        if (fa.Length != fb.Length) return false;
        var ba = new byte[buf];
        var bb = new byte[buf];
        int read;
        while ((read = await fa.ReadAsync(ba.AsMemory(0, buf), token).ConfigureAwait(false)) > 0)
        {
            var read2 = await fb.ReadAsync(bb.AsMemory(0, read), token).ConfigureAwait(false);
            if (read != read2 || !ba.AsSpan(0, read).SequenceEqual(bb.AsSpan(0, read)))
                return false;
        }
        return true;
    }

    private static string? NormalizeHash(string? cached)
    {
        if (string.IsNullOrWhiteSpace(cached)) return null;
        // ModelFile.HashSHA256 may have been stored in mixed case or with separators
        // from older import paths — normalise so cache lookups match new computations.
        var stripped = cached.Replace("-", string.Empty).Trim().ToLowerInvariant();
        return stripped.Length == 64 ? stripped : null;
    }

    private async Task PersistNewHashesAsync(
        IReadOnlyList<(int FileId, string Hash)> updates, CancellationToken token)
    {
        try
        {
            foreach (var (fileId, hash) in updates)
            {
                var entity = await _unitOfWork.ModelFiles.GetByIdAsync(fileId, token).ConfigureAwait(false);
                if (entity is null) continue;
                if (!string.Equals(entity.HashSHA256, hash, StringComparison.OrdinalIgnoreCase))
                {
                    entity.HashSHA256 = hash;
                    _unitOfWork.ModelFiles.Update(entity);
                }
            }
            await _unitOfWork.SaveChangesAsync(token).ConfigureAwait(false);
        }
        catch
        {
            // Persisting newly computed hashes is a best-effort optimisation —
            // a failure here must not abort the scan results we already have.
        }
    }

    private static LoraDuplicateFile ToDuplicateFile(Candidate c) => new(
        ModelId: c.ModelId,
        ModelVersionId: c.ModelVersionId,
        ModelFileId: c.ModelFileId,
        ThumbnailImageId: c.ThumbnailImageId,
        FilePath: c.FilePath,
        FileName: Path.GetFileName(c.FilePath),
        FolderPath: Path.GetDirectoryName(c.FilePath) ?? string.Empty,
        SizeBytes: c.SizeBytes,
        DisplayName: c.DisplayName);

    private sealed record Candidate(
        int ModelId,
        int ModelVersionId,
        int ModelFileId,
        int? ThumbnailImageId,
        string FilePath,
        long SizeBytes,
        string? CachedHash,
        string DisplayName);
}
