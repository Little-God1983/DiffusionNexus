using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Checks for inconsistent use of synonyms across captions.
/// <para>
/// For each <see cref="SynonymGroups"/> entry, counts how many captions use each
/// synonym. When two or more synonyms from the same group appear in the dataset,
/// a <see cref="IssueSeverity.Warning"/> is raised with fix suggestions to
/// standardize on any of the used terms.
/// </para>
/// Applies to all LoRA types. Runs after trigger-word checks (Order = 3).
/// </summary>
public class SynonymConsistencyCheck : IDatasetCheck
{
    /// <inheritdoc />
    public string Name => "Synonym Consistency";

    /// <inheritdoc />
    public string Description =>
        "Detects inconsistent use of synonyms across captions and suggests standardizing to a single term.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 3;

    /// <inheritdoc />
    public bool IsApplicable(LoraType loraType) => true;

    /// <inheritdoc />
    public List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(config);

        var issues = new List<Issue>();

        if (captions.Count == 0)
            return issues;

        foreach (var group in SynonymGroups.Groups)
        {
            var conflict = AnalyzeGroup(group, captions);

            if (conflict.UsedTerms.Count < 2)
                continue;

            issues.Add(BuildIssue(conflict, captions));
        }

        return issues;
    }

    /// <summary>
    /// Scans all captions for occurrences of each term in a synonym group.
    /// Returns which terms were found and which files use each term.
    /// </summary>
    internal static SynonymConflict AnalyzeGroup(
        HashSet<string> group, IReadOnlyList<CaptionFile> captions)
    {
        // term → list of (caption index, file path) that contain it
        var termHits = new Dictionary<string, List<TermHit>>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in group)
        {
            for (var i = 0; i < captions.Count; i++)
            {
                var caption = captions[i];
                if (string.IsNullOrWhiteSpace(caption.RawText))
                    continue;

                if (TextHelpers.ContainsFeature(caption.RawText, term, caption.DetectedStyle))
                {
                    if (!termHits.TryGetValue(term, out var hits))
                    {
                        hits = [];
                        termHits[term] = hits;
                    }
                    hits.Add(new TermHit(i, caption.FilePath));
                }
            }
        }

        // Sort terms by usage count descending (most-used first)
        var sorted = termHits
            .OrderByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SynonymConflict
        {
            UsedTerms = sorted.Select(kv => kv.Key).ToList(),
            HitsByTerm = sorted.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>
    /// Builds the issue with one <see cref="FixSuggestion"/> per possible standardization target.
    /// </summary>
    private Issue BuildIssue(SynonymConflict conflict, IReadOnlyList<CaptionFile> captions)
    {
        // Build human-readable summary: "car"(7x) vs "automobile"(2x) vs "vehicle"(1x)
        var termSummary = string.Join(
            " vs ",
            conflict.UsedTerms.Select(t => $"\"{t}\"({conflict.HitsByTerm[t].Count}x)"));

        // Collect all affected files (union of files across all terms)
        var allAffectedFiles = conflict.HitsByTerm.Values
            .SelectMany(hits => hits.Select(h => h.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // One fix suggestion per possible target term
        var fixSuggestions = new List<FixSuggestion>();

        foreach (var targetTerm in conflict.UsedTerms)
        {
            var edits = BuildEditsForTarget(targetTerm, conflict, captions);

            if (edits.Count > 0)
            {
                fixSuggestions.Add(new FixSuggestion
                {
                    Description = $"Replace all with \"{targetTerm}\".",
                    Edits = edits
                });
            }
        }

        return new Issue
        {
            Severity = IssueSeverity.Warning,
            Message = $"Synonym conflict: {termSummary}.",
            Details = "Using different words for the same concept across captions "
                    + "fragments the learned association during training. "
                    + "Standardizing to a single term improves consistency.",
            Domain = CheckDomain.Caption,
            CheckName = Name,
            AffectedFiles = allAffectedFiles,
            FixSuggestions = fixSuggestions
        };
    }

    /// <summary>
    /// For a given target term, builds <see cref="FileEdit"/>s that replace every
    /// other synonym with the target in affected captions.
    /// Hits are grouped by caption index so each file produces at most one edit,
    /// even when the caption contains multiple non-target synonyms.
    /// </summary>
    private static List<FileEdit> BuildEditsForTarget(
        string targetTerm,
        SynonymConflict conflict,
        IReadOnlyList<CaptionFile> captions)
    {
        // Group all non-target term hits by caption index → one edit per file
        var termsByCaptionIndex = new Dictionary<int, List<string>>();

        foreach (var (term, hits) in conflict.HitsByTerm)
        {
            if (term.Equals(targetTerm, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var hit in hits)
            {
                if (!termsByCaptionIndex.TryGetValue(hit.CaptionIndex, out var terms))
                {
                    terms = [];
                    termsByCaptionIndex[hit.CaptionIndex] = terms;
                }

                terms.Add(term);
            }
        }

        var edits = new List<FileEdit>();

        foreach (var (captionIndex, termsToRemove) in termsByCaptionIndex)
        {
            var caption = captions[captionIndex];
            var newText = caption.RawText;

            if (caption.DetectedStyle == CaptionStyle.BooruTags)
            {
                // BooruTags: replace all non-target synonym tags in one pass
                var tags = TextHelpers.SplitTags(newText);
                var replaced = tags.Select(t =>
                    termsToRemove.Any(r => r.Equals(t, StringComparison.OrdinalIgnoreCase))
                        ? targetTerm
                        : t);

                // Deduplicate consecutive target terms that may result from multiple replacements
                newText = string.Join(", ", replaced.Distinct(StringComparer.OrdinalIgnoreCase));
            }
            else
            {
                // NL / Mixed: replace the first non-target term in-place, remove the rest
                var hasTarget = TextHelpers.ContainsFeature(newText, targetTerm, caption.DetectedStyle);
                var replacedInPlace = false;

                foreach (var term in termsToRemove)
                {
                    if (!hasTarget && !replacedInPlace)
                    {
                        newText = TextHelpers.ReplacePhrase(newText, term, targetTerm, caption.DetectedStyle);
                        replacedInPlace = true;
                    }
                    else
                    {
                        newText = TextHelpers.RemovePhrase(newText, term, caption.DetectedStyle);
                    }
                }
            }

            edits.Add(new FileEdit
            {
                FilePath = caption.FilePath,
                OriginalText = caption.RawText,
                NewText = newText
            });
        }

        return edits;
    }

    /// <summary>
    /// Style-aware term replacement: replaces one synonym with another,
    /// preserving the caption's formatting style.
    /// </summary>
    internal static string ReplaceTerm(
        string rawText, string oldTerm, string newTerm, CaptionStyle style)
    {
        if (string.IsNullOrEmpty(rawText))
            return newTerm;

        if (style == CaptionStyle.BooruTags)
        {
            var tags = TextHelpers.SplitTags(rawText);
            var replaced = tags.Select(t =>
                t.Equals(oldTerm, StringComparison.OrdinalIgnoreCase) ? newTerm : t);
            return string.Join(", ", replaced);
        }

        // NL / Mixed / Unknown — in-place word-boundary replacement
        return TextHelpers.ReplacePhrase(rawText, oldTerm, newTerm, style);
    }

    /// <summary>
    /// Tracks a single occurrence of a synonym in a specific caption.
    /// </summary>
    internal sealed record TermHit(int CaptionIndex, string FilePath);

    /// <summary>
    /// Result of analyzing a single synonym group across all captions.
    /// </summary>
    internal sealed record SynonymConflict
    {
        /// <summary>Terms that were actually found, sorted by usage count descending.</summary>
        public required List<string> UsedTerms { get; init; }

        /// <summary>Per-term hit list.</summary>
        public required Dictionary<string, List<TermHit>> HitsByTerm { get; init; }
    }
}
