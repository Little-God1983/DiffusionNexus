namespace DiffusionNexus.UI.Services.SpellCheck;

/// <summary>
/// Manages a custom user dictionary of words that should not be flagged by the spell checker.
/// Words are persisted to disk so they survive application restarts.
/// </summary>
public interface IUserDictionaryService
{
    /// <summary>
    /// Returns true if the word is in the user dictionary (case-insensitive).
    /// </summary>
    bool Contains(string word);

    /// <summary>
    /// Adds a word to the user dictionary and persists it.
    /// </summary>
    void Add(string word);

    /// <summary>
    /// Removes a word from the user dictionary.
    /// </summary>
    void Remove(string word);

    /// <summary>
    /// Returns all words in the user dictionary.
    /// </summary>
    IReadOnlySet<string> GetAll();
}
