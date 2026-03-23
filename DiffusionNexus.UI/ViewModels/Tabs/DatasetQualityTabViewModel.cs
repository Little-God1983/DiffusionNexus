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
/// Drives folder picking, analysis via <see cref="AnalysisPipeline"/>, issue
/// navigation, and one-click fix application via <see cref="FixApplier"/>.
/// </summary>
public class DatasetQualityTabViewModel : ObservableObject, IDialogServiceAware
{
    private readonly AnalysisPipeline? _pipeline;

    private string _folderPath = string.Empty;
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
    public DatasetQualityTabViewModel(AnalysisPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _pipeline = pipeline;

        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        ApplyFixCommand = new AsyncRelayCommand<FixSuggestion?>(ApplyFixAsync);
        BackupCaptionsCommand = new AsyncRelayCommand(BackupCaptionsAsync);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public DatasetQualityTabViewModel()
    {
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync);
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        ApplyFixCommand = new AsyncRelayCommand<FixSuggestion?>(ApplyFixAsync);
        BackupCaptionsCommand = new AsyncRelayCommand(BackupCaptionsAsync);
    }

    #region IDialogServiceAware

    /// <inheritdoc />
    public IDialogService? DialogService { get; set; }

    #endregion

    #region Observable Properties

    /// <summary>
    /// Absolute path to the dataset folder to analyze.
    /// </summary>
    public string FolderPath
    {
        get => _folderPath;
        set
        {
            if (SetProperty(ref _folderPath, value))
            {
                OnPropertyChanged(nameof(CanAnalyze));
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

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
    /// Whether the Analyze command can execute (folder set and not already running).
    /// </summary>
    public bool CanAnalyze => !string.IsNullOrWhiteSpace(FolderPath) && !IsAnalyzing;

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

    #endregion

    #region Commands

    /// <summary>
    /// Opens a folder picker and sets <see cref="FolderPath"/>.
    /// </summary>
    public IAsyncRelayCommand BrowseFolderCommand { get; }

    /// <summary>
    /// Runs the quality analysis pipeline on the configured dataset.
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

    #endregion

    #region Command Implementations

    private async Task BrowseFolderAsync()
    {
        if (DialogService is null) return;

        var path = await DialogService.ShowOpenFolderDialogAsync("Select Dataset Folder");
        if (!string.IsNullOrWhiteSpace(path))
        {
            FolderPath = path;
        }
    }

    private async Task AnalyzeAsync()
    {
        if (_pipeline is null) return;

        IsAnalyzing = true;
        try
        {
            var config = new DatasetConfig
            {
                FolderPath = FolderPath,
                TriggerWord = string.IsNullOrWhiteSpace(TriggerWord) ? null : TriggerWord.Trim(),
                LoraType = SelectedLoraType
            };

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
        if (suggestion is null || _pipeline is null) return;

        IsAnalyzing = true;
        try
        {
            await Task.Run(() => FixApplier.Apply(suggestion, createBackup: true));

            // Re-run analysis to refresh the issue list
            var config = new DatasetConfig
            {
                FolderPath = FolderPath,
                TriggerWord = string.IsNullOrWhiteSpace(TriggerWord) ? null : TriggerWord.Trim(),
                LoraType = SelectedLoraType
            };

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
        if (string.IsNullOrWhiteSpace(FolderPath) || !Directory.Exists(FolderPath))
            return;

        await Task.Run(() =>
        {
            var backupDir = Path.Combine(FolderPath, $".quality-backup-{DateTime.Now:yyyyMMdd-HHmmss}");
            Directory.CreateDirectory(backupDir);

            foreach (var txtFile in Directory.EnumerateFiles(FolderPath, "*.txt"))
            {
                var dest = Path.Combine(backupDir, Path.GetFileName(txtFile));
                File.Copy(txtFile, dest, overwrite: true);
            }

            foreach (var captionFile in Directory.EnumerateFiles(FolderPath, "*.caption"))
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
    /// Pre-fills the folder path from the currently active dataset, if available.
    /// Called by the parent ViewModel when the dataset quality tab is activated.
    /// </summary>
    /// <param name="folderPath">The folder path to pre-fill.</param>
    /// <param name="loraType">Optional LoRA type to pre-select.</param>
    public void SetDatasetContext(string? folderPath, LoraType? loraType = null)
    {
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            FolderPath = folderPath;
        }

        if (loraType.HasValue)
        {
            SelectedLoraType = loraType.Value;
        }
    }

    #endregion
}
