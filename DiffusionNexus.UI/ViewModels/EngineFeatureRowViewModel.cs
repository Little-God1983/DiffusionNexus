using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using DiffusionNexus.UI.Services.Engine;

namespace DiffusionNexus.UI.ViewModels;

public enum EngineFeatureStatus { Checking, Installed, Partial, NotInstalled, Installing, Error }

/// <summary>One row of the Engine Features dialog.</summary>
public sealed partial class EngineFeatureRowViewModel : ObservableObject
{
    public EngineFeatureRowViewModel(EngineFeatureDefinition definition)
    {
        Definition = definition;
    }

    public EngineFeatureDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelectable))]
    private EngineFeatureStatus _status = EngineFeatureStatus.Checking;

    [ObservableProperty] private string _statusText = "Checking…";
    [ObservableProperty] private string _needsText = string.Empty;

    /// <summary>
    /// An Installed row is ticked only because it is installed. When it stops being installed (a
    /// re-check finds it Partial, or the check fails) the tick goes, so the row does not slip into the
    /// next Install selected: the user re-ticks it deliberately. Ticks on other rows are left alone.
    /// </summary>
    partial void OnStatusChanged(EngineFeatureStatus oldValue, EngineFeatureStatus newValue)
    {
        if (oldValue == EngineFeatureStatus.Installed && newValue != EngineFeatureStatus.Installed)
            IsSelected = false;
    }

    /// <summary>Installed rows stay ticked and locked; rows mid-check or mid-install are locked too.</summary>
    public bool IsSelectable => Status is EngineFeatureStatus.Partial or EngineFeatureStatus.NotInstalled or EngineFeatureStatus.Error;

    /// <summary>Latest check per workload, kept for the install step.</summary>
    internal List<(InstallationConfiguration Config, ConfigurationCheckResult Result)> Checks { get; } = [];

    /// <summary>Latest check per folder model (see <see cref="EngineFolderModel"/>).</summary>
    internal List<(EngineFolderModel Model, bool IsComplete)> FolderModelChecks { get; } = [];

    /// <summary>Derives status, status text and the Needs column from <see cref="Checks"/>.</summary>
    internal void ApplyChecks()
    {
        var nodes = Checks.SelectMany(c => c.Result.CustomNodeResults).ToList();
        // A catalog placeholder stands for a model the catalog cannot describe; when the row brings
        // that model as a folder model, the folder model is what counts.
        var models = Checks.SelectMany(c => c.Result.ModelResults).Where(m => !m.IsPlaceholder)
            .Select(m => m.IsInstalled)
            .Concat(FolderModelChecks.Select(f => f.IsComplete))
            .ToList();
        var missing = nodes.Count(n => !n.IsInstalled) + models.Count(installed => !installed);
        var present = nodes.Count(n => n.IsInstalled) + models.Count(installed => installed);

        NeedsText = $"{nodes.Count} node pack{(nodes.Count == 1 ? "" : "s")} · {models.Count} model{(models.Count == 1 ? "" : "s")}";

        if (missing == 0)
        {
            Status = EngineFeatureStatus.Installed;
            StatusText = "Installed";
            IsSelected = true;
        }
        else if (present == 0)
        {
            Status = EngineFeatureStatus.NotInstalled;
            StatusText = "Not installed";
        }
        else
        {
            Status = EngineFeatureStatus.Partial;
            // Name what is missing, nodes and models alike: "Partial · 1 node pack and 3 models missing".
            var nodesMissing = nodes.Count(n => !n.IsInstalled);
            var modelsMissing = models.Count(installed => !installed);
            var parts = new List<string>(2);
            if (nodesMissing > 0) parts.Add($"{nodesMissing} node pack{(nodesMissing == 1 ? "" : "s")}");
            if (modelsMissing > 0) parts.Add($"{modelsMissing} model{(modelsMissing == 1 ? "" : "s")}");
            StatusText = $"Partial · {string.Join(" and ", parts)} missing";
        }
    }
}
