using System.Collections.Generic;

namespace DiffusionNexus.UI.Models.Distiller;

/// <summary>Whether a rule set deletes words or replaces them.</summary>
public enum RuleKind { Delete, Replace }

/// <summary>A single word replacement (case-insensitive match on <see cref="From"/>).</summary>
public readonly record struct ReplacePair(string From, string To);

/// <summary>
/// A named, toggleable set of prompt-cleanup rules applied batch-wide by the Batch Metadata
/// Distiller. A set is EITHER a delete list OR a replace list, per <see cref="Kind"/>.
/// </summary>
public sealed class PromptRuleSet
{
    public string Name { get; set; } = "New rule set";
    public RuleKind Kind { get; set; } = RuleKind.Delete;
    public bool Enabled { get; set; } = true;

    /// <summary>Words removed when <see cref="Kind"/> is <see cref="RuleKind.Delete"/>.</summary>
    public IReadOnlyList<string> DeleteWords { get; set; } = [];

    /// <summary>Substitutions applied when <see cref="Kind"/> is <see cref="RuleKind.Replace"/>.</summary>
    public IReadOnlyList<ReplacePair> ReplacePairs { get; set; } = [];
}
