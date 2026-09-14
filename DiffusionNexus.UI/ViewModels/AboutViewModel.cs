using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services.Licensing;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the About view: application identity plus the third-party attribution
/// required by the licences the product ships under.
/// </summary>
/// <remarks>
/// The parameterless constructor is required by <c>ViewBase&lt;TViewModel&gt;</c>, whose
/// generic constraint includes <c>new()</c>. The list-taking constructor exists so the
/// filtering logic can be tested without depending on the embedded resource.
/// </remarks>
public partial class AboutViewModel : ViewModelBase
{
    private readonly IReadOnlyList<ThirdPartyComponent> _allComponents;

    /// <summary>Production constructor: reads the embedded index.</summary>
    public AboutViewModel() : this(ThirdPartyNotices.Load())
    {
    }

    /// <summary>Test constructor: takes the component list directly.</summary>
    public AboutViewModel(IReadOnlyList<ThirdPartyComponent> components)
    {
        _allComponents = components;
        Components = new ObservableCollection<ThirdPartyComponent>(_allComponents);
        SelectedComponent = Components.FirstOrDefault();
        NoticesFilePath = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");
    }

    /// <summary>Gets the application version from assembly metadata.</summary>
    public string AppVersion { get; } = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    /// <summary>The components matching <see cref="SearchText"/>.</summary>
    public ObservableCollection<ThirdPartyComponent> Components { get; }

    /// <summary>How many components ship in total, regardless of the current filter.</summary>
    public int TotalComponentCount => _allComponents.Count;

    /// <summary>Where the flat notices file sits next to the executable.</summary>
    public string NoticesFilePath { get; }

    /// <summary>False in dev builds, where no publish folder exists.</summary>
    public bool HasNoticesFile => File.Exists(NoticesFilePath);

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private ThirdPartyComponent? _selectedComponent;

    partial void OnSearchTextChanged(string value)
    {
        var query = value?.Trim() ?? string.Empty;
        var previous = SelectedComponent;

        Components.Clear();
        foreach (var component in Filter(query))
            Components.Add(component);

        // Keep the selection when it survives the filter; otherwise fall to the first match,
        // so the detail pane never shows a component the list no longer offers.
        SelectedComponent = previous is not null && Components.Contains(previous)
            ? previous
            : Components.FirstOrDefault();
    }

    private IEnumerable<ThirdPartyComponent> Filter(string query)
    {
        if (string.IsNullOrEmpty(query))
            return _allComponents;

        // Matching the licence id as well as the package id is what makes "LGPL" a useful
        // query — the reason someone opens this screen.
        return _allComponents.Where(c =>
            c.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            c.License.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void OpenProjectUrl()
    {
        var url = SelectedComponent?.ProjectUrl;
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open project URL {Url}", url);
        }
    }

    [RelayCommand]
    private void OpenNoticesFile()
    {
        if (!HasNoticesFile)
            return;

        try
        {
            Process.Start(new ProcessStartInfo(NoticesFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Could not open notices file {Path}", NoticesFilePath);
        }
    }
}
