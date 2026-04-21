using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
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
    private readonly AnalysisRunStore _runStore;
    private readonly DuplicateDetector? _duplicateDetector;
    private CancellationTokenSource? _analysisCts;

    private string _datasetFolderPath = string.Empty;
    private string _datasetLabel = string.Empty;
    private string _triggerWord = string.Empty;
    private int _currentVersion;
    private LoraType _selectedLoraType = LoraType.Character;
    private bool _isAnalyzing;
    private string _analysisStatusText = string.Empty;
    private double _analysisProgress;
    private string _summaryText = string.Empty;
    private Issue? _selectedIssue;
    private AnalysisReport? _lastReport;

    private int _selectedTabIndex;
    private double _compositeScore;
    private string _compositeScoreLabel = string.Empty;
    private string _compositeScoreColor = "#666";
    private bool _hasCompositeScore;
    private string _scoreCoverageText = string.Empty;

    /// <summary>
    /// Creates a new <see cref="DatasetQualityTabViewModel"/>.
    /// </summary>
    /// <param name="pipeline">The analysis pipeline for running quality checks.</param>
    /// <param name="runStore">Store for persisting and loading analysis run history.</param>
    /// <param name="bucketAnalyzer">Optional bucket analyzer for image bucketing analysis.</param>
    /// <param name="imageChecks">Optional image quality check implementations.</param>
    public DatasetQualityTabViewModel(
        AnalysisPipeline pipeline,
        AnalysisRunStore runStore,
        BucketAnalyzer? bucketAnalyzer = null,
        IEnumerable<IImageQualityCheck>? imageChecks = null,
        DuplicateDetector? duplicateDetector = null,
        ColorDistributionAnalyzer? colorDistributionAnalyzer = null)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        ArgumentNullException.ThrowIfNull(runStore);
        _pipeline = pipeline;
        _runStore = runStore;
        _duplicateDetector = duplicateDetector;

        ImageAnalysisTab = bucketAnalyzer is not null
            ? new ImageAnalysisTabViewModel(bucketAnalyzer, imageChecks, duplicateDetector, colorDistributionAnalyzer)
            : new ImageAnalysisTabViewModel();
        ImageAnalysisTab.FixDistributionRequested += OnFixDistributionRequested;

        TestRunsTab = new TestRunsTabViewModel(runStore);

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        AnalyzeAllCommand = new AsyncRelayCommand(AnalyzeAllAsync, () => CanAnalyze);
        CancelAnalysisCommand = new RelayCommand(CancelAnalysis, () => CanCancelAnalysis);
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
        _runStore = new AnalysisRunStore();

        ImageAnalysisTab = new ImageAnalysisTabViewModel();
        ImageAnalysisTab.FixDistributionRequested += OnFixDistributionRequested;

        TestRunsTab = new TestRunsTabViewModel();

        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        AnalyzeAllCommand = new AsyncRelayCommand(AnalyzeAllAsync, () => CanAnalyze);
        CancelAnalysisCommand = new RelayCommand(CancelAnalysis, () => CanCancelAnalysis);
        ApplyFixCommand = new AsyncRelayCommand<FixSuggestion?>(ApplyFixAsync);
        BackupCaptionsCommand = new AsyncRelayCommand(BackupCaptionsAsync);
        ExpandAllFilesCommand = new RelayCommand(ExpandAllFiles, () => EditableAffectedFiles.Count > 0);
        CollapseAllFilesCommand = new RelayCommand(CollapseAllFiles, () => EditableAffectedFiles.Count > 0);
    }

    #region IDialogServiceAware

    /// <inheritdoc />
    public IDialogService? DialogService
    {
        get => field;
        set
        {
            field = value;
            TestRunsTab.DialogService = value;
            ImageAnalysisTab.ImageQualityTab.DialogService = value;
            ImageAnalysisTab.ColorDistributionTab.DialogService = value;
        }
    }

    #endregion

    /// <summary>
    /// ViewModel for the embedded Image Analysis dashboard tab.
    /// </summary>
    public ImageAnalysisTabViewModel ImageAnalysisTab { get; }

    /// <summary>
    /// ViewModel for the Test Runs history sub-tab.
    /// </summary>
    public TestRunsTabViewModel TestRunsTab { get; }

    /// <summary>
    /// Raised when a child analysis tab requests navigation to Batch Crop/Scale.
    /// Bubbles up to <see cref="DatasetManagementViewModel"/>.
    /// </summary>
    public event Action? FixDistributionRequested;

    #region Observable Properties

    /// <summary>
    /// Index of the currently selected sub-tab (0 = Image Analysis, 1 = Caption Quality, 2 = Test Runs).
    /// </summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

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
                OnPropertyChanged(nameof(CanCancelAnalysis));
                AnalyzeCommand.NotifyCanExecuteChanged();
                AnalyzeAllCommand.NotifyCanExecuteChanged();
                CancelAnalysisCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>
    /// Whether a cancellable analysis is currently in progress.
    /// </summary>
    public bool CanCancelAnalysis => IsAnalyzing && _analysisCts is not null && !_analysisCts.IsCancellationRequested;

    /// <summary>
    /// Human-readable status text showing the current analysis phase (e.g. "Running Spell Check…").
    /// </summary>
    public string AnalysisStatusText
    {
        get => _analysisStatusText;
        private set => SetProperty(ref _analysisStatusText, value);
    }

    /// <summary>
    /// Overall analysis progress (0.0 – 1.0) across all pipeline phases.
    /// </summary>
    public double AnalysisProgress
    {
        get => _analysisProgress;
        private set => SetProperty(ref _analysisProgress, value);
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

    /// <summary>
    /// Composite quality score value (0–100).
    /// </summary>
    public double CompositeScore
    {
        get => _compositeScore;
        private set => SetProperty(ref _compositeScore, value);
    }

    /// <summary>
    /// Human-readable label for the composite score (Poor/Fair/Good/Excellent).
    /// </summary>
    public string CompositeScoreLabel
    {
        get => _compositeScoreLabel;
        private set => SetProperty(ref _compositeScoreLabel, value);
    }

    /// <summary>
    /// Color hex for the composite score display.
    /// </summary>
    public string CompositeScoreColor
    {
        get => _compositeScoreColor;
        private set => SetProperty(ref _compositeScoreColor, value);
    }

    /// <summary>
    /// Whether a composite score is available.
    /// </summary>
    public bool HasCompositeScore
    {
        get => _hasCompositeScore;
        private set => SetProperty(ref _hasCompositeScore, value);
    }

    /// <summary>
    /// Coverage text (e.g. "Based on 2 of 4 categories").
    /// </summary>
    public string ScoreCoverageText
    {
        get => _scoreCoverageText;
        private set => SetProperty(ref _scoreCoverageText, value);
    }

    /// <summary>
    /// Category score breakdowns for the composite score display.
    /// </summary>
    public ObservableCollection<CategoryScoreViewModel> CategoryScores { get; } = [];

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
    /// Used as a fallback when the issue has no fix suggestions.
    /// </summary>
    public ObservableCollection<EditableAffectedFile> EditableAffectedFiles { get; } = [];

    /// <summary>
    /// Fix suggestions with inline file editors attached.
    /// Each suggestion directly shows its affected files with editing capabilities.
    /// </summary>
    public ObservableCollection<FixSuggestionViewModel> MergedFixSuggestions { get; } = [];

    /// <summary>
    /// Whether the selected issue has fix suggestions with merged file editors.
    /// </summary>
    public bool HasFixSuggestions => MergedFixSuggestions.Count > 0;

    /// <summary>
    /// Whether the selected issue has affected files but no fix suggestions.
    /// Controls the fallback "Affected Files" display.
    /// </summary>
    public bool HasAffectedFilesOnly => EditableAffectedFiles.Count > 0 && MergedFixSuggestions.Count == 0;

    #endregion

    #region Commands

    /// <summary>
    /// Runs the quality analysis pipeline on the active dataset version (caption checks only).
    /// </summary>
    public IAsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// Runs the full analysis pipeline — captions, image quality, and bucket analysis
    /// in one pass. Updates the composite score and propagates results to all sub-tabs.
    /// </summary>
    public IAsyncRelayCommand AnalyzeAllCommand { get; }

    /// <summary>
    /// Cancels the in-progress analysis run started by <see cref="AnalyzeAllCommand"/>.
    /// </summary>
    public IRelayCommand CancelAnalysisCommand { get; }

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

    private async Task AnalyzeAllAsync()
    {
        if (_pipeline is null || !HasDatasetContext) return;

        SelectedTabIndex = 2;
        _analysisCts?.Dispose();
        _analysisCts = new CancellationTokenSource();
        var cancellationToken = _analysisCts.Token;
        IsAnalyzing = true;
        AnalysisStatusText = "Starting analysis…";
        AnalysisProgress = 0.0;
        TestRunsTab.BeginLiveAnalysis();
        try
        {
            var config = BuildConfig();
            var stopwatch = Stopwatch.StartNew();

            var statusProgress = new Progress<string>(s =>
            {
                var elapsed = stopwatch.Elapsed;
                var progress = AnalysisProgress;

                // Only notify TestRunsTab for top-level check starts (e.g. "Running Spell Check…")
                // Skip per-item progress like "Running Spell Check… caption 3 of 126"
                if (s.StartsWith("Running ", StringComparison.Ordinal) && s.EndsWith('…') && !s.Contains(" of ", StringComparison.Ordinal))
                {
                    TestRunsTab.OnCheckStarted(s[8..^1]); // strip "Running " prefix and "…" suffix
                }

                if (progress > 0.05 && elapsed.TotalSeconds > 2)
                {
                    var totalEstimated = TimeSpan.FromTicks((long)(elapsed.Ticks / progress));
                    var remaining = totalEstimated - elapsed;

                    if (remaining.TotalSeconds >= 1)
                    {
                        AnalysisStatusText = $"{s} — ~{FormatTimeRemaining(remaining)} remaining";
                        return;
                    }
                }

                AnalysisStatusText = s;
            });
            var percentProgress = new Progress<double>(p => AnalysisProgress = p);
            var checkScoreProgress = new Progress<CheckScore>(score =>
                TestRunsTab.OnCheckScoreReported(score));

            // Run the full pipeline: captions + image quality + bucket scoring
            var report = await _pipeline.AnalyzeFullAsync(config, new BucketConfig
            {
                BaseResolution = ImageAnalysisTab.BucketAnalysisTab.BaseResolution,
                StepSize = ImageAnalysisTab.BucketAnalysisTab.StepSize,
                MinDimension = ImageAnalysisTab.BucketAnalysisTab.MinDimension,
                MaxDimension = ImageAnalysisTab.BucketAnalysisTab.MaxDimension,
                MaxAspectRatio = ImageAnalysisTab.BucketAnalysisTab.MaxAspectRatio,
                BatchSize = ImageAnalysisTab.BucketAnalysisTab.BatchSize
            }, percentProgress, statusProgress: statusProgress, checkScoreProgress: checkScoreProgress, cancellationToken: cancellationToken);

            // Apply caption issues + composite score
            ApplyReport(report);

            // Propagate image quality results to the Image Quality sub-tab
            if (report.ImageCheckResults.Count > 0)
            {
                ImageAnalysisTab.ImageQualityTab.ApplyResults(report.ImageCheckResults);
            }

            // Propagate duplicate detection results to the Duplicate Detection sub-tab
            if (_duplicateDetector is not null)
            {
                var dupResult = report.ImageCheckResults
                    .FirstOrDefault(r => r.CheckName == DuplicateDetector.CheckDisplayName);
                if (dupResult is not null)
                {
                    ImageAnalysisTab.DuplicateDetectionTab.ApplyResults(dupResult, _duplicateDetector.LastClusters);
                }
            }

            // Propagate color distribution results to the Color Distribution sub-tab
            var colorResult = report.ImageCheckResults
                .FirstOrDefault(r => r.CheckName == ColorDistributionAnalyzer.CheckDisplayName);
            if (colorResult is not null)
            {
                ImageAnalysisTab.ColorDistributionTab.ApplyResults(colorResult);
            }

            // Run bucket analysis through the sub-tab so its full UI (bars, assignments table) gets populated
            AnalysisStatusText = "Populating bucket analysis UI…";
            await ImageAnalysisTab.BucketAnalysisTab.RunAnalysisAsync();

            // Persist the run record for the Test Runs history tab
            stopwatch.Stop();
            AnalysisStatusText = "Saving run record…";
            var runRecord = new AnalysisRunRecord
            {
                AnalyzedAtUtc = report.AnalyzedAt,
                Version = _currentVersion,
                DatasetLabel = DatasetLabel,
                LoraType = SelectedLoraType,
                Summary = report.Summary,
                CompositeScore = report.CompositeScore,
                CheckScores = report.CheckScores,
                Issues = report.Issues.Select(RunIssueSnapshot.FromIssue).ToList(),
                Duration = stopwatch.Elapsed
            };
            await _runStore.SaveAsync(_datasetFolderPath, runRecord);
            await TestRunsTab.OnRunSavedAsync();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AnalysisStatusText = "Analysis cancelled";
        }
        finally
        {
            TestRunsTab.EndLiveAnalysis();
            IsAnalyzing = false;
            AnalysisProgress = 0.0;
            _analysisCts?.Dispose();
            _analysisCts = null;
            OnPropertyChanged(nameof(CanCancelAnalysis));
            CancelAnalysisCommand.NotifyCanExecuteChanged();
        }
    }

    private void CancelAnalysis()
    {
        if (_analysisCts is null || _analysisCts.IsCancellationRequested)
            return;

        _analysisCts.Cancel();
        AnalysisStatusText = "Cancelling…";
        OnPropertyChanged(nameof(CanCancelAnalysis));
        CancelAnalysisCommand.NotifyCanExecuteChanged();
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

        // Only show caption-domain issues here; image issues are displayed in the Image Analysis tab
        Issues.Clear();
        foreach (var issue in report.Issues)
        {
            if (issue.Domain == CheckDomain.Caption)
            {
                Issues.Add(issue);
            }
        }

        SelectedIssue = Issues.Count > 0 ? Issues[0] : null;
        SummaryText = FormatSummary(report.Summary);
        ApplyCompositeScore(report.CompositeScore);

        OnPropertyChanged(nameof(HasResults));
    }

    /// <summary>
    /// Updates composite score display properties from a score result.
    /// </summary>
    private void ApplyCompositeScore(CompositeScoreResult? result)
    {
        CategoryScores.Clear();

        if (result is null)
        {
            HasCompositeScore = false;
            CompositeScore = 0;
            CompositeScoreLabel = string.Empty;
            CompositeScoreColor = "#666";
            ScoreCoverageText = string.Empty;
            return;
        }

        HasCompositeScore = true;
        CompositeScore = result.Score;
        CompositeScoreLabel = result.Label;
        CompositeScoreColor = GetScoreColor(result.Score);
        ScoreCoverageText = result.ParticipatingCategories < result.TotalCategories
            ? $"Based on {result.ParticipatingCategories} of {result.TotalCategories} categories"
            : $"All {result.TotalCategories} categories";

        foreach (var cat in result.CategoryScores)
        {
            CategoryScores.Add(new CategoryScoreViewModel
            {
                CategoryName = FormatCategoryName(cat.Category),
                Score = cat.Score,
                ScoreColor = GetScoreColor(cat.Score),
                Weight = $"{cat.Weight * 100:F0}%"
            });
        }
    }

    private static string GetScoreColor(double score) => score switch
    {
        >= 80 => "#4CAF50",  // Green
        >= 65 => "#8BC34A",  // Light green
        >= 40 => "#FFA726",  // Orange
        _ => "#FF6B6B"       // Red
    };

    /// <summary>
    /// Formats a <see cref="TimeSpan"/> as a compact human-readable remaining time string.
    /// </summary>
    private static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining.TotalMinutes >= 1)
            return $"{(int)remaining.TotalMinutes}m {remaining.Seconds}s";
        return $"{Math.Max(1, (int)remaining.TotalSeconds)}s";
    }

    private static string FormatCategoryName(QualityScoreCategory category) => category switch
    {
        QualityScoreCategory.ImageTechnicalQuality => "Image Quality",
        QualityScoreCategory.CaptionQuality => "Caption Quality",
        QualityScoreCategory.DatasetConsistency => "Consistency",
        QualityScoreCategory.DatasetCompleteness => "Completeness",
        _ => category.ToString()
    };

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
        _currentVersion = version;
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
        ApplyCompositeScore(null);

        OnPropertyChanged(nameof(HasDatasetContext));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();

        // Forward folder context to image analysis dashboard
        ImageAnalysisTab.RefreshContext(_datasetFolderPath);

        // Load run history for the Test Runs tab
        _ = TestRunsTab.RefreshContextAsync(_datasetFolderPath);
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
    /// Populates <see cref="EditableAffectedFiles"/> and <see cref="MergedFixSuggestions"/>
    /// from the given issue. When the issue has fix suggestions, each suggestion is wrapped
    /// with inline file editors so the user sees affected files directly within the fix.
    /// </summary>
    private void PopulateEditableFiles(Issue? issue)
    {
        EditableAffectedFiles.Clear();
        MergedFixSuggestions.Clear();

        if (issue is null ||
            (issue.AffectedFiles is not { Count: > 0 } && issue.FixSuggestions is not { Count: > 0 }))
        {
            OnPropertyChanged(nameof(HasFixSuggestions));
            OnPropertyChanged(nameof(HasAffectedFilesOnly));
            ExpandAllFilesCommand.NotifyCanExecuteChanged();
            CollapseAllFilesCommand.NotifyCanExecuteChanged();
            return;
        }

        // Extract recommended word range from metadata if present
        int? recMin = issue.Metadata.TryGetValue("RecommendedMinWords", out var minStr)
            && int.TryParse(minStr, out var minVal) ? minVal : null;
        int? recMax = issue.Metadata.TryGetValue("RecommendedMaxWords", out var maxStr)
            && int.TryParse(maxStr, out var maxVal) ? maxVal : null;

        // Create a shared lookup so the same file reuses one EditableAffectedFile instance
        var fileLookup = new Dictionary<string, EditableAffectedFile>(StringComparer.OrdinalIgnoreCase);

        EditableAffectedFile GetOrCreateEditor(string filePath)
        {
            if (fileLookup.TryGetValue(filePath, out var existing))
                return existing;

            var editor = new EditableAffectedFile(filePath, OnCaptionSavedAsync, recMin, recMax);
            fileLookup[filePath] = editor;
            return editor;
        }

        // Populate editable file list from affected files
        if (issue.AffectedFiles is { Count: > 0 })
        {
            foreach (var filePath in issue.AffectedFiles)
            {
                var ext = Path.GetExtension(filePath);
                if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".caption", StringComparison.OrdinalIgnoreCase))
                {
                    EditableAffectedFiles.Add(GetOrCreateEditor(filePath));
                }
            }
        }

        // Wrap fix suggestions with inline editors — each suggestion gets its own
        // EditableAffectedFile instances so expand/collapse state is independent.
        if (issue.FixSuggestions is { Count: > 0 })
        {
            foreach (var suggestion in issue.FixSuggestions)
            {
                var vm = new FixSuggestionViewModel
                {
                    Description = suggestion.Description,
                    Suggestion = suggestion
                };

                foreach (var edit in suggestion.Edits)
                {
                    vm.Edits.Add(new FixEditWithEditor
                    {
                        Edit = edit,
                        Editor = new EditableAffectedFile(edit.FilePath, OnCaptionSavedAsync, recMin, recMax)
                    });
                }

                MergedFixSuggestions.Add(vm);
            }
        }

        OnPropertyChanged(nameof(HasFixSuggestions));
        OnPropertyChanged(nameof(HasAffectedFilesOnly));
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
            var report = await Task.Run(() => _pipeline.Analyze(config));
            ApplyReport(report);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private void OnFixDistributionRequested()
    {
        FixDistributionRequested?.Invoke();
    }

    #endregion
}

/// <summary>
/// Lightweight ViewModel for displaying a single category score in the composite breakdown.
/// </summary>
public class CategoryScoreViewModel
{
    /// <summary>Human-readable category name.</summary>
    public required string CategoryName { get; init; }

    /// <summary>Score value (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Color hex for the score display.</summary>
    public required string ScoreColor { get; init; }

    /// <summary>Category weight as percentage string (e.g. "30%").</summary>
    public required string Weight { get; init; }
}
