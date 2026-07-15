using System;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Helpers;

/// <summary>
/// Fills a target list from a source range in scheduler-batched chunks. The
/// first batch is added synchronously (instant first paint); subsequent batches
/// go through <paramref name="post"/> (in production: Dispatcher post at
/// Background priority) so layout and input can interleave. Prevents realizing
/// hundreds of item containers in a single layout pass.
/// </summary>
public static class BatchedListFiller
{
    /// <returns>A cancel action that abandons the not-yet-run batches.</returns>
    public static Action Fill<T>(
        IList<T> target,
        IReadOnlyList<T> source,
        int start,
        int endExclusive,
        int batchSize,
        Action<Action> post,
        Action? onCompleted = null)
    {
        var cancelled = false;
        var next = start;

        void AddBatch()
        {
            if (cancelled) return;

            // Re-clamp against source.Count on every tick: the source can
            // shrink between posted batches (e.g. an item is deleted while the
            // fill is still streaming). endExclusive was captured at Fill time,
            // so trusting it alone would read past the new tail and throw.
            var batchEnd = Math.Min(Math.Min(next + batchSize, endExclusive), source.Count);
            for (; next < batchEnd; next++)
                target.Add(source[next]);

            // A shrink can also leave next at/past the new tail — that is
            // terminal too: stop posting and complete cleanly.
            if (next < endExclusive && next < source.Count)
                post(AddBatch);
            else
                onCompleted?.Invoke();
        }

        AddBatch();
        return () => cancelled = true;
    }
}
