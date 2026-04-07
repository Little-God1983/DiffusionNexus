using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Row model for the per-image quality results table.
/// </summary>
public class ImageQualityRowViewModel
{
    /// <summary>File name (no path).</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute file path.</summary>
    public required string FilePath { get; init; }

    /// <summary>Blur/sharpness score (0–100), or null if not checked.</summary>
    public double? BlurScore { get; init; }

    /// <summary>Exposure score (0–100), or null if not checked.</summary>
    public double? ExposureScore { get; init; }

    /// <summary>Overall average score across all checks.</summary>
    public required double OverallScore { get; init; }

    /// <summary>Blur detail text.</summary>
    public string? BlurDetail { get; init; }

    /// <summary>Exposure detail text.</summary>
    public string? ExposureDetail { get; init; }

    /// <summary>True when the overall score is below the warning threshold.</summary>
    public bool HasWarning => OverallScore < 50;

    /// <summary>Color hex for the overall score.</summary>
    public string ScoreColor => OverallScore switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };
}

/// <summary>
/// ViewModel for the Image Quality detail section within the Image Analysis dashboard.
/// Displays per-image blur and exposure scores from the latest analysis.
/// </summary>
public class ImageQualityTabViewModel : ObservableObject
{
    private readonly IEnumerable<IImageQualityCheck>? _imageChecks;

    private string _folderPath = string.Empty;
    private bool _isAnalyzing;
    private bool _hasResults;
    private double _overallScore;
    private string _overallScoreLabel = string.Empty;
    private int _issueCount;
    private string _summaryText = "Not analyzed yet";

    /// <summary>
    /// Creates a new <see cref="ImageQualityTabViewModel"/>.
    /// </summary>
    public ImageQualityTabViewModel(IEnumerable<IImageQualityCheck> imageChecks)
    {
        ArgumentNullException.ThrowIfNull(imageChecks);
        _imageChecks = imageChecks;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ImageQualityTabViewModel()
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
    }

    /// <summary>Per-image quality results.</summary>
    public ObservableCollection<ImageQualityRowViewModel> ImageRows { get; } = [];

    /// <summary>Issues detected during analysis.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>Whether analysis is running.</summary>
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

    /// <summary>Whether results are available.</summary>
    public bool HasResults
    {
        get => _hasResults;
        private set => SetProperty(ref _hasResults, value);
    }

    /// <summary>Can the analyze command execute.</summary>
    public bool CanAnalyze => !string.IsNullOrEmpty(_folderPath) && !IsAnalyzing;

    /// <summary>Overall image quality score (average across all images).</summary>
    public double OverallScore
    {
        get => _overallScore;
        private set => SetProperty(ref _overallScore, value);
    }

    /// <summary>Label for the overall score.</summary>
    public string OverallScoreLabel
    {
        get => _overallScoreLabel;
        private set => SetProperty(ref _overallScoreLabel, value);
    }

    /// <summary>Number of issues detected.</summary>
    public int IssueCount
    {
        get => _issueCount;
        private set => SetProperty(ref _issueCount, value);
    }

    /// <summary>Summary text for the dashboard card.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>Analyze command.</summary>
    public IAsyncRelayCommand AnalyzeCommand { get; }

    /// <summary>
    /// Raised when analysis completes, with (score, issueCount, label).
    /// </summary>
    public event Action<double, int, string>? AnalysisCompleted;

    /// <summary>
    /// Updates the dataset folder path and resets state.
    /// </summary>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        HasResults = false;
        ImageRows.Clear();
        Issues.Clear();
        SummaryText = "Not analyzed yet";
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Applies externally computed image check results (when run via the full pipeline).
    /// </summary>
    public void ApplyResults(IReadOnlyList<ImageCheckResult> results)
    {
        ImageRows.Clear();
        Issues.Clear();

        if (results.Count == 0)
        {
            HasResults = false;
            SummaryText = "No image quality checks were run.";
            return;
        }

        // Collect all per-image scores by file path
        var byFile = new Dictionary<string, (double? blur, string? blurDetail, double? exposure, string? exposureDetail)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            foreach (var pis in result.PerImageScores)
            {
                if (!byFile.TryGetValue(pis.FilePath, out var entry))
                    entry = (null, null, null, null);

                if (result.CheckName == "Blur Detection")
                    entry = (pis.Score, pis.Detail, entry.exposure, entry.exposureDetail);
                else if (result.CheckName == "Exposure Analysis")
                    entry = (entry.blur, entry.blurDetail, pis.Score, pis.Detail);

                byFile[pis.FilePath] = entry;
            }

            foreach (var issue in result.Issues)
                Issues.Add(issue);
        }

        // Build rows
        foreach (var (filePath, scores) in byFile)
        {
            var allScores = new List<double>();
            if (scores.blur.HasValue) allScores.Add(scores.blur.Value);
            if (scores.exposure.HasValue) allScores.Add(scores.exposure.Value);

            double overall = allScores.Count > 0 ? allScores.Average() : 0;

            ImageRows.Add(new ImageQualityRowViewModel
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                BlurScore = scores.blur,
                ExposureScore = scores.exposure,
                OverallScore = Math.Round(overall, 1),
                BlurDetail = scores.blurDetail,
                ExposureDetail = scores.exposureDetail
            });
        }

        // Sort by worst score first
        var sorted = ImageRows.OrderBy(r => r.OverallScore).ToList();
        ImageRows.Clear();
        foreach (var row in sorted)
            ImageRows.Add(row);

        // Summary
        double avgScore = results.Average(r => r.Score);
        int totalIssues = results.Sum(r => r.Issues.Count);
        string label = avgScore switch
        {
            >= 85 => "Excellent",
            >= 65 => "Good",
            >= 40 => "Fair",
            _ => "Poor"
        };

        OverallScore = Math.Round(avgScore, 1);
        OverallScoreLabel = label;
        IssueCount = totalIssues;
        HasResults = true;

        SummaryText = totalIssues > 0
            ? $"Score: {avgScore:F0} ({label}) · {totalIssues} issue{(totalIssues != 1 ? "s" : "")}"
            : $"Score: {avgScore:F0} ({label}) · No issues";

        AnalysisCompleted?.Invoke(avgScore, totalIssues, label);
    }

    private async Task AnalyzeAsync()
    {
        if (_imageChecks is null || string.IsNullOrEmpty(_folderPath))
            return;

        IsAnalyzing = true;
        try
        {
            // Scan for images using the lightweight header reader approach
            var images = await Task.Run(() =>
            {
                var imgList = new List<ImageFileInfo>();
                if (!Directory.Exists(_folderPath)) return imgList;

                foreach (var file in Directory.EnumerateFiles(_folderPath))
                {
                    if (!DiffusionNexus.Domain.Enums.SupportedMediaTypes.IsImageFile(file))
                        continue;
                    // Use file info as placeholder — dimensions come from checks
                    imgList.Add(new ImageFileInfo(file, 0, 0));
                }
                return imgList;
            });

            var config = new DatasetConfig
            {
                FolderPath = _folderPath,
                LoraType = Domain.Enums.LoraType.Character // Default; checks use IsApplicable
            };

            var results = new List<ImageCheckResult>();
            foreach (var check in _imageChecks.OrderBy(c => c.Order))
            {
                var result = await check.RunAsync(images, config);
                results.Add(result);
            }

            ApplyResults(results);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
