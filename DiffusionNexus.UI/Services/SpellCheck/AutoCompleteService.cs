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

            // Index root words and their common inflected forms.
            // RootWords only contains stems (e.g. "disappoint") but not
            // derived forms ("disappointed", "disappointing"). We expand each
            // root with common English suffixes and validate via Check().
            lock (_lock)
            {
                foreach (var entry in wordList.RootWords)
                {
                    if (entry.Length < 2 || !entry.All(c => char.IsLetter(c) || c == '\''))
                        continue;

                    var lower = entry.ToLowerInvariant();
                    Insert(lower, 1, increment: false);

                    // Generate common inflected forms and keep any that the dictionary accepts
                    foreach (var form in ExpandWordForms(lower))
                    {
                        if (form.Length >= 2 && wordList.Check(form))
                        {
                            Insert(form, 1, increment: false);
                        }
                    }
                }
            }

            Serilog.Log.Information("AutoComplete trie loaded with dictionary words and inflected forms");
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load autocomplete dictionary");
        }
    }

    /// <summary>
    /// Generates common English inflected/derived forms from a root word.
    /// Only candidates are returned — callers must validate via <see cref="WordList.Check"/>.
    /// </summary>
    private static IEnumerable<string> ExpandWordForms(string root)
    {
        // Direct suffixes
        yield return root + "s";
        yield return root + "es";
        yield return root + "ed";
        yield return root + "er";
        yield return root + "ers";
        yield return root + "est";
        yield return root + "ing";
        yield return root + "ings";
        yield return root + "ly";
        yield return root + "ment";
        yield return root + "ments";
        yield return root + "ness";
        yield return root + "tion";
        yield return root + "tions";
        yield return root + "able";
        yield return root + "ible";
        yield return root + "ful";
        yield return root + "less";
        yield return root + "ous";
        yield return root + "ive";
        yield return root + "al";
        yield return root + "ity";

        if (root.Length < 3) yield break;

        var last = root[^1];

        // Consonant doubling: run → running, stop → stopped
        if (last is not ('w' or 'x' or 'y') && !IsVowel(last))
        {
            var doubled = root + last;
            yield return doubled + "ed";
            yield return doubled + "er";
            yield return doubled + "ers";
            yield return doubled + "est";
            yield return doubled + "ing";
        }

        // Silent-e drop: make → making, hope → hoped
        if (last == 'e')
        {
            var trimmed = root[..^1];
            yield return trimmed + "ing";
            yield return trimmed + "ed";
            yield return trimmed + "er";
            yield return trimmed + "ers";
            yield return trimmed + "est";
            yield return trimmed + "able";
            yield return trimmed + "ible";
            yield return trimmed + "ation";
            yield return trimmed + "ations";
            yield return trimmed + "ive";
        }

        // Y → ied/ier/ies: happy → happier, carry → carried
        if (last == 'y' && root.Length >= 3 && !IsVowel(root[^2]))
        {
            var trimmed = root[..^1];
            yield return trimmed + "ied";
            yield return trimmed + "ier";
            yield return trimmed + "iers";
            yield return trimmed + "ies";
            yield return trimmed + "iest";
            yield return trimmed + "ily";
            yield return trimmed + "iness";
        }
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';

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
