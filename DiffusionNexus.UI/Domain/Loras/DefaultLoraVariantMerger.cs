using DiffusionNexus.Service.Classes;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DiffusionNexus.UI.Domain.Loras;

/// <summary>
/// Default implementation of <see cref="ILoraVariantMerger"/> that mirrors the existing grouping behavior
/// in the UI while providing a more testable structure.
/// </summary>
internal sealed class DefaultLoraVariantMerger : ILoraVariantMerger
{
    private static readonly StringComparer LabelComparer = StringComparer.OrdinalIgnoreCase;
    private readonly ILoraVariantClassifier _classifier;

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultLoraVariantMerger"/> class.
    /// </summary>
    /// <param name="classifier">Classifier used to normalize variant metadata.</param>
    public DefaultLoraVariantMerger(ILoraVariantClassifier? classifier = null)
    {
        _classifier = classifier ?? new DefaultLoraVariantClassifier();
    }

    /// <inheritdoc />
    public IReadOnlyList<LoraCardEntry> Merge(IEnumerable<LoraCardSeed> seeds)
    {
        if (seeds == null)
        {
            throw new ArgumentNullException(nameof(seeds));
        }

        var aggregator = new MergeAggregator(_classifier);
        foreach (LoraCardSeed seed in seeds)
        {
            aggregator.Add(seed);
        }

        return aggregator.ToEntries();
    }

    /// <summary>
    /// Aggregates merge results while preserving input order.
    /// </summary>
    private sealed class MergeAggregator
    {
        private readonly ILoraVariantClassifier _classifier;
        private readonly Dictionary<GroupKey, VariantGroup> _groups = new();
        private readonly List<IMergeItem> _orderedItems = new();

        public MergeAggregator(ILoraVariantClassifier classifier)
        {
            _classifier = classifier;
        }

        public void Add(LoraCardSeed seed)
        {
            LoraVariantClassification classification = _classifier.Classify(seed.Model);
            if (IsMergeCandidate(seed.Model, classification))
            {
                VariantGroup group = GetOrCreateGroup(seed, classification);
                group.AddVariant(classification.VariantLabel!, seed.Model);
            }
            else
            {
                _orderedItems.Add(new StandaloneItem(CreateEntry(seed, classification)));
            }
        }

        public IReadOnlyList<LoraCardEntry> ToEntries() => _orderedItems.Select(item => item.ToEntry()).ToList();

        private VariantGroup GetOrCreateGroup(LoraCardSeed seed, LoraVariantClassification classification)
        {
            var key = new GroupKey(classification.NormalizedKey!, seed.Model.ModelId!, seed.Model.DiffusionBaseModel);
            if (!_groups.TryGetValue(key, out VariantGroup? group))
            {
                group = new VariantGroup(seed);
                _groups.Add(key, group);
                _orderedItems.Add(group);
            }

            return group;
        }
    }

    /// <summary>
    /// Determines whether a seed is eligible for variant merging.
    /// </summary>
    private static bool IsMergeCandidate(ModelClass model, LoraVariantClassification classification)
    {
        if (model == null || classification == null)
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

    /// <summary>
    /// Creates a <see cref="LoraCardEntry"/> for non-mergeable seeds.
    /// </summary>
    private static LoraCardEntry CreateEntry(LoraCardSeed seed, LoraVariantClassification classification)
    {
        IReadOnlyList<LoraVariantDescriptor> variants = string.IsNullOrWhiteSpace(classification.VariantLabel)
            ? Array.Empty<LoraVariantDescriptor>()
            : new[] { new LoraVariantDescriptor(classification.VariantLabel!, seed.Model) };

        return new LoraCardEntry(seed.Model, seed.SourcePath, seed.FolderPath, seed.TreePath, seed.TreeSegments, variants);
    }

    /// <summary>
    /// Represents a merge item that can produce a <see cref="LoraCardEntry"/> when required.
    /// </summary>
    private interface IMergeItem
    {
        LoraCardEntry ToEntry();
    }

    /// <summary>
    /// Wraps standalone entries to participate in the ordered output collection.
    /// </summary>
    private sealed class StandaloneItem : IMergeItem
    {
        private readonly LoraCardEntry _entry;

        public StandaloneItem(LoraCardEntry entry)
        {
            _entry = entry;
        }

        public LoraCardEntry ToEntry() => _entry;
    }

    /// <summary>
    /// Represents a grouping of High/Low variants that should render as a single card.
    /// </summary>
    private sealed class VariantGroup : IMergeItem
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
    /// Provides a stable key for variant grouping.
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

    /// <summary>
    /// Determines the preferred ordering for variant labels.
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
}
