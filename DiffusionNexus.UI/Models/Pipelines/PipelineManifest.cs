using System.Collections.Generic;

namespace DiffusionNexus.UI.Models.Pipelines;

/// <summary>
/// Declarative description of a guided image pipeline and the model/LoRA assets it
/// requires. Authored as an app-side JSON resource (one file per pipeline under
/// <c>Assets/Pipelines/</c>) and loaded by <see cref="Services.Pipelines.IPipelineManifestProvider"/>.
///
/// A pipeline is intentionally an <b>app-level</b> concept (e.g. "Anime-To-Real produces
/// photoreal renders via the local DiffusionNexus core"), distinct from an installable
/// ComfyUI workload in the SDK database. The assets are downloaded into the default
/// ComfyUI installation's <c>models/</c> tree, which is the same layout the local
/// renderer scans.
/// </summary>
public sealed class PipelineManifest
{
    /// <summary>Stable identifier, matching the gallery tile id (e.g. "anime-to-real").</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display title shown on the tile.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Short description shown on the tile / as its tooltip.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>
    /// Optional tile icon/preview image — a path relative to <c>Assets/Pipelines/</c>
    /// (e.g. "Icons/anime-to-real.jpg"). When set, it fills the tile's preview area; otherwise a
    /// generic placeholder icon is shown.
    /// </summary>
    public string? Icon { get; init; }

    /// <summary>The models and LoRAs this pipeline needs in order to run locally.</summary>
    public List<PipelineAsset> Assets { get; init; } = new();

    /// <summary>
    /// Whether this entry appears as a tile in the Workflows (Pipelines) gallery. <c>true</c> for
    /// guided pipelines that have their own run screen (e.g. Anime-To-Real). <c>false</c> for
    /// download-only model bundles whose payload is consumed by another surface — e.g.
    /// Qwen-Image-2512, which is selected directly in the Diffusion Canvas model dropdown and has no
    /// run screen. Either way the entry still appears in Installer Manager → Diffusion Nexus Core →
    /// Workloads so its assets can be installed.
    /// </summary>
    public bool ShowInGallery { get; init; } = true;
}

/// <summary>The role a <see cref="PipelineAsset"/> plays in the pipeline.</summary>
public enum PipelineAssetKind
{
    /// <summary>The diffusion / UNET / DiT weights (e.g. a FLUX.2-klein GGUF). Usually VRAM-tiered.</summary>
    DiffusionModel,

    /// <summary>A text encoder (e.g. a Qwen LLM encoder).</summary>
    TextEncoder,

    /// <summary>A VAE / autoencoder.</summary>
    Vae,

    /// <summary>A LoRA applied over the base model at generation time.</summary>
    Lora,
}

/// <summary>
/// A single downloadable asset within a <see cref="PipelineManifest"/>. An asset is sourced
/// either from HuggingFace (<see cref="HuggingFaceLinks"/>, optionally VRAM-tiered) or from a
/// Civitai model page (<see cref="CivitaiModelId"/>), never both.
/// </summary>
public sealed class PipelineAsset
{
    /// <summary>Human-readable name (e.g. "FLUX.2-klein-9B GGUF").</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>The asset's role in the pipeline.</summary>
    public PipelineAssetKind Kind { get; init; }

    /// <summary>
    /// ComfyUI models subfolder the file is downloaded into, relative to the install's
    /// <c>models/</c> root (e.g. "diffusion_models", "text_encoders", "vae", "loras").
    /// </summary>
    public string TargetSubfolder { get; init; } = string.Empty;

    /// <summary>
    /// For HuggingFace assets: optional override of the on-disk filename used for the
    /// "already downloaded?" check (defaults to the filename parsed from the URL).
    /// For Civitai LoRA assets: a case-insensitive <b>substring hint</b> used to detect a
    /// previously-downloaded LoRA on disk (the real filename is only known after resolving
    /// the Civitai API at download time).
    /// </summary>
    public string? ExpectedFileName { get; init; }

    /// <summary>
    /// HuggingFace source links. A single entry for non-tiered assets (encoder, VAE); one
    /// entry per VRAM tier for the diffusion model (each tagged with <see cref="PipelineHuggingFaceLink.VramGb"/>).
    /// </summary>
    public List<PipelineHuggingFaceLink> HuggingFaceLinks { get; init; } = new();

    /// <summary>
    /// Civitai model-page id for LoRA assets (e.g. 1934100). Resolved to a concrete file URL +
    /// filename at download time. Null for HuggingFace assets.
    /// </summary>
    public int? CivitaiModelId { get; init; }
}

/// <summary>A single HuggingFace download link, optionally tied to a VRAM tier.</summary>
public sealed class PipelineHuggingFaceLink
{
    /// <summary>The HuggingFace file URL (a <c>/resolve/</c> link; normalized at download time).</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>
    /// The VRAM budget (in GB) this variant targets. Null means the link is not VRAM-specific
    /// and is always selected (e.g. the text encoder and VAE).
    /// </summary>
    public int? VramGb { get; init; }
}
