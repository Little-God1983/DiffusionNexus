using WeCantSpell.Hunspell;

namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Wraps WeCantSpell.Hunspell to provide spell checking for caption text.
/// Thread-safe: the underlying <see cref="WordList"/> is immutable once loaded.
/// </summary>
public sealed class SpellCheckService : ISpellCheckService
{
    private WordList? _wordList;
    private readonly HashSet<string> _supplementaryWords = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUserDictionaryService _userDictionary;
    private readonly string _dictionaryDirectory;

    /// <summary>
    /// Creates a new SpellCheckService that loads dictionaries from the given directory.
    /// </summary>
    public SpellCheckService(IUserDictionaryService userDictionary, string? dictionaryDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(userDictionary);
        _userDictionary = userDictionary;
        _dictionaryDirectory = dictionaryDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "Dictionaries");
        LoadDictionary();
    }

    /// <inheritdoc />
    public bool IsReady => _wordList is not null;

    /// <inheritdoc />
    public bool Check(string word)
    {
        if (string.IsNullOrWhiteSpace(word)) return true;
        if (_userDictionary.Contains(word)) return true;
        if (_supplementaryWords.Contains(word)) return true;
        return _wordList?.Check(word) ?? true;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5)
    {
        if (_wordList is null || string.IsNullOrWhiteSpace(word)) return [];
        return _wordList.Suggest(word).Take(maxSuggestions).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<SpellCheckError> CheckText(string text)
    {
        if (_wordList is null || string.IsNullOrWhiteSpace(text)) return [];

        var errors = new List<SpellCheckError>();
        int i = 0;
        int len = text.Length;

        while (i < len)
        {
            // Skip non-word characters
            if (!IsWordChar(text[i]))
            {
                i++;
                continue;
            }

            // Found start of a word
            int start = i;
            while (i < len && IsWordChar(text[i]))
            {
                i++;
            }

            // Trim trailing hyphens/apostrophes so "word-" or "word'" don't pollute the token.
            // Use a separate variable so i always stays past the consumed token,
            // preventing the outer loop from revisiting the same characters.
            int cleanEnd = i;
            while (cleanEnd > start && text[cleanEnd - 1] is '-' or '\'')
                cleanEnd--;

            var word = text.AsSpan(start, cleanEnd - start);

            // Skip single characters, numbers, and words that start with a digit
            if (word.Length <= 1 || char.IsDigit(word[0]))
                continue;

            // Skip words that are all uppercase (likely acronyms)
            bool allUpper = true;
            foreach (var c in word)
            {
                if (!char.IsUpper(c)) { allUpper = false; break; }
            }
            if (allUpper) continue;

            var wordStr = word.ToString();
            if (!Check(wordStr))
            {
                errors.Add(new SpellCheckError(wordStr, start, wordStr.Length));
            }
        }

        return errors;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SpellCheckError>> CheckTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (_wordList is null || string.IsNullOrWhiteSpace(text))
            return Task.FromResult<IReadOnlyList<SpellCheckError>>([]);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return CheckText(text);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public void AddToUserDictionary(string word)
    {
        _userDictionary.Add(word);
    }

    private void LoadDictionary()
    {
        try
        {
            var affPath = Path.Combine(_dictionaryDirectory, "en_US.aff");
            var dicPath = Path.Combine(_dictionaryDirectory, "en_US.dic");

            if (!File.Exists(affPath) || !File.Exists(dicPath))
            {
                Serilog.Log.Warning("Spell check dictionaries not found at {Path}", _dictionaryDirectory);
                return;
            }

            _wordList = WordList.CreateFromFiles(dicPath, affPath);
            Serilog.Log.Information("Spell check dictionary loaded from {Path}", _dictionaryDirectory);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load spell check dictionary");
        }

        LoadSupplementaryWords();
    }

    private void LoadSupplementaryWords()
    {
        try
        {
            var path = Path.Combine(_dictionaryDirectory, "supplementary_words.txt");
            if (!File.Exists(path)) return;

            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.Length >= 2 && !trimmed.StartsWith('#'))
                {
                    _supplementaryWords.Add(trimmed);
                }
            }

            Serilog.Log.Information(
                "Supplementary dictionary loaded with {Count} words from {Path}",
                _supplementaryWords.Count, path);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Failed to load supplementary dictionary");
        }
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '-';
}
