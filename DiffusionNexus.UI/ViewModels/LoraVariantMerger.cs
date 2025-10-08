using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels;

internal static class LoraVariantMerger
{
    private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;

    public static IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds)
    {
        if (seeds == null)
        {
            throw new ArgumentNullException(nameof(seeds));
        }

        var order = new List<object>();
        var groupLookup = new Dictionary<GroupKey, VariantGroup>();

        foreach (var seed in seeds)
        {
            var classification = LoraVariantClassifier.Classify(seed.Model);
            if (IsMergeCandidate(seed.Model, classification))
            {
                var key = new GroupKey(classification.NormalizedKey!, seed.Model.ModelId!, seed.Model.DiffusionBaseModel);
                if (!groupLookup.TryGetValue(key, out var group))
                {
                    group = new VariantGroup(seed);
                    groupLookup.Add(key, group);
                    order.Add(group);
                }

                group.AddVariant(classification.VariantLabel!, seed.Model);
            }
            else
            {
                order.Add(CreateEntry(seed, classification));
            }
        }

        return order
            .Select(item => item switch
            {
                VariantGroup group => group.ToEntry(),
                LoraCardEntry entry => entry,
                _ => throw new InvalidOperationException("Unexpected order entry type.")
            })
            .ToList();
    }

    private static bool IsMergeCandidate(ModelClass model, LoraVariantClassification classification)
    {
        if (model == null)
        {
            return false;
        }

        if (classification == null)
        {
            return false;
        }

        if (!string.Equals(classification.VariantLabel, "High", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(classification.VariantLabel, "Low", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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

        return true;
    }

    private static LoraCardEntry CreateEntry(LoraCardSeed seed, LoraVariantClassification classification)
    {
        IReadOnlyList<LoraVariantDescriptor> variants;
        if (!string.IsNullOrWhiteSpace(classification.VariantLabel))
        {
            variants = new[] { new LoraVariantDescriptor(classification.VariantLabel!, seed.Model) };
        }
        else
        {
            variants = Array.Empty<LoraVariantDescriptor>();
        }

        return new LoraCardEntry(seed.Model, seed.SourcePath, seed.FolderPath, seed.TreePath, seed.TreeSegments, variants);
    }

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
            var ordered = _variants
                .OrderBy(v => GetVariantOrder(v.Key))
                .ThenBy(v => v.Key, LabelComparer)
                .Select(v => new LoraVariantDescriptor(v.Key, v.Value))
                .ToList();

            var selected = ordered.First();
            return new LoraCardEntry(selected.Model, _seed.SourcePath, _seed.FolderPath, _seed.TreePath, _seed.TreeSegments, ordered);
        }
    }
}

internal sealed record LoraCardSeed(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments);

internal sealed record LoraVariantDescriptor(string Label, ModelClass Model);

internal sealed record LoraCardEntry(
    ModelClass Model,
    string SourcePath,
    string? FolderPath,
    string TreePath,
    IReadOnlyList<string>? TreeSegments,
    IReadOnlyList<LoraVariantDescriptor> Variants);
