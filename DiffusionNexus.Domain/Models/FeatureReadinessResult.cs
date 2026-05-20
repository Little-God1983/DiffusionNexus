namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Result of checking whether a feature's prerequisites are satisfied.
/// Returned by <see cref="Services.IFeatureReadinessService.CheckAsync"/>.
/// </summary>
public sealed record FeatureReadinessResult
{
    /// <summary>The feature that was checked.</summary>
    public required Enums.Feature Feature { get; init; }

    /// <summary>The backend that produced this result (ComfyUI, LocalInference, …).</summary>
    public required Enums.BackendKind Backend { get; init; }

    /// <summary>
    /// Whether the active backend is reachable / loaded. For ComfyUI this means the server
    /// answered a health check; for LocalInference this means the native library is loaded.
    /// </summary>
    public required bool IsBackendOnline { get; init; }

    /// <summary>
    /// <c>true</c> when the feature is ready to execute on the active backend.
    /// </summary>
    public required bool IsReady { get; init; }

    /// <summary>
    /// Human-readable name of the active backend (e.g. <c>"ComfyUI"</c>, <c>"Diffusion Nexus Core"</c>).
    /// </summary>
    public required string ActiveBackendName { get; init; }

    /// <summary>
    /// Human-readable descriptions of blocking problems (backend offline, missing nodes, missing models).
    /// Empty when <see cref="IsReady"/> is <c>true</c>.
    /// </summary>
    public required IReadOnlyList<string> MissingRequirements { get; init; }

    /// <summary>
    /// Non-blocking warnings (e.g. an auto-download model not yet present).
    /// </summary>
    public required IReadOnlyList<string> Warnings { get; init; }

    /// <summary>Optional endpoint identifier (e.g. ComfyUI server URL). Useful for error messages.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Creates a result representing a backend that is completely unreachable.</summary>
    public static FeatureReadinessResult BackendOffline(
        Enums.Feature feature,
        Enums.BackendKind backend,
        string activeBackendName,
        string offlineMessage,
        string? endpoint = null) => new()
    {
        Feature = feature,
        Backend = backend,
        IsBackendOnline = false,
        IsReady = false,
        ActiveBackendName = activeBackendName,
        MissingRequirements = [offlineMessage],
        Warnings = [],
        Endpoint = endpoint
    };
}
