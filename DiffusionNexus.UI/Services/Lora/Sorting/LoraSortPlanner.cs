namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// Pure planner: computes where every candidate lands without touching the disk
/// (reads are injected). Collision policy per spec §7.1 — different content gets a
/// deterministic rename, identical content is skipped, overwrite does not exist.
/// </summary>
public sealed class LoraSortPlanner
{
    private readonly Func<string, string> _hashFile;
    private readonly Func<string, bool> _fileExistsOnDisk;

    public LoraSortPlanner(Func<string, string> hashFile, Func<string, bool> fileExistsOnDisk)
    {
        _hashFile = hashFile;
        _fileExistsOnDisk = fileExistsOnDisk;
    }

    /// <param name="ct">Planning re-hashes on every collision and every option toggle
    /// re-plans, so a large library can spend minutes here — the caller's Cancel must
    /// be able to cut it short.</param>
    public LoraSortPlan BuildPlan(IReadOnlyList<SortCandidate> candidates, LoraSortOptions options,
        CancellationToken ct = default)
    {
        var moves = new List<PlannedMove>(candidates.Count);
        var claimed = new Dictionary<string, Dictionary<string, SortCandidate>>(StringComparer.OrdinalIgnoreCase);
        var hashCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        // A hash we cannot read is not "no collision" — it is a file we cannot prove is
        // identical, so it must be treated as different content (rename, never overwrite).
        // Same guard CivitaiDownloadQueue.ResolveCollisionFreeTargetPathAsync carries; the
        // sorter dropped it while claiming to mirror the convention, so one .safetensors
        // held open by a running backend killed the entire preview.
        string? HashOfFile(string path)
        {
            if (hashCache.TryGetValue(path, out var cached)) return cached;
            try
            {
                return hashCache[path] = _hashFile(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return hashCache[path] = null;
            }
        }

        string? HashOfCandidate(SortCandidate c)
            => NormalizeHash(c.Sha256) ?? HashOfFile(c.FilePath);

        static bool SameContent(string? left, string? right)
            => left is not null && right is not null
               && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var targetDir = SorterPathBuilder.BuildTargetDirectory(
                options.TargetRoot, candidate.BaseModelRaw, candidate.CategoryFolderName, options.IncludeCategory);
            var names = claimed.TryGetValue(targetDir, out var existing)
                ? existing
                : claimed[targetDir] = new Dictionary<string, SortCandidate>(StringComparer.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(candidate.FilePath);

            bool NameIsTaken(string name)
                => names.ContainsKey(name) || _fileExistsOnDisk(Path.Combine(targetDir, name));

            // Whoever holds `name` in this target directory: an earlier candidate of this
            // same plan, or the file already sitting there on disk.
            string? HashOfClaimant(string name)
                => names.TryGetValue(name, out var claimant)
                    ? HashOfCandidate(claimant)
                    : HashOfFile(Path.Combine(targetDir, name));

            var sourceDir = Path.GetDirectoryName(candidate.FilePath) ?? string.Empty;
            if (string.Equals(sourceDir, targetDir, StringComparison.OrdinalIgnoreCase)
                && !names.ContainsKey(fileName))
            {
                names[fileName] = candidate;
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.AlreadyInPlace, WasRenamed: false));
                continue;
            }

            if (!NameIsTaken(fileName))
            {
                names[fileName] = candidate;
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.Transfer, WasRenamed: false));
                continue;
            }

            // Collision: classify by content. Claimant is the earlier candidate if any,
            // otherwise the file already on disk at the plain target path.
            var myHash = HashOfCandidate(candidate);

            if (SameContent(myHash, HashOfClaimant(fileName)))
            {
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.SkippedDuplicate, WasRenamed: false));
                continue;
            }

            // The plain name belongs to different content, so the deterministic
            // {stem}_{versionId} name is next. If THAT one is already taken it is very
            // likely this same file from an earlier run: copy mode leaves the source in
            // place, so run 2 collided on the plain name again, found its own _{versionId}
            // copy "taken" and fell through to _2, run 3 to _3, unbounded. Hash-compare
            // first — an identical file there is our earlier copy, so there is nothing to do.
            if (candidate.CivitaiVersionId is { } versionId)
            {
                var suffixed = $"{Path.GetFileNameWithoutExtension(fileName)}_{versionId}{Path.GetExtension(fileName)}";
                if (NameIsTaken(suffixed) && SameContent(myHash, HashOfClaimant(suffixed)))
                {
                    moves.Add(new PlannedMove(candidate, targetDir,
                        Path.Combine(targetDir, suffixed), PlannedAction.SkippedDuplicate, WasRenamed: false));
                    continue;
                }
            }

            var renamed = SorterPathBuilder.BuildCollisionFreeFileName(
                fileName, candidate.CivitaiVersionId, NameIsTaken);
            names[renamed] = candidate;
            moves.Add(new PlannedMove(candidate, targetDir,
                Path.Combine(targetDir, renamed), PlannedAction.Transfer, WasRenamed: true));
        }

        var transfers = moves.Where(m => m.Action == PlannedAction.Transfer).ToList();
        // TODO: Linux Implementation for LoRA Sorter: Path.GetPathRoot returns "/" for every
        // absolute path on Linux, so sameVolumeMove is always true, requiredBytes collapses to
        // 0 and the free-space pre-flight silently passes for a cross-device move. A Linux
        // build needs a real device-id comparison (stat st_dev / DriveInfo mount point) behind
        // an injectable volume-identity policy rather than this string compare.
        var sameVolumeMove = options.IsMove && string.Equals(
            Path.GetPathRoot(options.SourceRoot), Path.GetPathRoot(options.TargetRoot),
            StringComparison.OrdinalIgnoreCase);
        var requiredBytes = sameVolumeMove ? 0L : transfers.Sum(m => m.Candidate.FileSizeBytes);

        return new LoraSortPlan(
            moves, options.SourceRoot, options.TargetRoot, options.IsMove, requiredBytes,
            TransferCount: transfers.Count,
            AlreadyInPlaceCount: moves.Count(m => m.Action == PlannedAction.AlreadyInPlace),
            RenamedCount: moves.Count(m => m.WasRenamed),
            SkippedDuplicateCount: moves.Count(m => m.Action == PlannedAction.SkippedDuplicate));
    }

    /// <summary>
    /// Stored DB hashes are not trustworthy input: ModelFile.HashSHA256 has been written
    /// in mixed case and with separators by older import paths (the reason
    /// LoraDuplicateFinder.NormalizeHash exists), and a dashed legacy value never equals
    /// a freshly computed one — so "identical content is skipped" silently never fired
    /// and the duplicate was renamed and transferred instead. Anything that is not
    /// exactly 64 hex digits after stripping separators is not a hash and is reported as
    /// absent, so the file is hashed lazily rather than trusted as content identity.
    /// </summary>
    private static string? NormalizeHash(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;

        Span<char> normalized = stackalloc char[64];
        var length = 0;
        foreach (var c in stored)
        {
            if (c is '-' or ':' or ' ' or '\t' or '\r' or '\n') continue;
            if (!Uri.IsHexDigit(c) || length == normalized.Length) return null;
            normalized[length++] = char.ToLowerInvariant(c);
        }
        return length == normalized.Length ? new string(normalized) : null;
    }
}
