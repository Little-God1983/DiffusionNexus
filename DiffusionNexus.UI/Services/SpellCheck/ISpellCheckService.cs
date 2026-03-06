namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Represents a misspelled word with its position in the text.
/// </summary>
/// <param name="Word">The misspelled word.</param>
/// <param name="StartIndex">Zero-based start index in the source text.</param>
/// <param name="Length">Character length of the misspelled word.</param>
public record SpellCheckError(string Word, int StartIndex, int Length);

/// <summary>
/// Provides spell checking and suggestion services for caption text editing.
/// </summary>
public interface ISpellCheckService
{
    /// <summary>
    /// Whether the dictionary has been loaded and the service is ready.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Checks whether a single word is spelled correctly.
    /// </summary>
    bool Check(string word);

    /// <summary>
    /// Returns up to <paramref name="maxSuggestions"/> corrections for a misspelled word.
    /// </summary>
    IReadOnlyList<string> Suggest(string word, int maxSuggestions = 5);

    /// <summary>
    /// Scans the full text and returns all misspelled word positions.
    /// </summary>
    IReadOnlyList<SpellCheckError> CheckText(string text);

    /// <summary>
    /// Adds a word to the user's custom dictionary so it is no longer flagged.
    /// </summary>
    void AddToUserDictionary(string word);
}
