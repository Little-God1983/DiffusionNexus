namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Lightweight spell-checking contract used by the dataset quality pipeline.
/// Implementations wrap a concrete spell-check engine (e.g. Hunspell).
/// </summary>
public interface ISpellChecker
{
    /// <summary>
    /// Whether the underlying dictionary has been loaded and the service is operational.
    /// When false, checks that depend on spelling should gracefully skip.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Checks whether a single word is spelled correctly.
    /// </summary>
    /// <param name="word">The word to check.</param>
    /// <returns>True if the word is correct or unrecognised (fail-open); false if misspelled.</returns>
    bool Check(string word);

    /// <summary>
    /// Extracts individual words from <paramref name="text"/> and returns those
    /// that the dictionary considers misspelled.
    /// </summary>
    /// <param name="text">Full caption text to scan.</param>
    /// <returns>Distinct misspelled words found in the text (case-preserved).</returns>
    IReadOnlyList<string> FindMisspelledWords(string text);
}
