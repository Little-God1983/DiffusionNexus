using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DiffusionNexus.UI.ViewModels;

public partial class DiffusionModelFilterViewModel : ObservableObject
{
    private bool _suppressSelectionNotifications;

    public ObservableCollection<DiffusionModelFilterOptionViewModel> Options { get; } = new();

    public event EventHandler? FiltersChanged;

    public IEnumerable<string> SelectedModels => Options
        .Where(o => o.IsSelected)
        .Select(o => o.DisplayName);

    public void SetOptions(IEnumerable<string> modelNames)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var normalized = modelNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(comparer)
            .OrderBy(name => name, comparer)
            .ToList();

        var previousSelections = Options
            .ToDictionary(o => o.DisplayName, o => o.IsSelected, comparer);

        foreach (var option in Options)
        {
            option.SelectionChanged -= OnOptionSelectionChanged;
        }

        Options.Clear();

        try
        {
            _suppressSelectionNotifications = true;

            foreach (var name in normalized)
            {
                var option = new DiffusionModelFilterOptionViewModel(name);
                option.SelectionChanged += OnOptionSelectionChanged;

                if (previousSelections.TryGetValue(name, out var isSelected) && isSelected)
                {
                    option.SetIsSelectedSilently(true);
                }

                Options.Add(option);
            }
        }
        finally
        {
            _suppressSelectionNotifications = false;
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearSelection()
    {
        bool anyChanged = false;
        foreach (var option in Options)
        {
            if (option.IsSelected)
            {
                option.SetIsSelectedSilently(false);
                anyChanged = true;
            }
        }

        if (anyChanged)
        {
            FiltersChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnOptionSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressSelectionNotifications)
        {
            return;
        }

        FiltersChanged?.Invoke(this, EventArgs.Empty);
    }
}
