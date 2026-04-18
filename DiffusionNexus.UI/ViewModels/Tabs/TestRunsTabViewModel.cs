using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Views.Controls;

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
    private bool _isLiveAnalyzing;
    private string _currentCheckName = string.Empty;

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

    /// <summary>
    /// Dialog service for showing confirmation dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

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

    /// <summary>
    /// Whether enough data points exist to render the score trend chart (at least 2 runs).
    /// </summary>
    public bool HasTrendData => TrendDataPoints.Count >= 2;

    /// <summary>
    /// Whether a live analysis is currently in progress.
    /// </summary>
    public bool IsLiveAnalyzing
    {
        get => _isLiveAnalyzing;
        private set => SetProperty(ref _isLiveAnalyzing, value);
    }

    /// <summary>
    /// Name of the check currently being executed.
    /// </summary>
    public string CurrentCheckName
    {
        get => _currentCheckName;
        private set => SetProperty(ref _currentCheckName, value);
    }

    /// <summary>
    /// Check scores that have completed during the live analysis.
    /// </summary>
    public ObservableCollection<LiveCheckScoreViewModel> LiveCheckScores { get; } = [];

    #endregion

    #region Collections

    /// <summary>
    /// All loaded runs, newest first.
    /// </summary>
    public ObservableCollection<TestRunViewModel> Runs { get; } = [];

    /// <summary>
    /// Score trend data points for the trend line chart (oldest first).
    /// </summary>
    public ObservableCollection<ScoreTrendDataPoint> TrendDataPoints { get; } = [];

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
        NotifyRunCountChanged();

        if (!string.IsNullOrWhiteSpace(_datasetFolderPath))
        {
            await LoadRunsAsync();
        }
    }

    /// <summary>
    /// Notifies the tab that a new run was saved so it can reload.
    /// </summary>
    public async Task OnRunSavedAsync()
    {
        await LoadRunsAsync();
    }

    private async Task LoadRunsAsync()
    {
        if (string.IsNullOrWhiteSpace(_datasetFolderPath))
            return;

        IsLoading = true;
        try
        {
            var records = await _store.LoadAllAsync(_datasetFolderPath);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Runs.Clear();
                foreach (var record in records)
                {
                    Runs.Add(TestRunViewModel.FromRecord(record));
                }

                if (Runs.Count > 0)
                    SelectedRun = Runs[0];

                RebuildTrendData();
                NotifyRunCountChanged();
            });
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

        if (DialogService is not null)
        {
            var confirmed = await DialogService.ShowConfirmAsync(
                "Delete Run",
                $"Delete the run from {runVm.Timestamp}?\n\nThis action cannot be undone.");

            if (!confirmed)
                return;
        }

        await Task.Run(() => _store.Delete(_datasetFolderPath, runVm.Record));

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Runs.Remove(runVm);
            if (SelectedRun == runVm)
                SelectedRun = Runs.Count > 0 ? Runs[0] : null;

            RebuildTrendData();
            NotifyRunCountChanged();
        });
    }

    /// <summary>
    /// Starts a live analysis session, clearing previous live scores.
    /// </summary>
    public void BeginLiveAnalysis()
    {
        LiveCheckScores.Clear();
        CurrentCheckName = string.Empty;
        SelectedRun = null;
        IsLiveAnalyzing = true;
    }

    /// <summary>
    /// Reports that a check has started running.
    /// </summary>
    public void OnCheckStarted(string checkName)
    {
        CurrentCheckName = checkName;

        // Add a placeholder entry for the running check
        var existing = LiveCheckScores.FirstOrDefault(c => c.CheckName == checkName);
        if (existing is null)
        {
            LiveCheckScores.Add(new LiveCheckScoreViewModel
            {
                CheckName = checkName,
                IsRunning = true
            });
        }
    }

    /// <summary>
    /// Reports a completed check score during live analysis.
    /// </summary>
    public void OnCheckScoreReported(CheckScore score)
    {
        var existing = LiveCheckScores.FirstOrDefault(c => c.CheckName == score.CheckName);
        if (existing is not null)
        {
            existing.Score = score.Score;
            existing.CategoryName = score.Category.ToString();
            existing.ScoreColor = TestRunViewModel.GetScoreColor(score.Score);
            existing.IsRunning = false;
        }
        else
        {
            LiveCheckScores.Add(new LiveCheckScoreViewModel
            {
                CheckName = score.CheckName,
                Score = score.Score,
                CategoryName = score.Category.ToString(),
                ScoreColor = TestRunViewModel.GetScoreColor(score.Score),
                IsRunning = false
            });
        }
    }

    /// <summary>
    /// Ends the live analysis session.
    /// </summary>
    public void EndLiveAnalysis()
    {
        IsLiveAnalyzing = false;
        CurrentCheckName = string.Empty;
    }

    private void NotifyRunCountChanged()
    {
        OnPropertyChanged(nameof(HasRuns));
        OnPropertyChanged(nameof(HasTrendData));
    }

    /// <summary>
    /// Rebuilds the trend data points from the current run list (oldest first).
    /// </summary>
    private void RebuildTrendData()
    {
        TrendDataPoints.Clear();
        foreach (var run in Runs.Reverse())
        {
            if (run.CompositeScore is not { } score)
                continue;

            var dateLabel = run.Record.AnalyzedAtUtc.ToLocalTime().ToString("dd MMM");
            var tooltip = $"{score:F0} — {run.Timestamp}";
            TrendDataPoints.Add(new ScoreTrendDataPoint(score, dateLabel, tooltip));
        }
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

    /// <summary>Expandable issue wrappers from this run.</summary>
    public IReadOnlyList<ExpandableIssueViewModel> Issues { get; init; } = [];

    /// <summary>Per-check score breakdowns (caption + image).</summary>
    public IReadOnlyList<CheckScoreViewModel> CheckScores { get; init; } = [];

    /// <summary>Number of auto-fixable issues.</summary>
    public required int FixableIssueCount { get; init; }

    /// <summary>Number of checks that were executed.</summary>
    public required int ChecksRun { get; init; }

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

        var checkScoreVms = record.CheckScores
            .Select(cs => new CheckScoreViewModel
            {
                CheckName = cs.CheckName,
                Score = cs.Score,
                ScoreColor = GetScoreColor(cs.Score),
                CategoryName = FormatCategoryName(cs.Category)
            })
            .ToList();

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
            Issues = record.Issues.Select(ExpandableIssueViewModel.FromSnapshot).ToList(),
            CheckScores = checkScoreVms,
            FixableIssueCount = record.Summary.FixableIssueCount,
            ChecksRun = record.Summary.ChecksRun
        };
    }

    internal static string GetScoreColor(double score) => score switch
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

/// <summary>
/// ViewModel for displaying a single per-check score in the detail view.
/// </summary>
public class CheckScoreViewModel
{
    /// <summary>Name of the check (e.g. "Exposure Analysis").</summary>
    public required string CheckName { get; init; }

    /// <summary>Score value (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Color hex for the score display.</summary>
    public required string ScoreColor { get; init; }

    /// <summary>Human-readable category name.</summary>
    public required string CategoryName { get; init; }
}

/// <summary>
/// Observable ViewModel for a check score during live analysis, supporting in-progress state.
/// </summary>
public partial class LiveCheckScoreViewModel : ObservableObject
{
    private double _score;
    private string _categoryName = string.Empty;
    private string _scoreColor = "#888";
    private bool _isRunning = true;

    /// <summary>Name of the check.</summary>
    public required string CheckName { get; init; }

    /// <summary>Score value (0–100), updated when the check completes.</summary>
    public double Score
    {
        get => _score;
        set => SetProperty(ref _score, value);
    }

    /// <summary>Human-readable category name.</summary>
    public string CategoryName
    {
        get => _categoryName;
        set => SetProperty(ref _categoryName, value);
    }

    /// <summary>Color hex for the score display.</summary>
    public string ScoreColor
    {
        get => _scoreColor;
        set => SetProperty(ref _scoreColor, value);
    }

    /// <summary>Whether this check is still running.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        set => SetProperty(ref _isRunning, value);
    }
}

/// <summary>
/// Wraps a <see cref="RunIssueSnapshot"/> with expand/collapse state so the
/// Test Runs detail view can show affected files inline.
/// </summary>
public partial class ExpandableIssueViewModel : ObservableObject
{
    private bool _isExpanded;

    /// <summary>The underlying issue snapshot.</summary>
    public required RunIssueSnapshot Snapshot { get; init; }

    /// <summary>Whether the affected-files list is expanded.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Whether this issue has affected files to show.</summary>
    public bool HasFiles => Snapshot.AffectedFiles.Count > 0;

    /// <summary>Display-friendly file names (just the file name, not the full path).</summary>
    public IReadOnlyList<string> FileNames { get; private init; } = [];

    /// <summary>Toggles the expanded state.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>
    /// Creates an <see cref="ExpandableIssueViewModel"/> from a stored snapshot.
    /// </summary>
    public static ExpandableIssueViewModel FromSnapshot(RunIssueSnapshot snapshot) => new()
    {
        Snapshot = snapshot,
        FileNames = snapshot.AffectedFiles
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .ToList()!
    };
}
