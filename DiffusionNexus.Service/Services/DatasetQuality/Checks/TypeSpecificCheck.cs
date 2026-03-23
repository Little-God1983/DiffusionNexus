using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality.Dictionaries;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Type-specific dataset quality check that applies different rules based on
/// <see cref="DatasetConfig.LoraType"/>.
/// <list type="bullet">
/// <item><b>Character</b>: warns when clothing or pose categories show zero diversity
///   (same outfit / same pose in every caption).</item>
/// <item><b>Concept</b>: warns when viewpoint/camera-angle never varies.</item>
/// <item><b>Style</b>: flags style-describing words that must be removed so the
///   style bakes in implicitly; warns when content/subject diversity is too low.</item>
/// </list>
/// Applies to all LoRA types, with different behavior per type. Runs after
/// the feature-consistency check (Order&nbsp;=&nbsp;5).
/// </summary>
public class TypeSpecificCheck : IDatasetCheck
{
    /// <summary>
    /// Minimum number of captions required for diversity analysis to be meaningful.
    /// </summary>
    internal const int MinCaptionsForAnalysis = 3;

    /// <summary>
    /// Minimum ratio of unique token sets to total captions before flagging low
    /// content diversity (Style LoRA). A value of 0.40 means at least 40% of
    /// captions should have distinct subjects.
    /// </summary>
    internal const double MinContentDiversityRatio = 0.40;

    /// <summary>
    /// Feature categories used to detect clothing diversity for Character LoRAs.
    /// </summary>
    private static readonly string[] CharacterClothingCategories =
        ["clothing_upper", "clothing_lower", "clothing_full", "clothing_footwear"];

    /// <inheritdoc />
    public string Name => "Type-Specific";

    /// <inheritdoc />
    public string Description =>
        "Applies LoRA-type-specific rules: clothing/pose diversity for Character, " +
        "viewpoint diversity for Concept, and style-leak detection for Style.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 5;

    /// <inheritdoc />
    public bool IsApplicable(LoraType loraType) => true;

    /// <inheritdoc />
    public List<Issue> Run(IReadOnlyList<CaptionFile> captions, DatasetConfig config)
    {
        ArgumentNullException.ThrowIfNull(captions);
        ArgumentNullException.ThrowIfNull(config);

        if (captions.Count < MinCaptionsForAnalysis)
            return [];

        return config.LoraType switch
        {
            LoraType.Character => AnalyzeCharacter(captions),
            LoraType.Concept => AnalyzeConcept(captions),
            LoraType.Style => AnalyzeStyle(captions),
            _ => []
        };
    }

    #region Character Analysis

    /// <summary>
    /// Character LoRA: warn if outfit or pose never varies across captions.
    /// </summary>
    private List<Issue> AnalyzeCharacter(IReadOnlyList<CaptionFile> captions)
    {
        var issues = new List<Issue>();

        CheckCategoryDiversity(captions, CharacterClothingCategories, "clothing",
            "Character LoRAs need outfit variety so the model doesn't permanently associate " +
            "a single outfit with the character. Include multiple outfits across captions.",
            issues);

        CheckCategoryDiversity(captions, ["pose"], "pose",
            "Character LoRAs need pose variety so the model learns the character in " +
            "different positions. Include varied poses across captions.",
            issues);

        return issues;
    }

    /// <summary>
    /// Checks whether a set of feature categories shows any diversity across captions.
    /// If every caption that mentions a term from those categories uses the exact same
    /// term (or no caption uses any term), a Warning is raised.
    /// </summary>
    private void CheckCategoryDiversity(
        IReadOnlyList<CaptionFile> captions,
        IReadOnlyList<string> categoryNames,
        string humanLabel,
        string details,
        List<Issue> issues)
    {
        var termsFound = CollectDistinctTerms(captions, categoryNames);

        if (termsFound.Count <= 1 && HasAnyCategoryTermPresent(captions, categoryNames))
        {
            var singleTerm = termsFound.Count == 1 ? termsFound.First() : null;
            var message = singleTerm is not null
                ? $"No {humanLabel} diversity — \"{singleTerm}\" is the only {humanLabel} term across all captions."
                : $"No {humanLabel} diversity detected across captions.";

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = message,
                Details = details,
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = captions.Select(c => c.FilePath).ToList()
            });
        }
    }

    /// <summary>
    /// Collects all distinct feature terms from the given categories that appear
    /// across any caption.
    /// </summary>
    private static HashSet<string> CollectDistinctTerms(
        IReadOnlyList<CaptionFile> captions,
        IReadOnlyList<string> categoryNames)
    {
        var distinctTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var categoryName in categoryNames)
        {
            if (!FeatureCategories.Categories.TryGetValue(categoryName, out var terms))
                continue;

            foreach (var term in terms)
            {
                foreach (var caption in captions)
                {
                    if (string.IsNullOrWhiteSpace(caption.RawText))
                        continue;

                    if (TextHelpers.ContainsFeature(caption.RawText, term, caption.DetectedStyle))
                    {
                        distinctTerms.Add(term);
                        break; // Found at least one caption with this term, move to next term
                    }
                }
            }
        }

        return distinctTerms;
    }

    /// <summary>
    /// Returns true if at least one caption contains a term from the specified categories.
    /// </summary>
    private static bool HasAnyCategoryTermPresent(
        IReadOnlyList<CaptionFile> captions,
        IReadOnlyList<string> categoryNames)
    {
        foreach (var categoryName in categoryNames)
        {
            if (!FeatureCategories.Categories.TryGetValue(categoryName, out var terms))
                continue;

            foreach (var term in terms)
            {
                foreach (var caption in captions)
                {
                    if (string.IsNullOrWhiteSpace(caption.RawText))
                        continue;

                    if (TextHelpers.ContainsFeature(caption.RawText, term, caption.DetectedStyle))
                        return true;
                }
            }
        }

        return false;
    }

    #endregion

    #region Concept Analysis

    /// <summary>
    /// Concept LoRA: warn if viewpoint/camera-angle never varies across captions.
    /// </summary>
    private List<Issue> AnalyzeConcept(IReadOnlyList<CaptionFile> captions)
    {
        var issues = new List<Issue>();

        CheckCategoryDiversity(captions, ["camera_angle"], "viewpoint/angle",
            "Concept LoRAs benefit from varied camera angles so the model learns the " +
            "concept from multiple perspectives. Include different angles and shot types.",
            issues);

        return issues;
    }

    #endregion

    #region Style Analysis

    /// <summary>
    /// Style LoRA: flag style-leak words that must be removed; warn on low content diversity.
    /// </summary>
    private List<Issue> AnalyzeStyle(IReadOnlyList<CaptionFile> captions)
    {
        var issues = new List<Issue>();

        DetectStyleLeakWords(captions, issues);
        CheckContentDiversity(captions, issues);

        return issues;
    }

    /// <summary>
    /// Scans all captions for <see cref="StyleLeakWords"/> and raises a Critical issue
    /// for each term found, with fix suggestions to remove them.
    /// </summary>
    private void DetectStyleLeakWords(IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        // Track which terms have been flagged to avoid duplicates
        var flaggedTerms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in StyleLeakWords.Set)
        {
            var presentIndices = new List<int>();

            for (var i = 0; i < captions.Count; i++)
            {
                if (string.IsNullOrWhiteSpace(captions[i].RawText))
                    continue;

                if (TextHelpers.ContainsFeature(captions[i].RawText, term, captions[i].DetectedStyle))
                    presentIndices.Add(i);
            }

            if (presentIndices.Count == 0)
                continue;

            if (!flaggedTerms.Add(term))
                continue;

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

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"Style-leak word \"{term}\" found in {presentIndices.Count}/{captions.Count} caption(s) " +
                          "— must be removed for Style LoRA.",
                Details = $"The term \"{term}\" describes artistic style or quality. In a Style LoRA, " +
                          "these words become entangled with the style itself, preventing the model from " +
                          "learning the style cleanly. Remove all style-describing words so the style " +
                          "bakes in implicitly through the training images.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = presentIndices.Select(i => captions[i].FilePath).ToList(),
                FixSuggestions =
                [
                    new FixSuggestion
                    {
                        Description = $"Remove \"{term}\" from {presentIndices.Count} caption(s).",
                        Edits = removeEdits
                    }
                ]
            });
        }
    }

    /// <summary>
    /// Checks whether the caption subjects are diverse enough for style training.
    /// Style LoRAs need varied subjects so the model learns the style, not the content.
    /// Computes the ratio of unique token-set signatures to total captions.
    /// </summary>
    private void CheckContentDiversity(IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var caption in captions)
        {
            if (string.IsNullOrWhiteSpace(caption.RawText))
                continue;

            // Build a normalized signature from the sorted unique tokens
            var tokens = TextHelpers.ExtractTokens(caption.RawText);
            var signature = string.Join("|", tokens.Distinct(StringComparer.OrdinalIgnoreCase).Order());
            signatures.Add(signature);
        }

        var nonEmptyCaptions = captions.Count(c => !string.IsNullOrWhiteSpace(c.RawText));
        if (nonEmptyCaptions == 0)
            return;

        var diversityRatio = (double)signatures.Count / nonEmptyCaptions;

        if (diversityRatio < MinContentDiversityRatio)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"Low content diversity — only {signatures.Count} unique subject(s) " +
                          $"across {nonEmptyCaptions} captions ({signatures.Count * 100 / nonEmptyCaptions}%).",
                Details = "Style LoRAs need varied subjects (different scenes, objects, compositions) " +
                          "so the model learns the artistic style rather than specific content. " +
                          "Consider adding images with different subjects while maintaining the same style.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = captions.Select(c => c.FilePath).ToList()
            });
        }
    }

    #endregion
}
