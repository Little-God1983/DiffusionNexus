using DiffusionNexus.Domain.Models;

namespace DiffusionNexus.Service.Services.DatasetQuality;

/// <summary>
/// Severity bucket for an image's overall quality.
/// </summary>
public enum ImageQualityVerdict
{
    /// <summary>No scores available — check did not run for this image.</summary>
    Unknown = 0,
    /// <summary>Overall score &lt; 40.</summary>
    Bad = 1,
    /// <summary>Overall score 40–64.</summary>
    Mediocre = 2,
    /// <summary>Overall score 65–79.</summary>
    Good = 3,
    /// <summary>Overall score &ge; 80.</summary>
    Excellent = 4
}

/// <summary>
/// One concrete problem detected for an image (e.g. "Blur — Laplacian variance 67").
/// </summary>
/// <param name="MetricName">Display name of the metric (e.g. "Blur").</param>
/// <param name="Score">Score for that metric (0–100).</param>
/// <param name="Description">Human-readable description of the problem.</param>
public record ImageQualityProblem(string MetricName, double Score, string Description);

/// <summary>
/// Combined verdict + per-metric problems + de-duplicated fix suggestions for one image.
/// Designed to be rendered as: headline, bullet list of problems, bullet list of fixes.
/// </summary>
public record ImageQualityAdvice
{
    /// <summary>Overall verdict.</summary>
    public required ImageQualityVerdict Verdict { get; init; }

    /// <summary>One-line summary suitable for a tooltip or row subtitle.</summary>
    public required string Headline { get; init; }

    /// <summary>Concrete problems — empty when the image is fine.</summary>
    public required IReadOnlyList<ImageQualityProblem> Problems { get; init; }

    /// <summary>Suggested fixes (de-duplicated). Empty when the image is fine.</summary>
    public required IReadOnlyList<string> SuggestedFixes { get; init; }
}

/// <summary>
/// Pure-logic helper that converts a <see cref="PerImageQualitySummary"/> into a
/// user-facing <see cref="ImageQualityAdvice"/>: a headline, a list of problems,
/// and a de-duplicated list of suggested fixes.
/// </summary>
public static class ImageQualityAdvisor
{
    // Score bands. Mirrors ImageQualityItemViewModel.ScoreColor in the UI layer.
    private const double BadThreshold = 40.0;
    private const double MediocreThreshold = 65.0;
    private const double GoodThreshold = 80.0;

    // Generic fix strings — kept short so they fit in a side panel.
    private const string FixReplaceOrTrash = "Replace with a higher-quality source, or mark as Trash.";
    private const string FixOpenInEditor = "Open in the Image Editor to adjust.";
    private const string FixReshoot = "Re-shoot in better conditions if possible.";

    /// <summary>
    /// Builds an <see cref="ImageQualityAdvice"/> for one image.
    /// </summary>
    public static ImageQualityAdvice Analyze(PerImageQualitySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var overall = summary.OverallScore;
        if (double.IsNaN(overall))
        {
            return new ImageQualityAdvice
            {
                Verdict = ImageQualityVerdict.Unknown,
                Headline = "No quality checks ran for this image.",
                Problems = [],
                SuggestedFixes = []
            };
        }

        var problems = new List<ImageQualityProblem>(4);
        AddProblem(problems, "Blur", summary.BlurScore, summary.BlurDetail, BlurDescription);
        AddProblem(problems, "Exposure", summary.ExposureScore, summary.ExposureDetail, ExposureDescription);
        AddProblem(problems, "Noise", summary.NoiseScore, summary.NoiseDetail, NoiseDescription);
        AddProblem(problems, "JPEG quality", summary.JpegScore, summary.JpegDetail, JpegDescription);

        var verdict = ClassifyVerdict(overall);
        var headline = BuildHeadline(verdict, overall, problems);
        var fixes = BuildFixSuggestions(problems);

        return new ImageQualityAdvice
        {
            Verdict = verdict,
            Headline = headline,
            Problems = problems,
            SuggestedFixes = fixes
        };
    }

    private static void AddProblem(
        List<ImageQualityProblem> problems,
        string metricName,
        double? score,
        string? detail,
        Func<double, string?, string> describe)
    {
        if (score is not { } value || value >= MediocreThreshold)
            return;

        problems.Add(new ImageQualityProblem(metricName, value, describe(value, detail)));
    }

    private static string BlurDescription(double score, string? detail) =>
        score < BadThreshold
            ? AppendDetail("Image looks very blurry or soft.", detail)
            : AppendDetail("Image is slightly soft.", detail);

    private static string ExposureDescription(double score, string? detail) =>
        score < BadThreshold
            ? AppendDetail("Severe under- or over-exposure with clipped pixels.", detail)
            : AppendDetail("Exposure is off — highlights or shadows are weak.", detail);

    private static string NoiseDescription(double score, string? detail) =>
        score < BadThreshold
            ? AppendDetail("High visible noise (likely shot at high ISO).", detail)
            : AppendDetail("Some visible noise.", detail);

    private static string JpegDescription(double score, string? detail) =>
        score < BadThreshold
            ? AppendDetail("Heavy JPEG compression artifacts (low quality save).", detail)
            : AppendDetail("Mild JPEG compression artifacts.", detail);

    private static string AppendDetail(string sentence, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? sentence : $"{sentence} ({detail})";

    private static ImageQualityVerdict ClassifyVerdict(double overall) => overall switch
    {
        >= GoodThreshold => ImageQualityVerdict.Excellent,
        >= MediocreThreshold => ImageQualityVerdict.Good,
        >= BadThreshold => ImageQualityVerdict.Mediocre,
        _ => ImageQualityVerdict.Bad
    };

    private static string BuildHeadline(
        ImageQualityVerdict verdict,
        double overall,
        IReadOnlyList<ImageQualityProblem> problems)
    {
        if (problems.Count == 0)
            return $"Looks good — overall score {overall:F0}/100.";

        var worst = problems.OrderBy(p => p.Score).First();
        return verdict switch
        {
            ImageQualityVerdict.Bad =>
                $"Poor quality ({overall:F0}/100). Worst issue: {worst.MetricName.ToLowerInvariant()}.",
            ImageQualityVerdict.Mediocre =>
                $"Mediocre quality ({overall:F0}/100). Watch the {worst.MetricName.ToLowerInvariant()}.",
            ImageQualityVerdict.Good =>
                $"Acceptable ({overall:F0}/100), but {worst.MetricName.ToLowerInvariant()} could be better.",
            _ => $"Score {overall:F0}/100."
        };
    }

    private static IReadOnlyList<string> BuildFixSuggestions(IReadOnlyList<ImageQualityProblem> problems)
    {
        if (problems.Count == 0)
            return [];

        // Use a set to de-duplicate generic suggestions (e.g. blur AND noise both
        // suggest "replace or trash" — we only want one bullet).
        var fixes = new List<string>(3);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var hasBadBlur = problems.Any(p => p.MetricName == "Blur" && p.Score < BadThreshold);
        var hasBadNoise = problems.Any(p => p.MetricName == "Noise" && p.Score < BadThreshold);
        var hasExposureProblem = problems.Any(p => p.MetricName == "Exposure");
        var hasJpegProblem = problems.Any(p => p.MetricName == "JPEG quality");

        if (hasBadBlur || hasBadNoise)
            AddUnique(fixes, seen, FixReplaceOrTrash);

        if (hasExposureProblem)
        {
            AddUnique(fixes, seen, FixOpenInEditor);
            AddUnique(fixes, seen, FixReshoot);
        }

        if (hasJpegProblem)
            AddUnique(fixes, seen, "Re-export from the original source at JPEG quality \u2265 85, or use PNG.");

        // Catch-all: nothing matched a specific fix but at least one mediocre score exists.
        if (fixes.Count == 0)
            AddUnique(fixes, seen, FixOpenInEditor);

        return fixes;
    }

    private static void AddUnique(List<string> list, HashSet<string> seen, string value)
    {
        if (seen.Add(value))
            list.Add(value);
    }
}
