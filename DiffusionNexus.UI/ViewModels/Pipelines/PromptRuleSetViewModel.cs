using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>Editable view of one named delete/replace rule set.</summary>
public partial class PromptRuleSetViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "New rule set";
    [ObservableProperty] private bool _isReplace;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>
    /// Free-text editor content. Delete sets: words separated by commas/newlines. Replace sets:
    /// one "from =&gt; to" (or "-&gt;" / "→") per line.
    /// </summary>
    [ObservableProperty] private string _wordsText = "";

    public PromptRuleSet ToModel()
    {
        if (IsReplace)
        {
            var pairs = new List<ReplacePair>();
            foreach (var line in WordsText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var idx = IndexOfArrow(line, out var arrowLen);
                if (idx < 0) continue;
                var from = line[..idx].Trim();
                var to = line[(idx + arrowLen)..].Trim();
                if (from.Length > 0) pairs.Add(new ReplacePair(from, to));
            }
            return new PromptRuleSet { Name = Name, Kind = RuleKind.Replace, Enabled = Enabled, ReplacePairs = pairs };
        }

        var words = WordsText
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new PromptRuleSet { Name = Name, Kind = RuleKind.Delete, Enabled = Enabled, DeleteWords = words };
    }

    private static int IndexOfArrow(string line, out int len)
    {
        foreach (var (arrow, l) in new[] { ("=>", 2), ("->", 2), ("→", 1) })
        {
            var i = line.IndexOf(arrow, StringComparison.Ordinal);
            if (i >= 0) { len = l; return i; }
        }
        len = 0; return -1;
    }
}
