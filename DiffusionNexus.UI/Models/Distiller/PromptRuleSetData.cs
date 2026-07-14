using System.Collections.Generic;

namespace DiffusionNexus.UI.Models.Distiller;

/// <summary>One persisted search→replacement row of a Replace rule set.</summary>
public sealed record ReplacePairData(string From, string To);

/// <summary>
/// Serialization shape for one Batch Metadata Distiller rule set, stored as JSON in
/// <c>AppSettings.DistillerRuleSetsJson</c>. Mirrors the editor state losslessly
/// (delete sets keep the user's raw comma/newline formatting in <see cref="WordsText"/>).
/// </summary>
public sealed class PromptRuleSetData
{
    public string Name { get; set; } = "";
    public bool IsReplace { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Delete-set editor content (unused for replace sets).</summary>
    public string WordsText { get; set; } = "";

    /// <summary>Replace-set rows (unused for delete sets).</summary>
    public List<ReplacePairData> Pairs { get; set; } = [];
}
