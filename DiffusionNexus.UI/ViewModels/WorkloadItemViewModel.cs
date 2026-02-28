using CommunityToolkit.Mvvm.ComponentModel;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// Represents a single workload row in the Workloads table.
/// </summary>
public partial class WorkloadItemViewModel : ViewModelBase
{
    /// <summary>
    /// The SDK configuration ID.
    /// </summary>
    public Guid Id { get; }

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private int _configVersion;

    [ObservableProperty]
    private int _configSubVersion;

    /// <summary>
    /// Placeholder status text.
    /// </summary>
    [ObservableProperty]
    private string _status = "Unknown";

    /// <summary>
    /// Display string for the version column.
    /// </summary>
    public string VersionDisplay => $"{ConfigVersion}.{ConfigSubVersion}";

    public WorkloadItemViewModel(Guid id, string name, string description, int configVersion, int configSubVersion)
    {
        Id = id;
        _name = name;
        _description = description;
        _configVersion = configVersion;
        _configSubVersion = configSubVersion;
    }
}
