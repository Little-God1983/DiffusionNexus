namespace DiffusionNexus.UI.Services.Lora.Sorting;

/// <summary>
/// The copies the last sort run made, each a source and the target it was copied to (#651).
/// Right after a copy run every source collides with its own copy, and the planner proved them
/// identical with a full SHA-256 of both files — measured: 106 files, 26 GB, 79 s behind the
/// overlay — to learn what the run already knew. The planner asks here first.
/// </summary>
/// <remarks>
/// A pair is vouched for only while BOTH files still have the size and last-write time they had
/// right after the copy: a file replaced or edited since is hashed again, as before. Each run
/// replaces the previous run's pairs. Safe to call <see cref="IsKnownCopy"/> from the planning
/// thread while a run records: the set is swapped whole, never mutated.
/// </remarks>
public sealed class SortedCopyRegistry
{
    private readonly record struct Stamp(long Length, DateTime LastWriteTimeUtc);

    private volatile Dictionary<(string Source, string Target), (Stamp Source, Stamp Target)> _pairs = new();

    /// <summary>Replaces the recorded pairs with <paramref name="copies"/>, stamped as they are
    /// now. A pair whose file cannot be read is left out — it is simply hashed later.</summary>
    /// <returns>How many pairs were recorded.</returns>
    public int Record(IEnumerable<(string Source, string Target)> copies)
    {
        var pairs = new Dictionary<(string Source, string Target), (Stamp Source, Stamp Target)>(PairComparer.Instance);
        foreach (var (source, target) in copies)
        {
            if (TryStamp(source) is { } sourceStamp && TryStamp(target) is { } targetStamp)
                pairs[(source, target)] = (sourceStamp, targetStamp);
        }
        _pairs = pairs;
        return pairs.Count;
    }

    /// <summary>Whether <paramref name="target"/> is the copy the last run made of
    /// <paramref name="source"/>, and neither has changed since.</summary>
    public bool IsKnownCopy(string source, string target)
        => _pairs.TryGetValue((source, target), out var stamps)
           && TryStamp(source) == stamps.Source
           && TryStamp(target) == stamps.Target;

    private static Stamp? TryStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class PairComparer : IEqualityComparer<(string Source, string Target)>
    {
        public static readonly PairComparer Instance = new();

        public bool Equals((string Source, string Target) x, (string Source, string Target) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Source, y.Source)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Target, y.Target);

        public int GetHashCode((string Source, string Target) pair)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(pair.Target));
    }
}
