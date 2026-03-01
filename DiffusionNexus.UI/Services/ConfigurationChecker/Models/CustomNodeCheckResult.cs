namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Check result for a single custom node (Git repository).
/// </summary>
public sealed record CustomNodeCheckResult
{
    /// <summary>Unique ID of the Git repository entity.</summary>
    public required Guid Id { get; init; }

    /// <summary>Display name of the custom node.</summary>
    public required string Name { get; init; }

    /// <summary>Repository URL.</summary>
    public required string Url { get; init; }

    /// <summary>Whether the node folder was found on disk.</summary>
    public required bool IsInstalled { get; init; }

    /// <summary>The expected folder path that was checked.</summary>
    public required string ExpectedPath { get; init; }
}
