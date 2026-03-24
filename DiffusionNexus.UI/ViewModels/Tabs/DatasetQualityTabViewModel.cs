using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the Dataset Quality tab within the dataset version detail view.
/// Analyzes the currently active dataset version via <see cref="AnalysisPipeline"/>
/// and applies one-click fixes via <see cref="FixApplier"/>.
/// </summary>
public class DatasetQualityTabViewModel : ObservableObject, IDialogServiceAware
{
    private readonly AnalysisPipeline? _pipeline;

    private string _datasetFolderPath = string.Empty;
    private string _datasetLabel = string.Empty;
    private string _triggerWord = string.Empty;
    private LoraType _selectedLoraType = LoraType.Character;
    private bool _isAnalyzing;
    private string _summaryText = string.Empty;
    private Issue? _selectedIssue;
    private AnalysisReport? _lastReport;

    /// <summary>
    /// Creates a new <see cref="DatasetQualityTabViewModel"/>.
    /// </summary>
    /// <param name="pipeline">The analysis pipeline for running quality checks.</param>
    /// <param name="bucketAnalyzer">Optional bucket analyzer for image bucketing analysis.</param>
    public DatasetQualityTabViewModel(AnalysisPipeline pipeline, BucketAnalyzer? bucketAnalyzer = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;

        BucketAnalysisTab = bucketAnalyzer is not null
            ? new BucketAnalysisTabViewModel(bucketAnalyzer)
            : new BucketAnalysisTabViewModel();

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        ApplyFixCommand = new AsyncRelayCommand<FixSuggestion?>(ApplyFixAsync);
        BackupCaptionsCommand = new AsyncRelayCommand(BackupCaptionsAsync);
        ExpandAllFilesCommand = new RelayCommand(ExpandAllFiles, () => EditableAffectedFiles.Count > 0);
        CollapseAllFilesCommand = new RelayCommand(CollapseAllFiles, () => EditableAffectedFiles.Count > 0);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public DatasetQualityTabViewModel()
    {
        BucketAnalysisTab = new BucketAnalysisTabViewModel();

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        ApplyFixCommand = new AsyncRelayCommand<FixSuggestion?>(ApplyFixAsync);
        BackupCaptionsCommand = new AsyncRelayCommand(BackupCaptionsAsync);
        ExpandAllFilesCommand = new RelayCommand(ExpandAllFiles, () => EditableAffectedFiles.Count > 0);
        CollapseAllFilesCommand = new RelayCommand(CollapseAllFiles, () => EditableAffectedFiles.Count > 0);
    }

    #region IDialogServiceAware

    /// <inheritdoc />
    public IDialogService? DialogService { get; set; }

    #endregion

    /// <summary>
    /// ViewModel for the embedded bucket analysis sub-tab.
    /// </summary>
    public BucketAnalysisTabViewModel BucketAnalysisTab { get; }

    #region Observable Properties

    /// <summary>
    /// Display label showing the active dataset name and version (e.g. "Ahkasha — V8").
    /// </summary>
    public string DatasetLabel
    {
        get => _datasetLabel;
        private set => SetProperty(ref _datasetLabel, value);
    }

    /// <summary>
    /// Whether the tab has a valid dataset context to analyze.
    /// </summary>
    public bool HasDatasetContext => !string.IsNullOrWhiteSpace(_datasetFolderPath);

    /// <summary>
    /// Trigger word / token configured for training.
    /// </summary>
    public string TriggerWord
    {
        get => _triggerWord;
        set => SetProperty(ref _triggerWord, value);
    }

    /// <summary>
    /// Currently selected LoRA type that determines which checks apply.
    /// </summary>
    public LoraType SelectedLoraType
    {
        get => _selectedLoraType;
        set => SetProperty(ref _selectedLoraType, value);
    }

    /// <summary>
    /// Whether analysis is currently running.
    /// </summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(CanAnalyze));
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether the Analyze command can execute (dataset context set and not already running).
    /// </summary>
    public bool CanAnalyze => HasDatasetContext && !IsAnalyzing;

    /// <summary>
    /// Human-readable summary line for the bottom status bar.
    /// </summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>
    /// Whether analysis results are available.
    /// </summary>
    public bool HasResults => _lastReport is not null;

    /// <summary>
    /// Currently selected issue in the left panel list.
    /// </summary>
    public Issue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (SetProperty(ref _selectedIssue, value))
            {
                OnPropertyChanged(nameof(HasSelectedIssue));
                PopulateEditableFiles(value);
            }
        }
    }

    /// <summary>
    /// Whether an issue is currently selected.
    /// </summary>
    public bool HasSelectedIssue => _selectedIssue is not null;

    #endregion

    #region Collections

    /// <summary>
    /// Available LoRA types for the dropdown.
    /// </summary>
    public LoraType[] AvailableLoraTypes { get; } = Enum.GetValues<LoraType>();

    /// <summary>
    /// All issues from the last analysis run, sorted by severity (Critical first).
    /// </summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>
    /// Editable wrappers for the currently selected issue's affected files.
    /// Each item supports inline caption editing with save/reset.
    /// </summary>
    public ObservableCollection<EditableAffectedFile> EditableAffectedFiles { get; } = [];

    #endregion

    #region Commands

    /// <summary>
    /// Runs the quality analysis pipeline on the active dataset version.
    /// </summary>
    public IAsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// Applies a single <see cref="FixSuggestion"/> then re-runs analysis.
    /// </summary>
    public IAsyncRelayCommand<FixSuggestion?> ApplyFixCommand { get; }

    /// <summary>
    /// Creates a timestamped backup of caption files in the dataset folder.
    /// </summary>
    public IAsyncRelayCommand BackupCaptionsCommand { get; }

    /// <summary>
    /// Expands all affected file editors for the current issue.
    /// </summary>
    public IRelayCommand ExpandAllFilesCommand { get; }

    /// <summary>
    /// Collapses all affected file editors for the current issue.
    /// </summary>
    public IRelayCommand CollapseAllFilesCommand { get; }

    #endregion

    #region Command Implementations

    private async Task AnalyzeAsync()
    {
        if (_pipeline is null || !HasDatasetContext) return;

        IsAnalyzing = true;
        try
        {
            var config = BuildConfig();

            // Run analysis on a background thread to keep the UI responsive
            var report = await Task.Run(() => _pipeline.Analyze(config));
            ApplyReport(report);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async Task ApplyFixAsync(FixSuggestion? suggestion)
    {
        if (suggestion is null || _pipeline is null || !HasDatasetContext) return;

        IsAnalyzing = true;
        try
        {
            await Task.Run(() => FixApplier.Apply(suggestion, createBackup: true));

            // Re-run analysis to refresh the issue list
            var config = BuildConfig();
            var report = await Task.Run(() => _pipeline.Analyze(config));
            ApplyReport(report);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async Task BackupCaptionsAsync()
    {
        if (!HasDatasetContext || !Directory.Exists(_datasetFolderPath))
            return;

        await Task.Run(() =>
        {
            var backupDir = Path.Combine(_datasetFolderPath, $".quality-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);

            foreach (var txtFile in Directory.EnumerateFiles(_datasetFolderPath, "*.txt"))
            {
                var dest = Path.Combine(backupDir, Path.GetFileName(txtFile));
                File.Copy(txtFile, dest, overwrite: true);
            }

            foreach (var captionFile in Directory.EnumerateFiles(_datasetFolderPath, "*.caption"))
            {
                var dest = Path.Combine(backupDir, Path.GetFileName(captionFile));
                File.Copy(captionFile, dest, overwrite: true);
            }
        });

        if (DialogService is not null)
        {
            await DialogService.ShowMessageAsync("Backup Complete",
                "All caption files have been backed up.");
        }
    }

    private DatasetConfig BuildConfig() => new()
    {
        FolderPath = _datasetFolderPath,
        TriggerWord = string.IsNullOrWhiteSpace(TriggerWord) ? null : TriggerWord.Trim(),
        LoraType = SelectedLoraType
    };

    #endregion

    #region Helpers

    /// <summary>
    /// Populates the observable collections and summary from an analysis report.
    /// </summary>
    private void ApplyReport(AnalysisReport report)
    {
        _lastReport = report;

        Issues.Clear();
        foreach (var issue in report.Issues)
        {
            Issues.Add(issue);
        }

        SelectedIssue = Issues.Count > 0 ? Issues[0] : null;
        SummaryText = FormatSummary(report.Summary);

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Formats the summary line for the bottom status bar.
    /// </summary>
    private static string FormatSummary(AnalysisSummary summary)
    {
        var criticalCount = summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Critical);
        var warningCount = summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Warning);
        var infoCount = summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Info);

        var parts = new List<string>
        {
            $"{summary.TotalImageFiles} files",
            $"{summary.TotalCaptionFiles} captions"
        };

        if (criticalCount > 0)
            parts.Add($"{criticalCount} critical");
        if (warningCount > 0)
            parts.Add($"{warningCount} warnings");
        if (infoCount > 0)
            parts.Add($"{infoCount} info");
        if (summary.FixableIssueCount > 0)
            parts.Add($"{summary.FixableIssueCount} fixable");

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// Updates the analysis context from the currently active dataset and version.
    /// Called by the parent ViewModel when the dataset is opened or the version changes.
    /// </summary>
    /// <param name="folderPath">Absolute path to the version folder.</param>
    /// <param name="datasetName">Display name of the dataset.</param>
    /// <param name="version">Currently selected version number.</param>
    /// <param name="categoryName">Category name ("Character", "Concept", "Style") for LoRA type mapping.</param>
    public void RefreshContext(string? folderPath, string? datasetName, int version, string? categoryName)
    {
        _datasetFolderPath = folderPath ?? string.Empty;
        DatasetLabel = string.IsNullOrWhiteSpace(datasetName)
            ? string.Empty
            : $"{datasetName} — V{version}";

        // Auto-map category name to LoRA type when a recognized category is set
        if (!string.IsNullOrWhiteSpace(categoryName))
        {
            SelectedLoraType = MapCategoryToLoraType(categoryName);
        }

        // Clear stale results when the context changes
        _lastReport = null;
        Issues.Clear();
        SelectedIssue = null;
        SummaryText = string.Empty;

        OnPropertyChanged(nameof(HasDatasetContext));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();

        // Forward folder context to bucket analysis sub-tab
        BucketAnalysisTab.RefreshContext(_datasetFolderPath);
    }

    /// <summary>
    /// Maps a dataset category name to the corresponding <see cref="LoraType"/>.
    /// Falls back to <see cref="LoraType.Character"/> when the category is not recognized.
    /// </summary>
    private static LoraType MapCategoryToLoraType(string categoryName) => categoryName.Trim().ToLowerInvariant() switch
    {
        "character" => LoraType.Character,
        "concept" => LoraType.Concept,
        "style" => LoraType.Style,
        _ => LoraType.Character
    };

    /// <summary>
    /// Populates <see cref="EditableAffectedFiles"/> from the given issue's affected file paths.
    /// Only includes .txt/.caption files that exist on disk.
    /// </summary>
    private void PopulateEditableFiles(Issue? issue)
    {
        EditableAffectedFiles.Clear();

        if (issue?.AffectedFiles is not { Count: > 0 })
        {
            ExpandAllFilesCommand.NotifyCanExecuteChanged();
            CollapseAllFilesCommand.NotifyCanExecuteChanged();
            return;
        }

        // Extract recommended word range from metadata if present
        int? recMin = issue.Metadata.TryGetValue("RecommendedMinWords", out var minStr)
            && int.TryParse(minStr, out var minVal) ? minVal : null;
        int? recMax = issue.Metadata.TryGetValue("RecommendedMaxWords", out var maxStr)
            && int.TryParse(maxStr, out var maxVal) ? maxVal : null;

        foreach (var filePath in issue.AffectedFiles)
        {
            var ext = Path.GetExtension(filePath);
            if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".caption", StringComparison.OrdinalIgnoreCase))
            {
                EditableAffectedFiles.Add(
                    new EditableAffectedFile(filePath, OnCaptionSavedAsync, recMin, recMax));
            }
        }

        ExpandAllFilesCommand.NotifyCanExecuteChanged();
        CollapseAllFilesCommand.NotifyCanExecuteChanged();
    }

    private void ExpandAllFiles()
    {
        foreach (var file in EditableAffectedFiles)
        {
            file.Expand();
        }
    }

    private void CollapseAllFiles()
    {
        foreach (var file in EditableAffectedFiles)
        {
            file.Collapse();
        }
    }

    /// <summary>
    /// Callback invoked after a caption file is saved. Re-runs analysis to refresh results.
    /// </summary>
    private async Task OnCaptionSavedAsync(EditableAffectedFile _)
    {
        if (_pipeline is null || !HasDatasetContext) return;

        IsAnalyzing = true;
        try
        {
            var config = BuildConfig();
            var report = await Task.Run(() => _pipeline.Analyze(config)).ConfigureAwait(false);
            ApplyReport(report);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    #endregion
}
