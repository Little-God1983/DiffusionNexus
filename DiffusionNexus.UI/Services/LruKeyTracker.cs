namespace DiffusionNexus.UI.Services;

/// <summary>
/// Tracks least-recently-used ordering for a set of string keys using a case-insensitive
/// comparer (<see cref="StringComparer.OrdinalIgnoreCase"/>).
///
/// <para>
/// Extracted from <see cref="ThumbnailService"/> to fix #429: the service's cache
/// dictionary is case-insensitive, but the original access-order tracking was a
/// <c>LinkedList&lt;string&gt;</c> whose <c>Remove(string)</c> compares ordinally
/// case-sensitive. Windows paths are case-insensitive, so the same file arriving with
/// different casing (e.g. <c>C:\ds\Img.png</c> vs <c>c:\ds\img.png</c>) was treated as
/// one cache entry but two access-order entries, letting stale duplicates accumulate
/// and preventing the cache from ever shrinking back to its configured cap.
/// </para>
///
/// <para>
/// This class has no Avalonia dependency (unlike <see cref="ThumbnailService"/>, whose
/// other responsibilities require a real <c>Bitmap</c> and are hard to unit test without
/// a headless Avalonia platform), so its LRU bookkeeping can be unit tested directly.
/// </para>
///
/// <para>
/// <b>Thread safety:</b> this class is not internally synchronized. Callers must apply
/// their own external locking around all method calls if used from multiple threads
/// (mirroring how <see cref="ThumbnailService"/> guards every call with its
/// <c>_evictionLock</c>).
/// </para>
/// </summary>
internal sealed class LruKeyTracker
{
    private readonly LinkedList<string> _order = new();
    private readonly Dictionary<string, LinkedListNode<string>> _nodes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Number of distinct tracked keys (case-insensitive).</summary>
    public int Count => _nodes.Count;

    /// <summary>
    /// Marks <paramref name="key"/> as the most recently used. If a key that compares
    /// equal case-insensitively is already tracked, its existing node is moved to the
    /// most-recently-used end in place — it does not create a duplicate entry, and the
    /// originally recorded casing is retained.
    /// </summary>
    public void Touch(string key)
    {
        if (_nodes.TryGetValue(key, out var node))
        {
            if (!ReferenceEquals(_order.Last, node))
            {
                _order.Remove(node);
                _order.AddLast(node);
            }

            return;
        }

        var newNode = _order.AddLast(key);
        _nodes[key] = newNode;
    }

    /// <summary>
    /// Removes <paramref name="key"/> from tracking (compared case-insensitively), if present.
    /// </summary>
    /// <returns><see langword="true"/> if a tracked key was found and removed.</returns>
    public bool Remove(string key)
    {
        if (!_nodes.Remove(key, out var node))
            return false;

        _order.Remove(node);
        return true;
    }

    /// <summary>
    /// Removes and returns the least-recently-used tracked key, or <see langword="null"/>
    /// if nothing is tracked.
    /// </summary>
    public string? EvictLeastRecentlyUsed()
    {
        var node = _order.First;
        if (node is null)
            return null;

        _order.RemoveFirst();
        _nodes.Remove(node.Value);
        return node.Value;
    }

    /// <summary>Removes all tracked keys.</summary>
    public void Clear()
    {
        _order.Clear();
        _nodes.Clear();
    }
}
