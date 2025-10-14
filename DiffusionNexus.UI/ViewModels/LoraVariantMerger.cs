using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Defines the behavior for merging individual LoRA card seeds into grouped card entries.
/// </summary>
internal interface ILoraVariantMerger
{
    /// <summary>
    /// Merges the provided seeds into logical card entries that expose variant selections.
    /// </summary>
    /// <param name="seeds">The source seeds discovered while scanning the local file system.</param>
    /// <returns>A list of grouped entries preserving the original ordering.</returns>
    IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds);
}

/// <summary>
/// Provides the default entry point for merging LoRA variant cards used by the UI.
/// </summary>
internal static class LoraVariantMerger
{
    private static readonly ILoraVariantMerger DefaultMerger = new DefaultLoraVariantMerger(LoraVariantClassifier.Classify);

    /// <summary>
    /// Merges the supplied seeds into grouped entries where available.
    /// </summary>
    /// <param name="seeds">The seeds discovered while building the LoRA library tree.</param>
    /// <returns>A merged list of card entries.</returns>
    public static IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds) => DefaultMerger.Merge(seeds);

    /// <summary>
    /// Exposes the merger implementation for unit tests.
    /// </summary>
    internal static ILoraVariantMerger Default => DefaultMerger;

    /// <summary>
    /// Default implementation that groups High/Low variants for identical models.
    /// </summary>
    private sealed class DefaultLoraVariantMerger : ILoraVariantMerger
    {
        private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;
        private readonly Func<ModelClass, LoraVariantClassification> _classify;

        public DefaultLoraVariantMerger(Func<ModelClass, LoraVariantClassification> classify)
        {
            _classify = classify ?? throw new ArgumentNullException(nameof(classify));
        }

        /// <inheritdoc />
        public IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds)
        {
            if (seeds is null)
            {
                throw new ArgumentNullException(nameof(seeds));
            }

            var order = new List<object>();
            var groups = new Dictionary<GroupKey, VariantGroup>();

            foreach (LoraCardSeed seed in seeds)
            {
                LoraVariantClassification classification = _classify(seed.Model);
                if (!TryAddToGroup(seed, classification, order, groups))
                {
                    order.Add(CreateStandaloneEntry(seed, classification));
                }
            }

            return ProjectResults(order);
        }

        private static bool TryAddToGroup(
            LoraCardSeed seed,
            LoraVariantClassification classification,
            ICollection<object> order,
            IDictionary<GroupKey, VariantGroup> groups)
        {
            if (!IsMergeCandidate(seed.Model, classification) || !TryCreateGroupKey(seed.Model, classification, out GroupKey key))
            {
                return false;
            }

            if (!groups.TryGetValue(key, out VariantGroup? group))
            {
                group = new VariantGroup(seed);
                groups.Add(key, group);
                order.Add(group);
            }

            group.AddVariant(classification.VariantLabel!, seed.Model);
            return true;
        }

        private static bool TryCreateGroupKey(
            ModelClass model,
            LoraVariantClassification classification,
            out GroupKey key)
        {
            key = default;
            if (string.IsNullOrWhiteSpace(classification.NormalizedKey))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(model.ModelId))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(model.DiffusionBaseModel))
            {
                return false;
            }

            key = new GroupKey(classification.NormalizedKey!, model.ModelId!, model.DiffusionBaseModel!);
            return true;
        }

        private static bool IsMergeCandidate(ModelClass model, LoraVariantClassification classification)
        {
            if (model is null || classification is null)
            {
                return false;
            }

            return string.Equals(classification.VariantLabel, "High", StringComparison.OrdinalIgnoreCase)
                || string.Equals(classification.VariantLabel, "Low", StringComparison.OrdinalIgnoreCase);
        }

        private static LoraCardEntry CreateStandaloneEntry(LoraCardSeed seed, LoraVariantClassification classification)
        {
            IReadOnlyList<LoraVariantDescriptor> variants = string.IsNullOrWhiteSpace(classification.VariantLabel)
                ? Array.Empty<LoraVariantDescriptor>()
                : new[] { new LoraVariantDescriptor(classification.VariantLabel!, seed.Model) };

            return new LoraCardEntry(seed.Model, seed.SourcePath, seed.FolderPath, seed.TreePath, seed.TreeSegments, variants);
        }

        private static IReadOnlyList<LoraCardEntry> ProjectResults(IEnumerable<object> order)
        {
            return order
                .Select(item => item switch
                {
                    VariantGroup group => group.ToEntry(),
                    LoraCardEntry entry => entry,
                    _ => throw new InvalidOperationException("Unexpected merge result type.")
                })
                .ToList();
        }

        /// <summary>
        /// Encapsulates the grouped variants that represent a single card entry.
        /// </summary>
        private sealed class VariantGroup
        {
            private readonly LoraCardSeed _seed;
            private readonly Dictionary<string, ModelClass> _variants = new(LabelComparer);

            public VariantGroup(LoraCardSeed seed)
            {
                _seed = seed;
            }

            public void AddVariant(string label, ModelClass model)
            {
                _variants[label] = model;
            }

            public LoraCardEntry ToEntry()
            {
                List<LoraVariantDescriptor> ordered = _variants
                    .OrderBy(v => GetVariantOrder(v.Key))
                    .ThenBy(v => v.Key, LabelComparer)
                    .Select(v => new LoraVariantDescriptor(v.Key, v.Value))
                    .ToList();

                LoraVariantDescriptor selected = ordered.First();
                return new LoraCardEntry(selected.Model, _seed.SourcePath, _seed.FolderPath, _seed.TreePath, _seed.TreeSegments, ordered);
            }
        }

        /// <summary>
        /// Determines the preferred ordering for variant labels so High appears before Low.
        /// </summary>
        private static int GetVariantOrder(string label)
        {
            if (string.Equals(label, "High", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(label, "Low", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        /// <summary>
        /// Composite key used to group variants belonging to the same logical model.
        /// </summary>
        private readonly struct GroupKey : IEquatable<GroupKey>
        {
            private readonly string _normalizedKey;
            private readonly string _modelId;
            private readonly string _baseModel;

            public GroupKey(string normalizedKey, string modelId, string baseModel)
            {
                _normalizedKey = normalizedKey;
                _modelId = modelId;
                _baseModel = baseModel;
            }

            public bool Equals(GroupKey other) =>
                string.Equals(_normalizedKey, other._normalizedKey, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_modelId, other._modelId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_baseModel, other._baseModel, StringComparison.OrdinalIgnoreCase);

            public override bool Equals(object? obj) => obj is GroupKey other && Equals(other);

            public override int GetHashCode()
            {
                var hash = new HashCode();
                hash.Add(_normalizedKey, StringComparer.OrdinalIgnoreCase);
                hash.Add(_modelId, StringComparer.OrdinalIgnoreCase);
                hash.Add(_baseModel, StringComparer.OrdinalIgnoreCase);
                return hash.ToHashCode();
            }
        }
    }
}

/// <summary>
/// Represents the raw data required to construct a LoRA card view model.
/// </summary>
internal sealed record LoraCardSeed(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments);

/// <summary>
/// Represents an individual variant option displayed on a LoRA card.
/// </summary>
/// <param name="Label">Display label shown to the user (High/Low/etc.).</param>
/// <param name="Model">The backing model metadata that will be loaded when selected.</param>
internal sealed record LoraVariantDescriptor(string Label, ModelClass Model);

/// <summary>
/// Represents a LoRA card displayed in the helper view, including available variants.
/// </summary>
/// <param name="Model">The default model shown on the card.</param>
/// <param name="SourcePath">Absolute path of the safetensor file.</param>
/// <param name="FolderPath">Optional folder path shown in the UI.</param>
/// <param name="TreePath">Path used to render the folder tree.</param>
/// <param name="TreeSegments">Segments for each level of the folder tree.</param>
/// <param name="Variants">Available variant selections (may be empty).</param>
internal sealed record LoraCardEntry(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments,
    IReadOnlyList<LoraVariantDescriptor> Variants);
