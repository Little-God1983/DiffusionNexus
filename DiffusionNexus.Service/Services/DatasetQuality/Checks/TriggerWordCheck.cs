using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Checks that the configured trigger word is used correctly across all captions.
/// <list type="bullet">
/// <item><b>Missing trigger</b>: caption does not contain the trigger word → Critical + fix (prepend)</item>
/// <item><b>Case mismatch</b>: trigger found but with wrong casing (e.g. "SKS" vs "sks") → Critical + fix</item>
/// <item><b>Position inconsistency</b>: most captions have the trigger at position N but some differ → Info</item>
/// <item><b>Duplicate trigger</b>: trigger appears more than once in the same caption → Warning</item>
/// </list>
/// Applies to Character and Concept LoRAs. Style LoRAs typically do not use a trigger word.
/// Skips entirely when <see cref="DatasetConfig.TriggerWord"/> is null or empty.
/// </summary>
public class TriggerWordCheck : IDatasetCheck
{
    /// <inheritdoc />
    public string Name => "Trigger Word";

    /// <inheritdoc />
    public string Description =>
        "Validates that the configured trigger word is present, correctly cased, consistently positioned, and not duplicated.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 2;

    /// <inheritdoc />
    public bool IsApplicable(LoraType loraType) =>
        loraType is LoraType.Character or LoraType.Concept;

    /// <inheritdoc />
    public List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(config);

        var issues = new List<Issue>();

        // Nothing to check when there is no trigger word or no captions
        if (string.IsNullOrWhiteSpace(config.TriggerWord) || captions.Count == 0)
            return issues;

        var trigger = config.TriggerWord;
        var captionResults = AnalyzeCaptions(captions, trigger);

        CheckMissing(captionResults, trigger, issues);
        CheckCaseMismatch(captionResults, trigger, issues);
        CheckDuplicate(captionResults, trigger, issues);
        CheckPositionInconsistency(captionResults, trigger, issues);

        return issues;
    }

    /// <summary>
    /// Pre-analyzes every caption for trigger word presence, casing, position, and count.
    /// </summary>
    private static List<TriggerAnalysis> AnalyzeCaptions(
        IReadOnlyList<CaptionFile> captions, string trigger)
    {
        var results = new List<TriggerAnalysis>(captions.Count);

        foreach (var caption in captions)
        {
            var tokens = Tokenize(caption.RawText, caption.DetectedStyle);
            var exactPositions = new List<int>();
            var caseInsensitivePositions = new List<int>();

            for (var i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Equals(trigger, StringComparison.Ordinal))
                {
                    exactPositions.Add(i);
                }
                else if (tokens[i].Equals(trigger, StringComparison.OrdinalIgnoreCase))
                {
                    caseInsensitivePositions.Add(i);
                }
            }

            results.Add(new TriggerAnalysis
            {
                Caption = caption,
                Tokens = tokens,
                ExactMatchPositions = exactPositions,
                CaseInsensitiveOnlyPositions = caseInsensitivePositions
            });
        }

        return results;
    }

    /// <summary>
    /// Flags captions that do not contain the trigger word at all (not even wrong case).
    /// Provides a fix suggestion to prepend the trigger.
    /// </summary>
    private void CheckMissing(
        List<TriggerAnalysis> results, string trigger, List<Issue> issues)
    {
        var missingFiles = new List<string>();
        var edits = new List<FileEdit>();

        foreach (var r in results)
        {
            if (r.ExactMatchPositions.Count == 0 && r.CaseInsensitiveOnlyPositions.Count == 0)
            {
                missingFiles.Add(r.Caption.FilePath);
                edits.Add(new FileEdit
                {
                    FilePath = r.Caption.FilePath,
                    OriginalText = r.Caption.RawText,
                    NewText = PrependTrigger(r.Caption.RawText, trigger, r.Caption.DetectedStyle)
                });
            }
        }

        if (missingFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{missingFiles.Count} caption(s) are missing the trigger word \"{trigger}\".",
                Details = "The trigger word must appear in every caption so the model learns to "
                        + "associate it with the trained concept. Captions without it will dilute training.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = missingFiles,
                FixSuggestions =
                [
                    new FixSuggestion
                    {
                        Description = $"Prepend \"{trigger}\" to each affected caption.",
                        Edits = edits
                    }
                ]
            });
        }
    }

    /// <summary>
    /// Flags captions where the trigger is present but with wrong casing.
    /// Provides a fix suggestion to correct the case.
    /// </summary>
    private void CheckCaseMismatch(
        List<TriggerAnalysis> results, string trigger, List<Issue> issues)
    {
        var mismatchFiles = new List<string>();
        var edits = new List<FileEdit>();

        foreach (var r in results)
        {
            // Only flag if there are case-insensitive matches but no exact matches
            if (r.ExactMatchPositions.Count == 0 && r.CaseInsensitiveOnlyPositions.Count > 0)
            {
                mismatchFiles.Add(r.Caption.FilePath);
                var corrected = ReplaceCaseMismatch(
                    r.Caption.RawText, trigger, r.Caption.DetectedStyle);
                edits.Add(new FileEdit
                {
                    FilePath = r.Caption.FilePath,
                    OriginalText = r.Caption.RawText,
                    NewText = corrected
                });
            }
        }

        if (mismatchFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{mismatchFiles.Count} caption(s) have the trigger word with incorrect casing.",
                Details = $"The trigger word \"{trigger}\" was found with different casing. "
                        + "Trigger words are case-sensitive during training — inconsistent casing "
                        + "fragments the learned association.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = mismatchFiles,
                FixSuggestions =
                [
                    new FixSuggestion
                    {
                        Description = $"Correct trigger word casing to \"{trigger}\".",
                        Edits = edits
                    }
                ]
            });
        }
    }

    /// <summary>
    /// Flags captions that contain the trigger word more than once.
    /// </summary>
    private void CheckDuplicate(
        List<TriggerAnalysis> results, string trigger, List<Issue> issues)
    {
        var duplicateFiles = new List<string>();

        foreach (var r in results)
        {
            var totalOccurrences = r.ExactMatchPositions.Count + r.CaseInsensitiveOnlyPositions.Count;
            if (totalOccurrences > 1)
            {
                duplicateFiles.Add(r.Caption.FilePath);
            }
        }

        if (duplicateFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{duplicateFiles.Count} caption(s) contain the trigger word \"{trigger}\" more than once.",
                Details = "Repeating the trigger word in a caption over-emphasizes it and can "
                        + "cause artifacts during inference. Each caption should contain the trigger exactly once.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = duplicateFiles
            });
        }
    }

    /// <summary>
    /// Flags inconsistent trigger word positioning across captions.
    /// Only reported as Info — the user may have intentional reasons.
    /// </summary>
    private void CheckPositionInconsistency(
        List<TriggerAnalysis> results, string trigger, List<Issue> issues)
    {
        // Collect the first occurrence position for captions that have an exact match
        var positionMap = new Dictionary<int, List<string>>();

        foreach (var r in results)
        {
            // Use the first exact match position; fall back to first case-insensitive
            int? position = r.ExactMatchPositions.Count > 0
                ? r.ExactMatchPositions[0]
                : r.CaseInsensitiveOnlyPositions.Count > 0
                    ? r.CaseInsensitiveOnlyPositions[0]
                    : null;

            if (position is null)
                continue;

            if (!positionMap.TryGetValue(position.Value, out var fileList))
            {
                fileList = [];
                positionMap[position.Value] = fileList;
            }
            fileList.Add(r.Caption.FilePath);
        }

        // Need at least 2 different positions to have inconsistency
        if (positionMap.Count < 2)
            return;

        // Find the majority position
        var majorityPosition = positionMap
            .OrderByDescending(kv => kv.Value.Count)
            .First();

        // All files NOT in the majority position are inconsistent
        var inconsistentFiles = positionMap
            .Where(kv => kv.Key != majorityPosition.Key)
            .SelectMany(kv => kv.Value)
            .ToList();

        if (inconsistentFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Info,
                Message = $"Trigger word \"{trigger}\" position varies across captions "
                        + $"(majority at position {majorityPosition.Key}).",
                Details = $"Most captions ({majorityPosition.Value.Count}) have the trigger word "
                        + $"at token position {majorityPosition.Key}, but {inconsistentFiles.Count} "
                        + "caption(s) have it elsewhere. Consistent positioning helps the model "
                        + "learn more predictably.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = inconsistentFiles
            });
        }
    }

    #region Internal Helpers

    /// <summary>
    /// Tokenizes a caption into words/tags based on its detected style.
    /// </summary>
    internal static List<string> Tokenize(string rawText, CaptionStyle style)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return [];

        if (style == CaptionStyle.BooruTags)
        {
            return TextHelpers.SplitTags(rawText);
        }

        // NL / Mixed / Unknown — whitespace split
        return rawText
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
    }

    /// <summary>
    /// Prepends the trigger word to a caption in a style-aware manner.
    /// </summary>
    internal static string PrependTrigger(string rawText, string trigger, CaptionStyle style)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return trigger;

        if (style == CaptionStyle.BooruTags)
        {
            return $"{trigger}, {rawText.TrimStart()}";
        }

        // NL / Mixed / Unknown — prepend with space
        return $"{trigger} {rawText.TrimStart()}";
    }

    /// <summary>
    /// Replaces all case-insensitive occurrences of the trigger with the correct casing.
    /// </summary>
    internal static string ReplaceCaseMismatch(
        string rawText, string trigger, CaptionStyle style)
    {
        if (string.IsNullOrWhiteSpace(rawText))
            return trigger;

        if (style == CaptionStyle.BooruTags)
        {
            var tags = TextHelpers.SplitTags(rawText);
            var corrected = tags.Select(t =>
                t.Equals(trigger, StringComparison.OrdinalIgnoreCase) ? trigger : t);
            return string.Join(", ", corrected);
        }

        // NL / Mixed / Unknown — word-by-word replacement
        var words = rawText.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            if (words[i].Equals(trigger, StringComparison.OrdinalIgnoreCase))
            {
                words[i] = trigger;
            }
        }
        return string.Join(' ', words);
    }

    #endregion

    /// <summary>
    /// Per-caption analysis result used internally.
    /// </summary>
    private sealed record TriggerAnalysis
    {
        public required CaptionFile Caption { get; init; }
        public required List<string> Tokens { get; init; }
        public required List<int> ExactMatchPositions { get; init; }
        public required List<int> CaseInsensitiveOnlyPositions { get; init; }
    }
}
