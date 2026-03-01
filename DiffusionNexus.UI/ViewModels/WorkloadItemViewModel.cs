using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

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
    /// Status text displayed in the table (Full, Partial, None, or Checking...).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusColor))]
    private string _status = "Checking...";

    /// <summary>
    /// Hex color string for the status text: green, yellow, or red.
    /// </summary>
    public string StatusColor => Status switch
    {
        "Full" => "#4CAF50",
        "Partial" => "#FFC107",
        "None" => "#F44336",
        _ => "#999999"
    };

    /// <summary>
    /// The full check result, populated after the checker runs.
    /// </summary>
    public ConfigurationCheckResult? CheckResult { get; set; }

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
