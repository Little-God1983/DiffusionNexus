namespace DiffusionNexus.Domain.Models;

/// <summary>
/// Declares the custom nodes and models that a single <see cref="Enums.ComfyUIFeature"/> requires
/// from the ComfyUI server. The readiness service uses this to verify prerequisites before
/// a feature runs.
/// </summary>
/// <param name="Feature">The feature these requirements belong to.</param>
/// <param name="DisplayName">Human-readable feature name shown in the UI.</param>
/// <param name="RequiredNodeTypes">
/// ComfyUI node <c>class_type</c> names that must be installed
/// (e.g. <c>"Qwen3_VQA"</c>, <c>"UltimateSDUpscale"</c>).
/// </param>
/// <param name="RequiredModels">
/// Models the feature needs. Each entry pairs a node type + input name (used to query
/// <c>/object_info</c>) with the expected model substring.
/// An empty list means no model verification is needed (or the model auto-downloads).
/// </param>
public sealed record ComfyUIFeatureRequirements(
    Enums.ComfyUIFeature Feature,
    string DisplayName,
    IReadOnlyList<string> RequiredNodeTypes,
    IReadOnlyList<ModelRequirement> RequiredModels);

/// <summary>
/// Describes a single model that must be present on the ComfyUI server for a feature to work.
/// The readiness service queries <c>/object_info/{NodeType}</c> and checks whether
/// <see cref="ExpectedModelSubstring"/> appears in the dropdown values for <see cref="InputName"/>.
/// </summary>
/// <param name="NodeType">The ComfyUI node class_type that uses this model (e.g. <c>"Qwen3_VQA"</c>).</param>
/// <param name="InputName">The input field on the node (e.g. <c>"model"</c>, <c>"unet_name"</c>).</param>
/// <param name="ExpectedModelSubstring">
/// A case-insensitive substring that must appear in at least one dropdown option.
/// For example <c>"Qwen3-VL-4B-Instruct-FP8"</c>.
/// </param>
/// <param name="DisplayName">Human-readable model name shown in warnings.</param>
/// <param name="ApproximateSizeDescription">Optional size hint shown to the user (e.g. <c>"~8 GB"</c>).</param>
/// <param name="AutoDownloads">
/// If <c>true</c>, the model auto-downloads on first run and a missing model is treated
/// as a warning rather than a blocking prerequisite.
/// </param>
public sealed record ModelRequirement(
    string NodeType,
    string InputName,
    string ExpectedModelSubstring,
    string DisplayName,
    string? ApproximateSizeDescription = null,
    bool AutoDownloads = false);
