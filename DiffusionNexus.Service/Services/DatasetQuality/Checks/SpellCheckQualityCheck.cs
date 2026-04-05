using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Runs spell checking on each caption and reports misspelled words as warnings.
/// <list type="bullet">
/// <item>Groups misspelled words per caption file.</item>
/// <item>Reports a single aggregated Warning issue listing all misspelled words
///   and which files they appear in.</item>
/// <item>Skips booru-tag captions (unconventional tokens are expected).</item>
/// </list>
/// Depends on <see cref="ISpellChecker"/> — when the dictionary is unavailable
/// the check silently returns no issues.
/// Runs last (Order = 6) because it is informational and does not affect other checks.
/// </summary>
public class SpellCheckQualityCheck : IDatasetCheck
{
    private readonly ISpellChecker _spellChecker;

    /// <summary>
    /// Creates a new <see cref="SpellCheckQualityCheck"/>.
    /// </summary>
    /// <param name="spellChecker">Spell-checking service to use.</param>
    public SpellCheckQualityCheck(ISpellChecker spellChecker)
    {
        ArgumentNullException.ThrowIfNull(spellChecker);
        _spellChecker = spellChecker;
    }

    /// <inheritdoc />
    public string Name => "Spell Check";

    /// <inheritdoc />
    public string Description =>
        "Checks captions for potential spelling errors and lists the suspect words.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 6;

    /// <inheritdoc />
    public bool IsApplicable(LoraType loraType) => true;

    /// <inheritdoc />
    public List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(config);

        var issues = new List<Issue>();

        if (!_spellChecker.IsReady || captions.Count == 0)
            return issues;

        // Collect misspelled words per file and across the whole dataset
        var allMisspelled = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var affectedFiles = new List<string>();

        foreach (var caption in captions)
        {
            // Skip booru-tag captions — unconventional tokens are expected
            if (caption.DetectedStyle == CaptionStyle.BooruTags)
                continue;

            if (string.IsNullOrWhiteSpace(caption.RawText))
                continue;

            var misspelled = _spellChecker.FindMisspelledWords(caption.RawText);
            if (misspelled.Count == 0)
                continue;

            affectedFiles.Add(caption.FilePath);

            foreach (var word in misspelled)
            {
                if (!allMisspelled.TryGetValue(word, out var files))
                {
                    files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    allMisspelled[word] = files;
                }
                files.Add(caption.FilePath);
            }
        }

        if (allMisspelled.Count == 0)
            return issues;

        // Build a readable details string listing each misspelled word and occurrence count
        var wordSummaries = allMisspelled
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => kv.Value.Count == 1
                ? $"\"{kv.Key}\""
                : $"\"{kv.Key}\" ({kv.Value.Count} files)")
            .ToList();

        var wordList = string.Join(", ", wordSummaries);

        issues.Add(new Issue
        {
            Severity = IssueSeverity.Warning,
            Message = $"Spell Check: {allMisspelled.Count} potentially misspelled word(s) found.",
            Details = $"The following words may be misspelled: {wordList}. "
                    + "If these are intentional (proper nouns, technical terms, etc.), "
                    + "consider adding them to your custom dictionary.",
            Domain = CheckDomain.Caption,
            CheckName = Name,
            AffectedFiles = affectedFiles.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        });

        return issues;
    }
}
