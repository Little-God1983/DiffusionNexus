using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.UI.Models.Pipelines;

namespace DiffusionNexus.UI.Services.Pipelines;

/// <summary>
/// Downloads and verifies the model/LoRA assets a <see cref="PipelineManifest"/> requires,
/// into the default ComfyUI installation's <c>models/</c> tree — the same layout the local
/// DiffusionNexus renderer scans. Mirrors the Installer Manager's "declare → diff against disk
/// → download what's missing → re-check" workload flow; no installed-state is persisted.
/// </summary>
public interface IPipelineAssetInstaller
{
    /// <summary>
    /// Resolves the primary download-target models root (the default ComfyUI installation's
    /// <c>models/</c> folder), or <c>null</c> when no ComfyUI installation is registered.
    /// </summary>
    Task<string?> ResolveModelsRootAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks which of the manifest's assets are already present, searching <b>every</b> model
    /// root the Diffusion Nexus core uses — each ComfyUI install's <c>models/</c> folder plus any
    /// <c>extra_model_paths.yaml</c> base paths (e.g. a shared <c>D:\models</c> library). Purely
    /// disk-based (no network); the result drives the tile's readiness badge.
    /// </summary>
    Task<PipelineReadiness> CheckAsync(PipelineManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads every currently-missing asset for <paramref name="manifest"/> into the primary
    /// models root, selecting the diffusion-model variant that fits <paramref name="vramGb"/>.
    /// Each download is routed through the download coordinator (status-bar progress + cancel).
    /// Returns the readiness re-computed (across all roots) after install.
    /// </summary>
    Task<PipelineReadiness> InstallMissingAsync(
        PipelineManifest manifest,
        int vramGb,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the on-disk <c>.safetensors</c> path of a LoRA previously downloaded from the given
    /// Civitai model id (matched via its <c>.civitai.info</c> sidecar), searching every model root.
    /// Returns null if no matching weights file is present.
    /// </summary>
    Task<string?> FindLoraPathByModelIdAsync(int civitaiModelId, CancellationToken cancellationToken = default);
}
