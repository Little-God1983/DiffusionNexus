using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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

    /// <summary>
    /// Raised on ANY edit to this set — its own properties, pair rows added/removed, or a pair's
    /// text. The owning ViewModel listens to schedule the auto-save of persisted rule sets.
    /// </summary>
    public event EventHandler? Changed;

    public PromptRuleSetViewModel()
    {
        Pairs.CollectionChanged += OnPairsCollectionChanged;
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPairsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var p in e.OldItems.OfType<ReplacePairViewModel>())
                p.PropertyChanged -= OnPairPropertyChanged;
        if (e.NewItems is not null)
            foreach (var p in e.NewItems.OfType<ReplacePairViewModel>())
                p.PropertyChanged += OnPairPropertyChanged;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPairPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        Changed?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void AddPair() => Pairs.Add(new ReplacePairViewModel());

    [RelayCommand]
    private void RemovePair(ReplacePairViewModel? pair)
    {
        if (pair is not null) Pairs.Remove(pair);
    }

    /// <summary>Snapshot of the editor state for persistence.</summary>
    public PromptRuleSetData ToData() => new()
    {
        Name = Name,
        IsReplace = IsReplace,
        Enabled = Enabled,
        WordsText = WordsText,
        Pairs = Pairs.Select(p => new ReplacePairData(p.From, p.To)).ToList(),
    };

    /// <summary>Rebuilds an editor view from a persisted snapshot.</summary>
    public static PromptRuleSetViewModel FromData(PromptRuleSetData data)
    {
        var vm = new PromptRuleSetViewModel
        {
            Name = data.Name,
            IsReplace = data.IsReplace,
            Enabled = data.Enabled,
            WordsText = data.WordsText,
        };
        foreach (var p in data.Pairs)
            vm.Pairs.Add(new ReplacePairViewModel { From = p.From, To = p.To });
        return vm;
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
