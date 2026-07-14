using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Models.Distiller;

namespace DiffusionNexus.UI.ViewModels.Pipelines;

/// <summary>One search→replacement row inside a Replace rule set.</summary>
public partial class ReplacePairViewModel : ViewModelBase
{
    [ObservableProperty] private string _from = "";
    [ObservableProperty] private string _to = "";
}

/// <summary>Editable view of one named delete/replace rule set.</summary>
public partial class PromptRuleSetViewModel : ViewModelBase
{
    [ObservableProperty] private string _name = "New rule set";
    [ObservableProperty] private bool _isReplace;
    [ObservableProperty] private bool _enabled = true;

    /// <summary>Delete-set editor content: words separated by commas/newlines.</summary>
    [ObservableProperty] private string _wordsText = "";

    /// <summary>Replace-set rows: each pair holds a search term and its replacement.</summary>
    public ObservableCollection<ReplacePairViewModel> Pairs { get; } = [];

    [RelayCommand]
    private void AddPair() => Pairs.Add(new ReplacePairViewModel());

    [RelayCommand]
    private void RemovePair(ReplacePairViewModel? pair)
    {
        if (pair is not null) Pairs.Remove(pair);
    }

    public PromptRuleSet ToModel()
    {
        if (IsReplace)
        {
            var pairs = Pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.From))
                .Select(p => new ReplacePair(p.From.Trim(), p.To.Trim()))
                .ToList();
            return new PromptRuleSet { Name = Name, Kind = RuleKind.Replace, Enabled = Enabled, ReplacePairs = pairs };
        }

        var words = WordsText
            .Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return new PromptRuleSet { Name = Name, Kind = RuleKind.Delete, Enabled = Enabled, DeleteWords = words };
    }
}
