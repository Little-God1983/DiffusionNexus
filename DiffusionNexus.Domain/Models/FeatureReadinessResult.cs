namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Result of checking whether a ComfyUI feature's prerequisites are satisfied.
/// Returned by <see cref="Services.IComfyUIReadinessService.CheckFeatureAsync"/>.
/// </summary>
public sealed record FeatureReadinessResult
{
    /// <summary>
    /// The feature that was checked.
    /// </summary>
    public required Enums.ComfyUIFeature Feature { get; init; }

    /// <summary>
    /// Whether the ComfyUI server is reachable.
    /// </summary>
    public required bool IsServerOnline { get; init; }

    /// <summary>
    /// Whether all required custom nodes are installed on the server.
    /// </summary>
    public required bool AllNodesInstalled { get; init; }

    /// <summary>
    /// Whether all required models are present (non-auto-download models only).
    /// Auto-download models that are missing appear in <see cref="Warnings"/> instead.
    /// </summary>
    public required bool AllModelsPresent { get; init; }

    /// <summary>
    /// <c>true</c> when the feature is ready to execute: server online, all nodes installed,
    /// and all blocking models present. Auto-download model warnings do not block readiness.
    /// </summary>
    public bool IsReady => IsServerOnline && AllNodesInstalled && AllModelsPresent;

    /// <summary>
    /// Human-readable descriptions of blocking problems (server down, missing nodes, missing models).
    /// Empty when <see cref="IsReady"/> is <c>true</c>.
    /// </summary>
    public required IReadOnlyList<string> MissingRequirements { get; init; }

    /// <summary>
    /// Non-blocking warnings (e.g. an auto-download model not yet present).
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>
    /// The server URL that was checked. Useful for error messages.
    /// </summary>
    public string? ServerUrl { get; init; }

    /// <summary>
    /// Creates a result representing a completely unreachable server.
    /// </summary>
    public static FeatureReadinessResult ServerOffline(Enums.ComfyUIFeature feature, string serverUrl) => new()
    {
        Feature = feature,
        IsServerOnline = false,
        AllNodesInstalled = false,
        AllModelsPresent = false,
        MissingRequirements = [$"ComfyUI server not reachable at {serverUrl}"],
        Warnings = [],
        ServerUrl = serverUrl
    };
}
