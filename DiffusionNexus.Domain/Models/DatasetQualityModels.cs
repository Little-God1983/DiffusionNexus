using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Configuration for a dataset being analyzed.
/// Carries the folder path, trigger word, and LoRA type that checks
/// need to tailor their rules.
/// </summary>
public record DatasetConfig
{
    /// <summary>
    /// Absolute path to the dataset folder containing images and captions.
    /// </summary>
    public required string FolderPath { get; init; }

    /// <summary>
    /// Trigger word/token used during training (e.g. "ohwx", "sks").
    /// Null when no trigger word is configured.
    /// </summary>
    public string? TriggerWord { get; init; }

    /// <summary>
    /// The LoRA type this dataset is being trained for.
    /// Determines which checks are applicable and how they score.
    /// </summary>
    public required LoraType LoraType { get; init; }
}

/// <summary>
/// A single caption file loaded from the dataset, paired with its image.
/// </summary>
public record CaptionFile
{
    /// <summary>
    /// Absolute path to the .txt / .caption file.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Raw caption text as read from disk.
    /// </summary>
    public required string RawText { get; init; }

    /// <summary>
    /// Detected caption style (natural language vs. booru tags).
    /// </summary>
    public CaptionStyle DetectedStyle { get; init; } = CaptionStyle.Unknown;

    /// <summary>
    /// Absolute path to the paired image file, or null when no matching image exists.
    /// </summary>
    public string? PairedImagePath { get; init; }

    /// <summary>
    /// File name without extension — used to match caption ↔ image by name.
    /// </summary>
    public string BaseName => Path.GetFileNameWithoutExtension(FilePath);
}

/// <summary>
/// A concrete text edit that can be applied to a file to fix an issue.
/// </summary>
public record FileEdit
{
    /// <summary>
    /// Absolute path to the file to modify.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// The original text to find in the file.
    /// </summary>
    public required string OriginalText { get; init; }

    /// <summary>
    /// The replacement text.
    /// </summary>
    public required string NewText { get; init; }
}

/// <summary>
/// A suggested fix for a detected issue. Contains a human-readable description
/// and the concrete file edits needed to apply the fix.
/// </summary>
public record FixSuggestion
{
    /// <summary>
    /// Human-readable description of what this fix does.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Ordered list of file edits that implement this fix.
    /// </summary>
    public required IReadOnlyList<FileEdit> Edits { get; init; }
}

/// <summary>
/// A single issue discovered during dataset quality analysis.
/// </summary>
public record Issue
{
    /// <summary>
    /// Severity of the issue.
    /// </summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>
    /// Short summary of the problem (suitable for a list view).
    /// </summary>
    public required string Message { get; init; }

    /// <summary>
    /// Detailed explanation of why this matters and how it affects training.
    /// </summary>
    public string? Details { get; init; }

    /// <summary>
    /// Which check domain produced this issue.
    /// </summary>
    public required CheckDomain Domain { get; init; }

    /// <summary>
    /// Name of the check that produced this issue (for grouping/filtering).
    /// </summary>
    public required string CheckName { get; init; }

    /// <summary>
    /// Paths of files affected by this issue.
    /// </summary>
    public IReadOnlyList<string> AffectedFiles { get; init; } = [];

    /// <summary>
    /// Optional suggested fixes the user can preview and apply.
    /// </summary>
    public IReadOnlyList<FixSuggestion> FixSuggestions { get; init; } = [];

    /// <summary>
    /// Optional check-specific metadata (e.g. recommended word range for length outliers).
    /// Keys are well-known constants; values are strings for serialisation safety.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}

/// <summary>
/// Summary statistics for a completed analysis run.
/// </summary>
public record AnalysisSummary
{
    /// <summary>
    /// Total number of caption files scanned.
    /// </summary>
    public int TotalCaptionFiles { get; init; }

    /// <summary>
    /// Total number of image files found (including unpaired).
    /// </summary>
    public int TotalImageFiles { get; init; }

    /// <summary>
    /// Count of issues by severity.
    /// </summary>
    public required IReadOnlyDictionary<IssueSeverity, int> CountBySeverity { get; init; }

    /// <summary>
    /// Number of checks that were executed.
    /// </summary>
    public int ChecksRun { get; init; }

    /// <summary>
    /// Total number of auto-fixable issues.
    /// </summary>
    public int FixableIssueCount { get; init; }
}

/// <summary>
/// Full report produced by the analysis pipeline.
/// </summary>
public record AnalysisReport
{
    /// <summary>
    /// The dataset configuration that was analyzed.
    /// </summary>
    public required DatasetConfig Config { get; init; }

    /// <summary>
    /// All issues found, ordered by severity (Critical → Warning → Info).
    /// </summary>
    public required IReadOnlyList<Issue> Issues { get; init; }

    /// <summary>
    /// Aggregated summary statistics.
    /// </summary>
    public required AnalysisSummary Summary { get; init; }

    /// <summary>
    /// UTC timestamp of when the analysis was completed.
    /// </summary>
    public DateTimeOffset AnalyzedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Per-check scores from all checks that ran (caption + image).
    /// Empty when no scored checks participated.
    /// </summary>
    public IReadOnlyList<CheckScore> CheckScores { get; init; } = [];

    /// <summary>
    /// Composite quality score, or null if no scored checks ran.
    /// </summary>
    public CompositeScoreResult? CompositeScore { get; init; }

    /// <summary>
    /// Per-image quality results from image analysis checks.
    /// Empty when no image checks were run.
    /// </summary>
    public IReadOnlyList<ImageCheckResult> ImageCheckResults { get; init; } = [];

    /// <summary>
    /// Creates summary statistics from the issue list and file counts.
    /// </summary>
    public static AnalysisSummary BuildSummary(
        IReadOnlyList<Issue> issues,
        int totalCaptionFiles,
        int totalImageFiles,
        int checksRun)
    {
        var countBySeverity = Enum.GetValues<IssueSeverity>()
            .ToDictionary(s => s, s => issues.Count(i => i.Severity == s));

        var fixableCount = issues.Count(i => i.FixSuggestions.Count > 0);

        return new AnalysisSummary
        {
            TotalCaptionFiles = totalCaptionFiles,
            TotalImageFiles = totalImageFiles,
            CountBySeverity = countBySeverity,
            ChecksRun = checksRun,
            FixableIssueCount = fixableCount
        };
    }
}
