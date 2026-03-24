using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.Service.Services.DatasetQuality.Checks;

/// <summary>
/// Checks caption format consistency across the dataset.
/// <list type="bullet">
/// <item><b>Mixed styles</b>: Some captions use NL, others use booru tags → Warning</item>
/// <item><b>Length outliers</b>: Captions whose word count deviates &gt;2σ from the mean → Warning</item>
/// <item><b>Empty/near-empty</b>: Captions with ≤2 words → Critical</item>
/// </list>
/// Applies to all LoRA types. Runs first (Order = 1) because downstream
/// checks depend on the <see cref="CaptionFile.DetectedStyle"/> that
/// <see cref="CaptionLoader"/> has already populated.
/// </summary>
public class FormatConsistencyCheck : IDatasetCheck
{
    /// <summary>
    /// Minimum word count threshold. Captions at or below this are flagged Critical.
    /// </summary>
    internal const int NearEmptyWordThreshold = 2;

    /// <summary>
    /// Number of standard deviations from the mean beyond which a caption
    /// length is considered an outlier.
    /// </summary>
    internal const double OutlierStdDevFactor = 2.0;

    /// <inheritdoc />
    public string Name => "Format Consistency";

    /// <inheritdoc />
    public string Description =>
        "Detects mixed caption styles, length outliers, and empty or near-empty captions.";

    /// <inheritdoc />
    public CheckDomain Domain => CheckDomain.Caption;

    /// <inheritdoc />
    public int Order => 1;

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

        CheckEmptyOrNearEmpty(captions, issues);
        CheckMixedStyles(captions, issues);
        CheckLengthOutliers(captions, issues);

        return issues;
    }

    /// <summary>
    /// Flags captions with ≤2 words as Critical.
    /// </summary>
    private void CheckEmptyOrNearEmpty(IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        var emptyFiles = new List<string>();
        var nearEmptyFiles = new List<string>();

        foreach (var caption in captions)
        {
            var wordCount = CountWords(caption.RawText);

            if (wordCount == 0)
            {
                emptyFiles.Add(caption.FilePath);
            }
            else if (wordCount <= NearEmptyWordThreshold)
            {
                nearEmptyFiles.Add(caption.FilePath);
            }
        }

        if (emptyFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{emptyFiles.Count} caption(s) are completely empty.",
                Details = "Empty captions provide no guidance to the model during training. "
                        + "Every image needs a descriptive caption for effective LoRA training.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = emptyFiles
            });
        }

        if (nearEmptyFiles.Count > 0)
        {
            issues.Add(new Issue
            {
                Severity = IssueSeverity.Critical,
                Message = $"{nearEmptyFiles.Count} caption(s) have ≤{NearEmptyWordThreshold} words.",
                Details = "Very short captions lack detail and cause the model to learn poorly. "
                        + "Consider expanding them with relevant descriptions of the image content.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = nearEmptyFiles
            });
        }
    }

    /// <summary>
    /// Flags mixed caption styles (some NL, some booru) as Warning.
    /// </summary>
    private void CheckMixedStyles(IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        var styleCounts = new Dictionary<CaptionStyle, List<string>>();

        foreach (var caption in captions)
        {
            // Only count definitive styles
            if (caption.DetectedStyle is CaptionStyle.NaturalLanguage or CaptionStyle.BooruTags)
            {
                if (!styleCounts.TryGetValue(caption.DetectedStyle, out var fileList))
                {
                    fileList = [];
                    styleCounts[caption.DetectedStyle] = fileList;
                }
                fileList.Add(caption.FilePath);
            }
        }

        // Mixed styles: we have both NL and Booru files
        if (styleCounts.ContainsKey(CaptionStyle.NaturalLanguage)
            && styleCounts.ContainsKey(CaptionStyle.BooruTags))
        {
            var nlCount = styleCounts[CaptionStyle.NaturalLanguage].Count;
            var booruCount = styleCounts[CaptionStyle.BooruTags].Count;

            // Determine which is the minority style
            var minorityStyle = nlCount <= booruCount
                ? CaptionStyle.NaturalLanguage
                : CaptionStyle.BooruTags;
            var minorityFiles = styleCounts[minorityStyle];
            var majorityStyleName = minorityStyle == CaptionStyle.NaturalLanguage
                ? "booru tags"
                : "natural language";

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"Mixed caption styles detected: {nlCount} natural language, {booruCount} booru tags.",
                Details = $"Mixing caption styles confuses the model during training. "
                        + $"The majority of captions use {majorityStyleName}. Consider converting "
                        + $"the {minorityFiles.Count} minority-style caption(s) to match.",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = minorityFiles
            });
        }
    }

    /// <summary>
    /// Flags captions whose word count is &gt;2 standard deviations from the dataset mean.
    /// </summary>
    private void CheckLengthOutliers(IReadOnlyList<CaptionFile> captions, List<Issue> issues)
    {
        // Need at least 3 captions for meaningful statistics
        if (captions.Count < 3)
            return;

        var wordCounts = new int[captions.Count];
        for (var i = 0; i < captions.Count; i++)
        {
            wordCounts[i] = CountWords(captions[i].RawText);
        }

        var mean = wordCounts.Average();
        var variance = wordCounts.Sum(w => (w - mean) * (w - mean)) / wordCounts.Length;
        var stdDev = Math.Sqrt(variance);

        // If stdDev is very small, all captions are similar length — no outliers
        if (stdDev < 1.0)
            return;

        var lowerBound = mean - (OutlierStdDevFactor * stdDev);
        var upperBound = mean + (OutlierStdDevFactor * stdDev);

        var outlierFiles = new List<string>();

        for (var i = 0; i < captions.Count; i++)
        {
            // Skip already-flagged empty/near-empty captions
            if (wordCounts[i] <= NearEmptyWordThreshold)
                continue;

            if (wordCounts[i] < lowerBound || wordCounts[i] > upperBound)
            {
                outlierFiles.Add(captions[i].FilePath);
            }
        }

        if (outlierFiles.Count > 0)
        {
            var minWords = (int)Math.Max(0, Math.Ceiling(lowerBound));
            var maxWords = (int)Math.Floor(upperBound);

            issues.Add(new Issue
            {
                Severity = IssueSeverity.Warning,
                Message = $"{outlierFiles.Count} caption(s) have unusual length (>{OutlierStdDevFactor}σ from mean of {mean:F0} words).",
                Details = $"Inconsistent caption lengths cause training instability by confusing the AI about word weight. "
                        + $"Very short captions cause 'concept bleed' (binding untagged background elements to your subject). "
                        + $"Very long captions introduce 'noise', forcing the AI to divide its attention and diluting the core subject. "
                        + $"Recommended range: {minWords}–{maxWords} words (mean {mean:F0} ± {OutlierStdDevFactor}σ).",
                Domain = CheckDomain.Caption,
                CheckName = Name,
                AffectedFiles = outlierFiles,
                Metadata = new Dictionary<string, string>
                {
                    ["RecommendedMinWords"] = minWords.ToString(),
                    ["RecommendedMaxWords"] = maxWords.ToString(),
                    ["MeanWords"] = ((int)Math.Round(mean)).ToString()
                }
            });
        }
    }

    /// <summary>
    /// Counts whitespace-delimited words in a caption string.
    /// </summary>
    internal static int CountWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return 0;

        return text.Split((char[])[' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    }
}
