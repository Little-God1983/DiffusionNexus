using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Analyzes whether features appear at a constant or variable rate across captions.
/// <para>
/// Core principle: a feature appearing in 80–99% of captions is <b>worse</b> than
/// 0% or 100% because it teaches the model contradictory associations.
/// </para>
/// <list type="bullet">
/// <item><b>Known features</b> (from <see cref="FeatureCategories"/>):
///   80–99% → Critical + fix; 100% → Info (consider baking into trigger).</item>
/// <item><b>Discovered n-grams</b> (bigrams/trigrams by frequency):
///   80–99% → Warning (not in known features).</item>
/// </list>
/// Applies to all LoRA types. Runs after synonym checks (Order = 4).
/// </summary>
public class FeatureConsistencyCheck : IDatasetCheck
{
    /// <summary>
    /// Lower bound of the "near-constant" danger zone (inclusive).
    /// A feature present in this fraction or more of captions, but not all, is problematic.
    /// </summary>
    internal const double NearConstantLowerBound = 0.80;

    /// <summary>
    /// Minimum number of captions required for the analysis to be meaningful.
    /// </summary>
    internal const int MinCaptionsForAnalysis = 3;

    /// <inheritdoc />
    public string Name => "Feature Consistency";

    /// <inheritdoc />
    public string Description =>
        "Detects features that appear in most but not all captions (80-99%), " +
        "which teaches contradictory associations, and features present in 100% " +
        "that could be baked into the trigger word.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 4;

    /// <inheritdoc />
    public bool IsApplicable(LoraType loraType) => true;

    /// <inheritdoc />
    public List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(config);

        var issues = new List<Issue>();

        if (captions.Count < MinCaptionsForAnalysis)
            return issues;

        var knownTermsCovered = AnalyzeKnownFeatures(captions, issues);
        AnalyzeDiscoveredNgrams(captions, knownTermsCovered, issues);

        return issues;
    }

    #region Known Features (Sub-analysis A)

    /// <summary>
    /// Iterates <see cref="FeatureCategories"/> and checks per-term presence.
    /// Returns the set of known terms that were found, so n-gram analysis can skip them.
    /// </summary>
    private HashSet<string> AnalyzeKnownFeatures(
        IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        var coveredTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = captions.Count;

        foreach (var (category, terms) in FeatureCategories.Categories)
        {
            foreach (var term in terms)
            {
                var hits = FindCaptionsContaining(captions, term);

                if (hits.Count == 0)
                    continue;

                coveredTerms.Add(term);

                var ratio = (double)hits.Count / total;
                var missingIndices = FindCaptionsMissing(captions, term);

                if (ratio >= NearConstantLowerBound && hits.Count < total)
                {
                    // 80-99%: Critical — near-constant is the worst zone
                    issues.Add(BuildNearConstantIssue(
                        term, category, hits, missingIndices, captions, total));
                }
                else if (hits.Count == total)
                {
                    // 100%: Info — consider baking into trigger
                    issues.Add(BuildFullyCoveredIssue(term, category, captions, total));
                }
            }
        }

        return coveredTerms;
    }

    /// <summary>
    /// Builds a Critical issue for a known feature in the 80–99% range.
    /// Provides two fix options: add to missing captions, or remove from all.
    /// </summary>
    private Issue BuildNearConstantIssue(
        string term,
        string category,
        List<int> presentIndices,
        List<int> missingIndices,
        IReadOnlyList<CaptionFile> captions,
        int total)
    {
        var missingFiles = missingIndices.Select(i => captions[i].FilePath).ToList();
        var presentFiles = presentIndices.Select(i => captions[i].FilePath).ToList();

        // Fix option 1: Add the term to all missing captions → 100%
        var addEdits = missingIndices.Select(i =>
        {
            var c = captions[i];
            return new FileEdit
            {
                FilePath = c.FilePath,
                OriginalText = c.RawText,
                NewText = TextHelpers.AppendPhrase(c.RawText, term, c.DetectedStyle)
            };
        }).ToList();

        // Fix option 2: Remove the term from all present captions → 0%
        var removeEdits = presentIndices.Select(i =>
        {
            var c = captions[i];
            return new FileEdit
            {
                FilePath = c.FilePath,
                OriginalText = c.RawText,
                NewText = TextHelpers.RemovePhrase(c.RawText, term, c.DetectedStyle)
            };
        }).ToList();

        var fixes = new List<FixSuggestion>
        {
            new()
            {
                Description = $"Add \"{term}\" to {missingIndices.Count} missing caption(s) ({total}/{total}).",
                Edits = addEdits
            },
            new()
            {
                Description = $"Remove \"{term}\" from all {presentIndices.Count} caption(s) (0/{total}, bakes into trigger).",
                Edits = removeEdits
            }
        };

        return new Issue
        {
            Severity = IssueSeverity.Critical,
            Message = $"\"{term}\" ({category}) in {presentIndices.Count}/{total} captions " +
                      $"— should be {total}/{total} or 0/{total}.",
            Details = $"The feature \"{term}\" appears in {presentIndices.Count} of {total} captions " +
                      $"({presentIndices.Count * 100 / total}%). A feature present in most but not all " +
                      "captions teaches contradictory associations — the model cannot decide whether " +
                      "this feature belongs to the concept or not. Either include it everywhere " +
                      "or remove it entirely (baking it into the trigger word).",
            Domain = CheckDomain.Caption,
            CheckName = Name,
            AffectedFiles = missingFiles,
            FixSuggestions = fixes
        };
    }

    /// <summary>
    /// Builds an Info issue for a known feature at exactly 100%.
    /// </summary>
    private Issue BuildFullyCoveredIssue(
        string term,
        string category,
        IReadOnlyList<CaptionFile> captions,
        int total)
    {
        var allFiles = captions.Select(c => c.FilePath).ToList();

        // Single fix: remove from all to bake into trigger
        var removeEdits = captions.Select(c => new FileEdit
        {
            FilePath = c.FilePath,
            OriginalText = c.RawText,
            NewText = TextHelpers.RemovePhrase(c.RawText, term, c.DetectedStyle)
        }).ToList();

        return new Issue
        {
            Severity = IssueSeverity.Info,
            Message = $"\"{term}\" ({category}) in {total}/{total} captions " +
                      "— consider removing to bake into trigger.",
            Details = $"The feature \"{term}\" appears in every caption. If this is an inherent " +
                      "property of the trained concept, it can be removed from captions and " +
                      "baked into the trigger word. This reduces caption noise and lets the model " +
                      "learn the association more cleanly through the trigger alone.",
            Domain = CheckDomain.Caption,
            CheckName = Name,
            AffectedFiles = allFiles,
            FixSuggestions =
            [
                new FixSuggestion
                {
                    Description = $"Remove \"{term}\" from all {total} caption(s) (bake into trigger).",
                    Edits = removeEdits
                }
            ]
        };
    }

    #endregion

    #region Discovered N-grams (Sub-analysis B)

    /// <summary>
    /// Extracts bigrams and trigrams from all captions, counts their frequency,
    /// and flags any in the 80–99% range that are not already covered by known features.
    /// </summary>
    private void AnalyzeDiscoveredNgrams(
        IReadOnlyList<CaptionFile> captions,
        HashSet<string> knownTermsCovered,
        List<Issue> issues)
    {
        var total = captions.Count;

        // Extract tokens per caption once
        var captionTokens = new List<IReadOnlyList<string>>(total);
        foreach (var caption in captions)
        {
            captionTokens.Add(TextHelpers.ExtractTokens(caption.RawText));
        }

        // Count n-gram presence across captions (presence = at least once in a caption)
        var ngramPresence = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < total; i++)
        {
            var tokens = captionTokens[i];
            var ngrams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var bg in TextHelpers.ExtractBigrams(tokens))
                ngrams.Add(bg);

            foreach (var tg in TextHelpers.ExtractTrigrams(tokens))
                ngrams.Add(tg);

            foreach (var ng in ngrams)
            {
                if (!ngramPresence.TryGetValue(ng, out var indices))
                {
                    indices = [];
                    ngramPresence[ng] = indices;
                }
                indices.Add(i);
            }
        }

        // Flag n-grams in the 80-99% range that are not already known features.
        // Also skip n-grams that contain a known term as a substring
        // (e.g. trigram "woman blue eyes" contains known "blue eyes").
        foreach (var (ngram, presentIndices) in ngramPresence)
        {
            if (knownTermsCovered.Contains(ngram) ||
                knownTermsCovered.Any(kt => ngram.Contains(kt, StringComparison.OrdinalIgnoreCase)))
                continue;

            var ratio = (double)presentIndices.Count / total;
            if (ratio >= NearConstantLowerBound && presentIndices.Count < total)
            {
                var missingFiles = Enumerable.Range(0, total)
                    .Except(presentIndices)
                    .Select(i => captions[i].FilePath)
                    .ToList();

                issues.Add(new Issue
                {
                    Severity = IssueSeverity.Warning,
                    Message = $"Discovered n-gram \"{ngram}\" in {presentIndices.Count}/{total} captions " +
                              $"— should be {total}/{total} or 0/{total}.",
                    Details = $"The phrase \"{ngram}\" was discovered in {presentIndices.Count} of " +
                              $"{total} captions ({presentIndices.Count * 100 / total}%). " +
                              "This near-constant pattern may confuse the model. " +
                              "Consider adding it to all captions or removing it entirely.",
                    Domain = CheckDomain.Caption,
                    CheckName = Name,
                    AffectedFiles = missingFiles
                });
            }
        }
    }

    #endregion

    #region Internal Helpers

    /// <summary>
    /// Returns indices of captions that contain the given term (style-aware).
    /// </summary>
    internal static List<int> FindCaptionsContaining(
        IReadOnlyList<CaptionFile> captions, string term)
    {
        var indices = new List<int>();
        for (var i = 0; i < captions.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(captions[i].RawText))
                continue;

            if (TextHelpers.ContainsFeature(captions[i].RawText, term, captions[i].DetectedStyle))
                indices.Add(i);
        }
        return indices;
    }

    /// <summary>
    /// Returns indices of captions that do NOT contain the given term (style-aware).
    /// </summary>
    internal static List<int> FindCaptionsMissing(
        IReadOnlyList<CaptionFile> captions, string term)
    {
        var indices = new List<int>();
        for (var i = 0; i < captions.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(captions[i].RawText) ||
                !TextHelpers.ContainsFeature(captions[i].RawText, term, captions[i].DetectedStyle))
            {
                indices.Add(i);
            }
        }
        return indices;
    }

    #endregion
}
