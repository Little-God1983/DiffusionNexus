using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Per-image quality result shown in the detail panel when an issue is selected.
/// Shows the image with its scores and a human-readable verdict.
/// </summary>
public class ImageQualityItemViewModel : ObservableObject
{
    private bool _isExpanded;

    /// <summary>File name (no path).</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute file path (for image preview).</summary>
    public required string FilePath { get; init; }

    /// <summary>Paired image path for preview (same as FilePath for image checks).</summary>
    public string ImagePath => FilePath;

    /// <summary>Overall score for this image (0–100).</summary>
    public required double OverallScore { get; init; }

    /// <summary>Human-readable verdict (e.g. "Very blurry — replace with sharper source").</summary>
    public required string Verdict { get; init; }

    /// <summary>Score breakdown lines (e.g. "Blur: 23/100 — Laplacian variance: 67").</summary>
    public required IReadOnlyList<string> ScoreBreakdown { get; init; }

    /// <summary>Color hex for the overall score.</summary>
    public string ScoreColor => OverallScore switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    /// <summary>Severity icon based on score.</summary>
    public string SeverityIcon => OverallScore switch
    {
        >= 80 => "\u2714",  // checkmark
        >= 40 => "\u26A0",  // warning
        _ => "\u2716"       // cross
    };

    /// <summary>Whether this item is expanded to show the image preview.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Toggles the expanded state.</summary>
    public IRelayCommand ToggleExpandCommand { get; }

    public ImageQualityItemViewModel()
    {
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }
}

/// <summary>
/// ViewModel for the Image Quality detail section within the Image Analysis dashboard.
/// Uses the same left (issue list) + right (detail with affected images) pattern as Caption Quality.
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
    private Issue? _selectedIssue;

    // Stores full per-image data for building the detail panel
    private readonly Dictionary<string, ImageQualityItemViewModel> _imageItemsByPath = new(StringComparer.OrdinalIgnoreCase);

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

    #region Observable Properties

    /// <summary>Issues from the analysis, shown in the left panel.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>Image items for the currently selected issue, shown in the right panel.</summary>
    public ObservableCollection<ImageQualityItemViewModel> AffectedImages { get; } = [];

    /// <summary>All images sorted by worst score, shown when "All Images" is selected.</summary>
    public ObservableCollection<ImageQualityItemViewModel> AllImages { get; } = [];

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

    /// <summary>Overall image quality score.</summary>
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

    /// <summary>Currently selected issue in the left panel.</summary>
    public Issue? SelectedIssue
    {
        get => _selectedIssue;
        set
        {
            if (SetProperty(ref _selectedIssue, value))
            {
                OnPropertyChanged(nameof(HasSelectedIssue));
                OnPropertyChanged(nameof(ShowAllImages));
                PopulateAffectedImages(value);
            }
        }
    }

    /// <summary>Whether an issue is selected.</summary>
    public bool HasSelectedIssue => _selectedIssue is not null;

    /// <summary>Whether to show all images (no specific issue selected).</summary>
    public bool ShowAllImages => _selectedIssue is null && HasResults;

    #endregion

    #region Commands

    /// <summary>Analyze command.</summary>
    public IAsyncRelayCommand AnalyzeCommand { get; }

    #endregion

    /// <summary>
    /// Raised when analysis completes, with (score, issueCount, label).
    /// </summary>
    public event Action<double, int, string>? AnalysisCompleted;

    /// <summary>
    /// Raised when analysis begins running.
    /// </summary>
    public event Action? AnalysisStarted;

    /// <summary>
    /// Updates the dataset folder path and resets state.
    /// </summary>
    public void RefreshContext(string folderPath)
    {
        _folderPath = folderPath ?? string.Empty;
        HasResults = false;
        Issues.Clear();
        AffectedImages.Clear();
        AllImages.Clear();
        _imageItemsByPath.Clear();
        SelectedIssue = null;
        SummaryText = "Not analyzed yet";
        OnPropertyChanged(nameof(CanAnalyze));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Applies externally computed image check results (when run via the full pipeline).
    /// </summary>
    public void ApplyResults(IReadOnlyList<ImageCheckResult> results)
    {
        Issues.Clear();
        AffectedImages.Clear();
        AllImages.Clear();
        _imageItemsByPath.Clear();

        if (results.Count == 0)
        {
            HasResults = false;
            SummaryText = "No image quality checks were run.";
            return;
        }

        // Collect all per-image scores by file path
        var byFile = new Dictionary<string, (double? blur, string? blurDetail, double? exposure, string? exposureDetail, double? noise, string? noiseDetail, double? color, string? colorDetail)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            foreach (var pis in result.PerImageScores)
            {
                if (!byFile.TryGetValue(pis.FilePath, out var entry))
                    entry = (null, null, null, null, null, null, null, null);

                if (result.CheckName == "Blur Detection")
                    entry = (pis.Score, pis.Detail, entry.exposure, entry.exposureDetail, entry.noise, entry.noiseDetail, entry.color, entry.colorDetail);
                else if (result.CheckName == "Exposure Analysis")
                    entry = (entry.blur, entry.blurDetail, pis.Score, pis.Detail, entry.noise, entry.noiseDetail, entry.color, entry.colorDetail);
                else if (result.CheckName == "Noise Estimation")
                    entry = (entry.blur, entry.blurDetail, entry.exposure, entry.exposureDetail, pis.Score, pis.Detail, entry.color, entry.colorDetail);
                else if (result.CheckName == "Color Distribution")
                    entry = (entry.blur, entry.blurDetail, entry.exposure, entry.exposureDetail, entry.noise, entry.noiseDetail, pis.Score, pis.Detail);

                byFile[pis.FilePath] = entry;
            }

            foreach (var issue in result.Issues)
                Issues.Add(issue);
        }

        // Build image items
        foreach (var (filePath, scores) in byFile)
        {
            var allScores = new List<double>();
            if (scores.blur.HasValue) allScores.Add(scores.blur.Value);
            if (scores.exposure.HasValue) allScores.Add(scores.exposure.Value);
            if (scores.noise.HasValue) allScores.Add(scores.noise.Value);
            if (scores.color.HasValue) allScores.Add(scores.color.Value);

            double overall = allScores.Count > 0 ? allScores.Average() : 0;
            overall = Math.Round(overall, 1);

            var breakdown = new List<string>();
            if (scores.blur.HasValue)
                breakdown.Add($"Sharpness: {scores.blur.Value:F0}/100 — {scores.blurDetail}");
            if (scores.exposure.HasValue)
                breakdown.Add($"Exposure: {scores.exposure.Value:F0}/100 — {scores.exposureDetail}");
            if (scores.noise.HasValue)
                breakdown.Add($"Noise: {scores.noise.Value:F0}/100 — {scores.noiseDetail}");
            if (scores.color.HasValue)
                breakdown.Add($"Color: {scores.color.Value:F0}/100 — {scores.colorDetail}");

            string verdict = BuildVerdict(scores.blur, scores.exposure, scores.noise, scores.color);

            var item = new ImageQualityItemViewModel
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                OverallScore = overall,
                Verdict = verdict,
                ScoreBreakdown = breakdown
            };

            _imageItemsByPath[filePath] = item;
        }

        // AllImages sorted by worst score first
        var sorted = _imageItemsByPath.Values.OrderBy(i => i.OverallScore).ToList();
        foreach (var item in sorted)
            AllImages.Add(item);

        // Summary
        double avgScore = results.Average(r => r.Score);
        int totalIssues = Issues.Count;
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
            ? $"Score: {avgScore:F0} ({label}) \u00b7 {totalIssues} issue{(totalIssues != 1 ? "s" : "")}"
            : $"Score: {avgScore:F0} ({label}) \u00b7 No issues";

        // Auto-select first issue if any
        SelectedIssue = Issues.Count > 0 ? Issues[0] : null;

        AnalysisCompleted?.Invoke(avgScore, totalIssues, label);
    }

    private void PopulateAffectedImages(Issue? issue)
    {
        AffectedImages.Clear();

        if (issue is null)
            return;

        foreach (var filePath in issue.AffectedFiles)
        {
            if (_imageItemsByPath.TryGetValue(filePath, out var item))
            {
                AffectedImages.Add(item);
            }
        }
    }

    private static string BuildVerdict(double? blur, double? exposure, double? noise, double? color)
    {
        var parts = new List<string>();

        if (blur.HasValue)
        {
            parts.Add(blur.Value switch
            {
                < 20 => "Extremely blurry \u2014 replace with a sharper image",
                < 40 => "Very blurry \u2014 consider replacing",
                < 65 => "Slightly soft \u2014 usable but may reduce output detail",
                < 80 => "Acceptable sharpness",
                _ => "Sharp"
            });
        }

        if (exposure.HasValue)
        {
            parts.Add(exposure.Value switch
            {
                < 20 => "Severely mis-exposed \u2014 replace this image",
                < 40 => "Poor exposure \u2014 consider replacing",
                < 65 => "Exposure could be better \u2014 review",
                < 80 => "Acceptable exposure",
                _ => "Well exposed"
            });
        }

        if (noise.HasValue)
        {
            parts.Add(noise.Value switch
            {
                < 20 => "Very noisy \u2014 denoise or replace",
                < 40 => "Noisy \u2014 consider denoising",
                < 70 => "Slight noise \u2014 acceptable",
                _ => "Clean"
            });
        }

        if (color.HasValue)
        {
            parts.Add(color.Value switch
            {
                < 50 => "Color issues detected \u2014 review",
                < 75 => "Minor color concerns",
                _ => "Good color distribution"
            });
        }

        return parts.Count > 0 ? string.Join(" · ", parts) : "No checks run";
    }

    private async Task AnalyzeAsync()
    {
        if (_imageChecks is null || string.IsNullOrEmpty(_folderPath))
            return;

        IsAnalyzing = true;
        AnalysisStarted?.Invoke();
        try
        {
            var images = await Task.Run(() =>
            {
                var imgList = new List<ImageFileInfo>();
                if (!Directory.Exists(_folderPath)) return imgList;

                foreach (var file in Directory.EnumerateFiles(_folderPath))
                {
                    if (!SupportedMediaTypes.IsImageFile(file))
                        continue;
                    imgList.Add(new ImageFileInfo(file, 0, 0));
                }
                return imgList;
            });

            var config = new DatasetConfig
            {
                FolderPath = _folderPath,
                LoraType = LoraType.Character
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
