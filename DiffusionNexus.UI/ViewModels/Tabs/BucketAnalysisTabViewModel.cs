using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Lightweight row model for the bucket distribution bar chart.
/// </summary>
public class BucketBarViewModel
{
    /// <summary>Bucket label (e.g. "1024 × 768").</summary>
    public required string Label { get; init; }

    /// <summary>Number of images in this bucket.</summary>
    public required int ImageCount { get; init; }

    /// <summary>Width of the bar in pixels (scaled relative to the largest bucket).</summary>
    public required double BarWidth { get; init; }

    /// <summary>True if this bucket contains only 1 image (visual warning hint).</summary>
    public required bool IsSingleImage { get; init; }
}

/// <summary>
/// Row model for the per-image assignment table.
/// </summary>
public class ImageAssignmentRowViewModel
{
    /// <summary>File name (no path).</summary>
    public required string FileName { get; init; }

    /// <summary>Original resolution text (e.g. "1920 × 1080").</summary>
    public required string OriginalResolution { get; init; }

    /// <summary>Assigned bucket resolution text.</summary>
    public required string BucketResolution { get; init; }

    /// <summary>Crop percentage formatted (e.g. "12.3%").</summary>
    public required string CropPercentage { get; init; }

    /// <summary>Scale factor formatted (e.g. "1.500×").</summary>
    public required string ScaleFactor { get; init; }

    /// <summary>True when scale factor ≥ 2.0 (heavy upscale).</summary>
    public required bool HasUpscaleWarning { get; init; }

    /// <summary>True when crop ≥ 15%.</summary>
    public required bool HasCropWarning { get; init; }
}

/// <summary>
/// ViewModel for the Bucket Analysis tab.
/// Simulates kohya_ss-style bucketing and shows distribution, per-image details, and issues.
/// </summary>
public class BucketAnalysisTabViewModel : ObservableObject
{
    private const double MaxBarPixels = 300.0;

    private readonly BucketAnalyzer? _analyzer;

    private string _folderPath = string.Empty;
    private int _baseResolution = 1024;
    private int _stepSize = 64;
    private int _minDimension = 256;
    private int _maxDimension = 2048;
    private double _maxAspectRatio = 2.0;
    private int _batchSize = 1;
    private bool _isAnalyzing;
    private bool _hasResults;
    private double _distributionScore;
    private string _scoreLabel = string.Empty;
    private string _summaryText = string.Empty;

    /// <summary>
    /// Creates a new <see cref="BucketAnalysisTabViewModel"/>.
    /// </summary>
    /// <param name="analyzer">The bucket analyzer service.</param>
    public BucketAnalysisTabViewModel(BucketAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public BucketAnalysisTabViewModel()
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
    }

    #region Observable Properties

    /// <summary>Base resolution for bucket generation (target area = BaseResolution²).</summary>
    public int BaseResolution
    {
        get => _baseResolution;
        set => SetProperty(ref _baseResolution, value);
    }

    /// <summary>Step size for bucket dimension increments.</summary>
    public int StepSize
    {
        get => _stepSize;
        set => SetProperty(ref _stepSize, value);
    }

    /// <summary>Minimum dimension for any bucket side.</summary>
    public int MinDimension
    {
        get => _minDimension;
        set => SetProperty(ref _minDimension, value);
    }

    /// <summary>Maximum dimension for any bucket side.</summary>
    public int MaxDimension
    {
        get => _maxDimension;
        set => SetProperty(ref _maxDimension, value);
    }

    /// <summary>Maximum aspect ratio (width/height or height/width).</summary>
    public double MaxAspectRatio
    {
        get => _maxAspectRatio;
        set => SetProperty(ref _maxAspectRatio, value);
    }

    /// <summary>Training batch size.</summary>
    public int BatchSize
    {
        get => _batchSize;
        set => SetProperty(ref _batchSize, value);
    }

    /// <summary>True while analysis is running.</summary>
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (SetProperty(ref _isAnalyzing, value))
            {
                AnalyzeCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>True when results are available to display.</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set => SetProperty(ref _hasResults, value);
    }

    /// <summary>Distribution evenness score (0–100).</summary>
    public double DistributionScore
    {
        get => _distributionScore;
        private set => SetProperty(ref _distributionScore, value);
    }

    /// <summary>Score label (Poor / Fair / Good / Excellent).</summary>
    public string ScoreLabel
    {
        get => _scoreLabel;
        private set => SetProperty(ref _scoreLabel, value);
    }

    /// <summary>Summary text describing the analysis results.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    #endregion

    #region Collections

    /// <summary>Bar chart data for used buckets.</summary>
    public ObservableCollection<BucketBarViewModel> UsedBuckets { get; } = [];

    /// <summary>Per-image assignment rows.</summary>
    public ObservableCollection<ImageAssignmentRowViewModel> ImageAssignments { get; } = [];

    /// <summary>Issues detected during analysis.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    #endregion

    #region Commands

    /// <summary>Command to start the bucket analysis.</summary>
    public AsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// Raised after analysis completes with the score, issue count, and label.
    /// Used by the parent dashboard to update its summary card.
    /// </summary>
    public event Action<double, int, string>? AnalysisCompleted;

    private bool CanAnalyze => !IsAnalyzing && !string.IsNullOrWhiteSpace(_folderPath);

    #endregion

    #region Public Methods

    /// <summary>
    /// Updates the dataset folder path and refreshes command state.
    /// Called by the parent view model when the dataset context changes.
    /// </summary>
    /// <param name="folderPath">Absolute path to the dataset folder.</param>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        ClearResults();
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    #endregion

    #region Private Methods

    private async Task AnalyzeAsync()
    {
        if (_analyzer is null || string.IsNullOrWhiteSpace(_folderPath))
            return;

        IsAnalyzing = true;
        ClearResults();

        try
        {
            var config = BuildConfig();
            var result = await Task.Run(() => _analyzer.AnalyzeFolder(_folderPath, config));
            ApplyResult(result);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private BucketConfig BuildConfig() => new()
    {
        BaseResolution = _baseResolution,
        StepSize = _stepSize,
        MinDimension = _minDimension,
        MaxDimension = _maxDimension,
        MaxAspectRatio = _maxAspectRatio,
        BatchSize = _batchSize
    };

    private void ApplyResult(BucketAnalysisResult result)
    {
        // Bar chart
        int maxCount = result.Distribution.Count > 0
            ? result.Distribution.Max(d => d.ImageCount)
            : 1;

        foreach (var entry in result.Distribution)
        {
            double barWidth = maxCount > 0
                ? (double)entry.ImageCount / maxCount * MaxBarPixels
                : 0;

            UsedBuckets.Add(new BucketBarViewModel
            {
                Label = entry.Bucket.Label,
                ImageCount = entry.ImageCount,
                BarWidth = Math.Max(barWidth, 2), // minimum visible bar
                IsSingleImage = entry.ImageCount == 1
            });
        }

        // Per-image table
        foreach (var a in result.Assignments)
        {
            ImageAssignments.Add(new ImageAssignmentRowViewModel
            {
                FileName = a.FileName,
                OriginalResolution = $"{a.OriginalWidth} × {a.OriginalHeight}",
                BucketResolution = a.AssignedBucket.Label,
                CropPercentage = $"{a.CropPercentage:F1}%",
                ScaleFactor = $"{a.ScaleFactor:F3}×",
                HasUpscaleWarning = a.ScaleFactor >= BucketAnalyzer.UpscaleCriticalThreshold,
                HasCropWarning = a.CropPercentage >= BucketAnalyzer.CropWarningThreshold
            });
        }

        // Issues
        foreach (var issue in result.Issues)
            Issues.Add(issue);

        // Score
        DistributionScore = result.DistributionScore;
        ScoreLabel = result.ScoreLabel;

        // Summary
        SummaryText = $"{result.Assignments.Count} images assigned to {result.Distribution.Count} bucket(s) "
                    + $"out of {result.AllBuckets.Count} available. "
                    + $"Distribution score: {result.DistributionScore:F1} ({result.ScoreLabel}).";

        HasResults = result.Assignments.Count > 0;

        AnalysisCompleted?.Invoke(result.DistributionScore, result.Issues.Count, result.ScoreLabel);
    }

    private void ClearResults()
    {
        UsedBuckets.Clear();
        ImageAssignments.Clear();
        Issues.Clear();
        DistributionScore = 0;
        ScoreLabel = string.Empty;
        SummaryText = string.Empty;
        HasResults = false;
    }

    #endregion
}
