using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// ViewModel for the "Test Runs" sub-tab inside Dataset Quality.
/// Displays a history of "Analyze All" runs with scores, issue counts, and details.
/// </summary>
public partial class TestRunsTabViewModel : ObservableObject
{
    private readonly AnalysisRunStore _store;

    private string _datasetFolderPath = string.Empty;
    private bool _isLoading;
    private TestRunViewModel? _selectedRun;

    /// <summary>
    /// Creates a new <see cref="TestRunsTabViewModel"/>.
    /// </summary>
    /// <param name="store">The store used to load and persist run records.</param>
    public TestRunsTabViewModel(AnalysisRunStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        RefreshCommand = new AsyncRelayCommand(LoadRunsAsync);
        DeleteRunCommand = new AsyncRelayCommand<TestRunViewModel?>(DeleteRunAsync);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public TestRunsTabViewModel()
    {
        _store = new AnalysisRunStore();

        RefreshCommand = new AsyncRelayCommand(LoadRunsAsync);
        DeleteRunCommand = new AsyncRelayCommand<TestRunViewModel?>(DeleteRunAsync);
    }

    #region Observable Properties

    /// <summary>
    /// Whether the run list is currently loading.
    /// </summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Currently selected run in the list.
    /// </summary>
    public TestRunViewModel? SelectedRun
    {
        get => _selectedRun;
        set
        {
            if (SetProperty(ref _selectedRun, value))
            {
                OnPropertyChanged(nameof(HasSelectedRun));
            }
        }
    }

    /// <summary>
    /// Whether a run is currently selected.
    /// </summary>
    public bool HasSelectedRun => _selectedRun is not null;

    /// <summary>
    /// Whether any runs are available.
    /// </summary>
    public bool HasRuns => Runs.Count > 0;

    #endregion

    #region Collections

    /// <summary>
    /// All loaded runs, newest first.
    /// </summary>
    public ObservableCollection<TestRunViewModel> Runs { get; } = [];

    #endregion

    #region Commands

    /// <summary>
    /// Reloads the run history from disk.
    /// </summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>
    /// Deletes a specific run from disk and the list.
    /// </summary>
    public IAsyncRelayCommand<TestRunViewModel?> DeleteRunCommand { get; }

    #endregion

    /// <summary>
    /// Updates the dataset folder context and reloads runs.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset version folder.</param>
    public async Task RefreshContextAsync(string? folderPath)
    {
        _datasetFolderPath = folderPath ?? string.Empty;

        Runs.Clear();
        SelectedRun = null;
        OnPropertyChanged(nameof(HasRuns));

        if (!string.IsNullOrWhiteSpace(_datasetFolderPath))
        {
            await LoadRunsAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Notifies the tab that a new run was saved so it can reload.
    /// </summary>
    public async Task OnRunSavedAsync()
    {
        await LoadRunsAsync().ConfigureAwait(false);
    }

    private async Task LoadRunsAsync()
    {
        if (string.IsNullOrWhiteSpace(_datasetFolderPath))
            return;

        IsLoading = true;
        try
        {
            var records = await _store.LoadAllAsync(_datasetFolderPath).ConfigureAwait(false);

            Runs.Clear();
            foreach (var record in records)
            {
                Runs.Add(TestRunViewModel.FromRecord(record));
            }

            if (Runs.Count > 0)
                SelectedRun = Runs[0];

            OnPropertyChanged(nameof(HasRuns));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task DeleteRunAsync(TestRunViewModel? runVm)
    {
        if (runVm is null || string.IsNullOrWhiteSpace(_datasetFolderPath))
            return;

        await Task.Run(() => _store.Delete(_datasetFolderPath, runVm.Record)).ConfigureAwait(false);

        Runs.Remove(runVm);
        if (SelectedRun == runVm)
            SelectedRun = Runs.Count > 0 ? Runs[0] : null;

        OnPropertyChanged(nameof(HasRuns));
    }
}

/// <summary>
/// ViewModel wrapper for a single <see cref="AnalysisRunRecord"/> in the run history list.
/// </summary>
public class TestRunViewModel
{
    /// <summary>The underlying record.</summary>
    public required AnalysisRunRecord Record { get; init; }

    /// <summary>Display timestamp (local time).</summary>
    public required string Timestamp { get; init; }

    /// <summary>Version label (e.g. "V3").</summary>
    public required string VersionLabel { get; init; }

    /// <summary>Dataset label at time of run.</summary>
    public required string DatasetLabel { get; init; }

    /// <summary>LoRA type used.</summary>
    public required string LoraTypeLabel { get; init; }

    /// <summary>Composite score (0–100), or null.</summary>
    public double? CompositeScore { get; init; }

    /// <summary>Composite score label (Poor/Fair/Good/Excellent).</summary>
    public string CompositeScoreLabel { get; init; } = string.Empty;

    /// <summary>Color hex for the composite score.</summary>
    public string CompositeScoreColor { get; init; } = "#666";

    /// <summary>Total issue count.</summary>
    public required int TotalIssues { get; init; }

    /// <summary>Critical issue count.</summary>
    public required int CriticalCount { get; init; }

    /// <summary>Warning issue count.</summary>
    public required int WarningCount { get; init; }

    /// <summary>Info issue count.</summary>
    public required int InfoCount { get; init; }

    /// <summary>Number of caption files.</summary>
    public required int CaptionFiles { get; init; }

    /// <summary>Number of image files.</summary>
    public required int ImageFiles { get; init; }

    /// <summary>Analysis duration formatted.</summary>
    public required string DurationText { get; init; }

    /// <summary>Per-category score breakdown for display.</summary>
    public IReadOnlyList<CategoryScoreViewModel> CategoryScores { get; init; } = [];

    /// <summary>Issue snapshots from this run.</summary>
    public IReadOnlyList<RunIssueSnapshot> Issues { get; init; } = [];

    /// <summary>
    /// Creates a <see cref="TestRunViewModel"/> from a stored <see cref="AnalysisRunRecord"/>.
    /// </summary>
    public static TestRunViewModel FromRecord(AnalysisRunRecord record)
    {
        var local = record.AnalyzedAtUtc.ToLocalTime();
        var criticalCount = record.Summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Critical);
        var warningCount = record.Summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Warning);
        var infoCount = record.Summary.CountBySeverity.GetValueOrDefault(IssueSeverity.Info);

        var categoryVms = record.CompositeScore?.CategoryScores
            .Select(c => new CategoryScoreViewModel
            {
                CategoryName = FormatCategoryName(c.Category),
                Score = c.Score,
                ScoreColor = GetScoreColor(c.Score),
                Weight = $"{c.Weight * 100:F0}%"
            })
            .ToList() ?? [];

        return new TestRunViewModel
        {
            Record = record,
            Timestamp = local.ToString("dd MMM yyyy  HH:mm:ss"),
            VersionLabel = $"V{record.Version}",
            DatasetLabel = record.DatasetLabel,
            LoraTypeLabel = record.LoraType.ToString(),
            CompositeScore = record.CompositeScore?.Score,
            CompositeScoreLabel = record.CompositeScore?.Label ?? string.Empty,
            CompositeScoreColor = record.CompositeScore is not null
                ? GetScoreColor(record.CompositeScore.Score)
                : "#666",
            TotalIssues = criticalCount + warningCount + infoCount,
            CriticalCount = criticalCount,
            WarningCount = warningCount,
            InfoCount = infoCount,
            CaptionFiles = record.Summary.TotalCaptionFiles,
            ImageFiles = record.Summary.TotalImageFiles,
            DurationText = FormatDuration(record.Duration),
            CategoryScores = categoryVms,
            Issues = record.Issues.ToList()
        };
    }

    private static string GetScoreColor(double score) => score switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    private static string FormatCategoryName(QualityScoreCategory category) => category switch
    {
        QualityScoreCategory.ImageTechnicalQuality => "Image Quality",
        QualityScoreCategory.CaptionQuality => "Caption Quality",
        QualityScoreCategory.DatasetConsistency => "Consistency",
        QualityScoreCategory.DatasetCompleteness => "Completeness",
        _ => category.ToString()
    };

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalMinutes >= 1)
            return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
        if (duration.TotalSeconds >= 1)
            return $"{duration.TotalSeconds:F1}s";
        return $"{duration.TotalMilliseconds:F0}ms";
    }
}
