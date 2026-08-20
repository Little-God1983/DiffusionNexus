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

    public LoraSortPlan BuildPlan(IReadOnlyList<SortCandidate> candidates, LoraSortOptions options)
    {
        var moves = new List<PlannedMove>(candidates.Count);
        var claimed = new Dictionary<string, Dictionary<string, SortCandidate>>(StringComparer.OrdinalIgnoreCase);
        var hashCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string HashOfFile(string path)
            => hashCache.TryGetValue(path, out var h) ? h : hashCache[path] = _hashFile(path);
        string HashOfCandidate(SortCandidate c)
            => !string.IsNullOrWhiteSpace(c.Sha256) ? c.Sha256! : HashOfFile(c.FilePath);

        foreach (var candidate in candidates)
        {
            var targetDir = SorterPathBuilder.BuildTargetDirectory(
                options.TargetRoot, candidate.BaseModelRaw, candidate.CategoryFolderName, options.IncludeCategory);
            var names = claimed.TryGetValue(targetDir, out var existing)
                ? existing
                : claimed[targetDir] = new Dictionary<string, SortCandidate>(StringComparer.OrdinalIgnoreCase);
            var fileName = Path.GetFileName(candidate.FilePath);

            bool NameIsTaken(string name)
                => names.ContainsKey(name) || _fileExistsOnDisk(Path.Combine(targetDir, name));

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
            var claimantHash = names.TryGetValue(fileName, out var claimant)
                ? HashOfCandidate(claimant)
                : HashOfFile(Path.Combine(targetDir, fileName));

            if (string.Equals(myHash, claimantHash, StringComparison.OrdinalIgnoreCase))
            {
                moves.Add(new PlannedMove(candidate, targetDir,
                    Path.Combine(targetDir, fileName), PlannedAction.SkippedDuplicate, WasRenamed: false));
                continue;
            }

            var renamed = SorterPathBuilder.BuildCollisionFreeFileName(
                fileName, candidate.CivitaiVersionId, NameIsTaken);
            names[renamed] = candidate;
            moves.Add(new PlannedMove(candidate, targetDir,
                Path.Combine(targetDir, renamed), PlannedAction.Transfer, WasRenamed: true));
        }

        var transfers = moves.Where(m => m.Action == PlannedAction.Transfer).ToList();
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
}
