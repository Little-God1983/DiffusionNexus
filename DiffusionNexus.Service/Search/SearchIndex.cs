using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.Service.Search;

/// <summary>
/// Lightweight in-memory inverted index used for fast lookups and autocomplete
/// suggestions. Tokenization is performed on simple separators and search uses
/// exact token matches. Thread-safe build and query operations.
/// </summary>
public class SearchIndex
{
    private readonly Dictionary<string, List<int>> _index = new(StringComparer.OrdinalIgnoreCase);
    public bool IsReady { get; private set; }

    public void Build(IReadOnlyList<string> items)
    {
        var local = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            foreach (var token in Tokenize(item))
            {
                if (!local.TryGetValue(token, out var list))
                    local[token] = list = new();
                list.Add(i);
            }
        }

        lock (_index)
        {
            _index.Clear();
            foreach (var kv in local)
                _index[kv.Key] = kv.Value;
            IsReady = true;
        }
    }

    public IEnumerable<int> Search(string query)
    {
        var tokens = Tokenize(query).ToList();
        lock (_index)
        {
            IEnumerable<int>? result = null;
            foreach (var token in tokens)
            {
                if (!_index.TryGetValue(token, out var list))
                    return Enumerable.Empty<int>();
                result = result == null ? list : result.Intersect(list);
            }
            return result ?? Enumerable.Empty<int>();
        }
    }

    public IEnumerable<int> SearchPrefix(string query)
    {
        var tokens = Tokenize(query).ToList();
        lock (_index)
        {
            IEnumerable<int>? result = null;
            foreach (var token in tokens)
            {
                var matches = _index
                    .Where(kv => kv.Key.StartsWith(token, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(kv => kv.Value)
                    .Distinct()
                    .ToList();
                if (matches.Count == 0)
                    return Enumerable.Empty<int>();
                result = result == null ? matches : result.Intersect(matches);
            }
            return result ?? Enumerable.Empty<int>();
        }
    }

    public IEnumerable<string> Suggest(string prefix, int limit)
    {
        lock (_index)
        {
            return _index.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .Distinct()
                .Take(limit)
                .ToList();
        }
    }

    private static IEnumerable<string> Tokenize(string text)
    {
        char[] seps = [' ', '_', '-', '.', ',', '[', ']', '(', ')'];
        return text.Split(seps, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
