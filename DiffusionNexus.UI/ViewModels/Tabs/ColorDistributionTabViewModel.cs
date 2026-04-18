using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality.ImageAnalysis;
using DiffusionNexus.UI.Services;

namespace DiffusionNexus.UI.ViewModels.Tabs;

/// <summary>
/// Per-image color distribution result shown in the detail panel.
/// </summary>
public class ColorDistributionItemViewModel : ObservableObject
{
    private bool _isExpanded;

    /// <summary>File name (no path).</summary>
    public required string FileName { get; init; }

    /// <summary>Absolute file path (for image preview).</summary>
    public required string FilePath { get; init; }

    /// <summary>Paired image path for preview.</summary>
    public string ImagePath => FilePath;

    /// <summary>Score for this image (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Human-readable detail (e.g. "Grayscale; Color-cast").</summary>
    public required string Detail { get; init; }

    /// <summary>Color hex for the score.</summary>
    public string ScoreColor => Score switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    /// <summary>Severity icon.</summary>
    public string SeverityIcon => Score switch
    {
        >= 80 => "\u2714",
        >= 40 => "\u26A0",
        _ => "\u2716"
    };

    /// <summary>Whether this item is expanded to show the image preview.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>Toggles the expanded state.</summary>
    public IRelayCommand ToggleExpandCommand { get; }

    public ColorDistributionItemViewModel()
    {
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }
}

/// <summary>
/// ViewModel for the Color Distribution detail section within the Image Analysis dashboard.
/// Shows color consistency issues: grayscale mixing, color-cast, palette outliers.
/// </summary>
public class ColorDistributionTabViewModel : ObservableObject, IDialogServiceAware
{
    /// <inheritdoc />
    public IDialogService? DialogService { get; set; }
    private readonly ColorDistributionAnalyzer? _analyzer;

    private string _folderPath = string.Empty;
    private bool _isAnalyzing;
    private bool _hasResults;
    private double _overallScore;
    private string _overallScoreLabel = string.Empty;
    private int _issueCount;
    private string _summaryText = "Not analyzed yet";
    private Issue? _selectedIssue;

    private readonly Dictionary<string, ColorDistributionItemViewModel> _imageItemsByPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new <see cref="ColorDistributionTabViewModel"/> with an analyzer.
    /// </summary>
    public ColorDistributionTabViewModel(ColorDistributionAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        _analyzer = analyzer;
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        OpenFixerCommand = new AsyncRelayCommand(OpenFixerAsync, () => CanOpenFixer);
    }

    /// <summary>
    /// Design-time constructor.
    /// </summary>
    public ColorDistributionTabViewModel()
    {
        AnalyzeCommand = new AsyncRelayCommand(AnalyzeAsync, () => CanAnalyze);
        OpenFixerCommand = new AsyncRelayCommand(OpenFixerAsync, () => CanOpenFixer);
    }

    #region Observable Properties

    /// <summary>Issues from the analysis.</summary>
    public ObservableCollection<Issue> Issues { get; } = [];

    /// <summary>Image items for the currently selected issue.</summary>
    public ObservableCollection<ColorDistributionItemViewModel> AffectedImages { get; } = [];

    /// <summary>All images sorted by worst score first.</summary>
    public ObservableCollection<ColorDistributionItemViewModel> AllImages { get; } = [];

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
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                OnPropertyChanged(nameof(CanOpenFixer));
                OpenFixerCommand.NotifyCanExecuteChanged();
            }
        }
    }

    /// <summary>Can the fixer be opened (issues exist to fix).</summary>
    public bool CanOpenFixer => HasResults && Issues.Count > 0;

    /// <summary>Can the analyze command execute.</summary>
    public bool CanAnalyze => !string.IsNullOrEmpty(_folderPath) && !IsAnalyzing;

    /// <summary>Overall color distribution score.</summary>
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

    /// <summary>Summary text.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>Currently selected issue.</summary>
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

    /// <summary>Opens the color fixer window.</summary>
    public IAsyncRelayCommand OpenFixerCommand { get; }

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
    /// Applies externally computed results (when run via the full pipeline).
    /// </summary>
    public void ApplyResults(ImageCheckResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Issues.Clear();
        AffectedImages.Clear();
        AllImages.Clear();
        _imageItemsByPath.Clear();

        foreach (var issue in result.Issues)
            Issues.Add(issue);

        foreach (var pis in result.PerImageScores)
        {
            var item = new ColorDistributionItemViewModel
            {
                FileName = Path.GetFileName(pis.FilePath),
                FilePath = pis.FilePath,
                Score = pis.Score,
                Detail = pis.Detail ?? string.Empty
            };

            _imageItemsByPath[pis.FilePath] = item;
        }

        // All images sorted by worst score first
        foreach (var item in _imageItemsByPath.Values.OrderBy(i => i.Score))
            AllImages.Add(item);

        string label = result.Score switch
        {
            >= 85 => "Excellent",
            >= 65 => "Good",
            >= 40 => "Fair",
            _ => "Poor"
        };

        OverallScore = Math.Round(result.Score, 1);
        OverallScoreLabel = label;
        IssueCount = Issues.Count;
        HasResults = true;

        SummaryText = Issues.Count > 0
            ? $"Score: {result.Score:F0} ({label}) \u00b7 {Issues.Count} issue{(Issues.Count != 1 ? "s" : "")}"
            : $"Score: {result.Score:F0} ({label}) \u00b7 No issues";

        SelectedIssue = Issues.Count > 0 ? Issues[0] : null;

        AnalysisCompleted?.Invoke(result.Score, Issues.Count, label);
    }

    private void PopulateAffectedImages(Issue? issue)
    {
        AffectedImages.Clear();

        if (issue is null)
            return;

        foreach (var filePath in issue.AffectedFiles)
        {
            if (_imageItemsByPath.TryGetValue(filePath, out var item))
                AffectedImages.Add(item);
        }
    }

    private async Task OpenFixerAsync()
    {
        if (DialogService is null || !HasResults || Issues.Count == 0)
            return;

        var problematicImages = _imageItemsByPath.Values
            .Where(i => i.Score < 80)
            .OrderBy(i => i.Score)
            .ToList();

        if (problematicImages.Count == 0)
            return;

        await DialogService.ShowColorFixerAsync(problematicImages);
    }

    private async Task AnalyzeAsync()
    {
        if (_analyzer is null || string.IsNullOrEmpty(_folderPath))
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

            var result = await _analyzer.RunAsync(images, config);
            ApplyResults(result);
        }
        finally
        {
            IsAnalyzing = false;
        }
    }
}
