using WeCantSpell.Hunspell;

namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Trie-based autocomplete backed by the Hunspell dictionary word list.
/// Words are indexed at startup; user-typed words can be added at runtime.
/// </summary>
public sealed class AutoCompleteService : IAutoCompleteService
{
    private readonly TrieNode _root = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates an AutoCompleteService and seeds it from the Hunspell dictionary.
    /// </summary>
    public AutoCompleteService(string? dictionaryDirectory = null)
    {
        var dir = dictionaryDirectory ?? Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        LoadFromDictionary(dir);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetSuggestions(string prefix, int maxResults = 8)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 2)
            return [];

        var lowerPrefix = prefix.ToLowerInvariant();

        lock (_lock)
        {
            var node = FindNode(lowerPrefix);
            if (node is null) return [];

            var results = new List<(string Word, int Frequency)>();
            CollectWords(node, lowerPrefix, results, maxResults * 4);

            return results
                .OrderByDescending(r => r.Frequency)
                .ThenBy(r => r.Word.Length)
                .Select(r => r.Word)
                .Where(w => !w.Equals(lowerPrefix, StringComparison.Ordinal))
                .Take(maxResults)
                .ToList();
        }
    }

    /// <inheritdoc />
    public void RecordWord(string word)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length < 2) return;

        lock (_lock)
        {
            Insert(word.ToLowerInvariant(), 1, increment: true);
        }
    }

    private void LoadFromDictionary(string directory)
    {
        try
        {
            var dicPath = Path.Combine(directory, "en_US.dic");
            var affPath = Path.Combine(directory, "en_US.aff");

            if (!File.Exists(dicPath) || !File.Exists(affPath)) return;

            var wordList = WordList.CreateFromFiles(dicPath, affPath);

            // Index all root words from the dictionary
            lock (_lock)
            {
                foreach (var entry in wordList.RootWords)
                {
                    if (entry.Length >= 2 && entry.All(c => char.IsLetter(c) || c == '\''))
                    {
                        Insert(entry.ToLowerInvariant(), 1, increment: false);
                    }
                }
            }

            Serilog.Log.Information("AutoComplete trie loaded with dictionary words");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load autocomplete dictionary");
        }
    }

    private void Insert(string word, int frequency, bool increment)
    {
        var current = _root;
        foreach (var c in word)
        {
            current.Children ??= [];
            if (!current.Children.TryGetValue(c, out var child))
            {
                child = new TrieNode();
                current.Children[c] = child;
            }
            current = child;
        }
        current.IsWord = true;
        if (increment)
            current.Frequency += frequency;
        else
            current.Frequency = Math.Max(current.Frequency, frequency);
    }

    private TrieNode? FindNode(string prefix)
    {
        var current = _root;
        foreach (var c in prefix)
        {
            if (current.Children is null || !current.Children.TryGetValue(c, out var child))
                return null;
            current = child;
        }
        return current;
    }

    private static void CollectWords(TrieNode node, string prefix, List<(string, int)> results, int limit)
    {
        if (results.Count >= limit) return;

        if (node.IsWord)
        {
            results.Add((prefix, node.Frequency));
        }

        if (node.Children is null) return;

        foreach (var kvp in node.Children)
        {
            if (results.Count >= limit) return;
            CollectWords(kvp.Value, prefix + kvp.Key, results, limit);
        }
    }

    private sealed class TrieNode
    {
        public Dictionary<char, TrieNode>? Children;
        public bool IsWord;
        public int Frequency;
    }
}
