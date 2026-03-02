namespace DiffusionNexus.UI.Services.ConfigurationChecker.Models;

/// <summary>
/// Aggregated result of checking a configuration against a ComfyUI instance.
/// </summary>
public sealed record ConfigurationCheckResult
{
    /// <summary>Overall status: Full (green), Partial (yellow), or None (red).</summary>
    public required ConfigurationStatus OverallStatus { get; init; }

    /// <summary>Status considering only custom nodes.</summary>
    public required ConfigurationStatus CustomNodesStatus { get; init; }

    /// <summary>Status considering only models.</summary>
    public required ConfigurationStatus ModelsStatus { get; init; }

    /// <summary>Detected installation type (Manual vs Portable).</summary>
    public required ComfyUIInstallationType InstallationType { get; init; }

    /// <summary>Per-node check results.</summary>
    public required IReadOnlyList<CustomNodeCheckResult> CustomNodeResults { get; init; }

    /// <summary>Per-model check results.</summary>
    public required IReadOnlyList<ModelCheckResult> ModelResults { get; init; }

    /// <summary>Total custom nodes expected.</summary>
    public int TotalCustomNodes => CustomNodeResults.Count;

    /// <summary>Custom nodes found on disk.</summary>
    public int InstalledCustomNodes => CustomNodeResults.Count(n => n.IsInstalled);

    /// <summary>Total models expected.</summary>
    public int TotalModels => ModelResults.Count;

    /// <summary>Models found on disk.</summary>
    public int InstalledModels => ModelResults.Count(m => m.IsInstalled);

    /// <summary>Human-readable summary, e.g. "3/5 models, 2/2 custom nodes".</summary>
    public string Summary =>
        $"{InstalledModels}/{TotalModels} models, {InstalledCustomNodes}/{TotalCustomNodes} custom nodes";
}
