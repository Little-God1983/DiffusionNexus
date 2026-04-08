using DiffusionNexus.Domain.Enums;

namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Serializable snapshot of a single "Analyze All" run, stored as JSON
/// inside the <c>.quality-runs</c> folder of the dataset version directory.
/// </summary>
public record AnalysisRunRecord
{
    /// <summary>
    /// UTC timestamp when the analysis completed.
    /// </summary>
    public required DateTimeOffset AnalyzedAtUtc { get; init; }

    /// <summary>
    /// Dataset version number that was analyzed (e.g. 1, 2, 3…).
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Human-readable dataset label at the time of the run (e.g. "Ahkasha — V8").
    /// </summary>
    public string DatasetLabel { get; init; } = string.Empty;

    /// <summary>
    /// LoRA type used for the analysis.
    /// </summary>
    public LoraType LoraType { get; init; }

    /// <summary>
    /// Summary statistics from the run.
    /// </summary>
    public required AnalysisSummary Summary { get; init; }

    /// <summary>
    /// Composite quality score, or null if no scored checks ran.
    /// </summary>
    public CompositeScoreResult? CompositeScore { get; init; }

    /// <summary>
    /// Per-check scores (caption + image).
    /// </summary>
    public IReadOnlyList<CheckScore> CheckScores { get; init; } = [];

    /// <summary>
    /// Snapshot of issues discovered, stored without fix suggestions to keep files small.
    /// </summary>
    public IReadOnlyList<RunIssueSnapshot> Issues { get; init; } = [];

    /// <summary>
    /// Duration of the analysis run.
    /// </summary>
    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Lightweight snapshot of an <see cref="Issue"/> suitable for historical storage.
/// Omits <see cref="FixSuggestion"/> and file edits that become stale over time.
/// </summary>
public record RunIssueSnapshot
{
    /// <summary>Severity of the issue.</summary>
    public required IssueSeverity Severity { get; init; }

    /// <summary>Short summary of the problem.</summary>
    public required string Message { get; init; }

    /// <summary>Detailed explanation.</summary>
    public string? Details { get; init; }

    /// <summary>Which check domain produced this issue.</summary>
    public required CheckDomain Domain { get; init; }

    /// <summary>Name of the check that produced this issue.</summary>
    public required string CheckName { get; init; }

    /// <summary>Number of files affected.</summary>
    public int AffectedFileCount { get; init; }

    /// <summary>
    /// Creates a snapshot from a full <see cref="Issue"/>.
    /// </summary>
    public static RunIssueSnapshot FromIssue(Issue issue) => new()
    {
        Severity = issue.Severity,
        Message = issue.Message,
        Details = issue.Details,
        Domain = issue.Domain,
        CheckName = issue.CheckName,
        AffectedFileCount = issue.AffectedFiles.Count
    };
}
