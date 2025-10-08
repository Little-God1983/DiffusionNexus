using DiffusionNexus.Service.Classes;
using System.Collections.Generic;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Describes a LoRA discovered on disk before classification or merging occurs.
/// </summary>
/// <param name="Model">Underlying metadata describing the LoRA.</param>
/// <param name="SourcePath">Absolute path to the source file.</param>
/// <param name="FolderPath">Optional folder path displayed in the UI tree.</param>
/// <param name="TreePath">Logical tree representation used to group folders.</param>
/// <param name="TreeSegments">Precomputed folder hierarchy segments.</param>
internal sealed record LoraCardSeed(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments);

/// <summary>
/// Identifies a concrete LoRA variant and the <see cref="ModelClass"/> that should be loaded when selected.
/// </summary>
/// <param name="Label">User facing label describing the variant (e.g. High or Low).</param>
/// <param name="Model">Model metadata associated with the variant.</param>
internal sealed record LoraVariantDescriptor(string Label, ModelClass Model);

/// <summary>
/// Represents the merged information displayed on a LoRA card, including grouped variants.
/// </summary>
/// <param name="Model">Primary model shown for the card.</param>
/// <param name="SourcePath">Canonical source path used for IO operations.</param>
/// <param name="FolderPath">Folder path used when displaying the card in the tree.</param>
/// <param name="TreePath">Normalized tree identifier.</param>
/// <param name="TreeSegments">Optional folder hierarchy segments.</param>
/// <param name="Variants">Ordered list of switchable variants available for the card.</param>
internal sealed record LoraCardEntry(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments,
    IReadOnlyList<LoraVariantDescriptor> Variants);
