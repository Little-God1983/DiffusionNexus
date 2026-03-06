namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Provides word completions based on a prefix, sorted by frequency.
/// </summary>
public interface IAutoCompleteService
{
    /// <summary>
    /// Returns up to <paramref name="maxResults"/> word completions for the given prefix,
    /// sorted by descending frequency.
    /// </summary>
    IReadOnlyList<string> GetSuggestions(string prefix, int maxResults = 8);

    /// <summary>
    /// Adds or increments the frequency of a word (e.g. when the user types it).
    /// </summary>
    void RecordWord(string word);
}
