namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Backend-agnostic description of an application feature whose readiness can be checked
/// through <see cref="Services.IFeatureReadinessService"/>.
/// </summary>
/// <param name="Feature">The feature these requirements belong to.</param>
/// <param name="DisplayName">Human-readable feature name shown in the UI.</param>
/// <param name="WorkloadConfigurationId">
/// Optional Installer SDK <c>InstallationConfiguration.Id</c> backing this feature on the
/// ComfyUI backend. The <see cref="Services.IFeatureBackend"/> for ComfyUI uses this to
/// delegate readiness to the same disk-walking workload checker the Installer Manager dialog
/// uses, so both surfaces report identical state. Ignored by non-ComfyUI backends.
/// </param>
public sealed record FeatureRequirements(
    Enums.Feature Feature,
    string DisplayName,
    Guid? WorkloadConfigurationId = null);
