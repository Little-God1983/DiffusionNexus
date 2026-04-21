using CommunityToolkit.Mvvm.ComponentModel;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.ViewModels.Dialogs;

/// <summary>
/// One row in the Image Quality Fixer dialog. Wraps a <see cref="PerImageQualitySummary"/>
/// plus the matching <see cref="ImageQualityAdvice"/>, optional file metadata, and a live
/// link to the dataset image (if resolved) so the parent dialog VM can mutate the rating.
/// Pure data — commands live on the parent <c>ImageQualityFixerViewModel</c>.
/// </summary>
public partial class ImageQualityFixerItemViewModel : ObservableObject
{
    /// <summary>
    /// Creates a new fixer row.
    /// </summary>
    /// <param name="summary">Per-image score summary from the analysis pipeline.</param>
    /// <param name="advice">Pre-computed advice (verdict + problems + fix suggestions).</param>
    /// <param name="datasetImage">
    /// Resolved dataset image, when the parent dataset exposes one. Null when no match
    /// was found (e.g. file added after the dataset was loaded). Replace and "Edit in
    /// Image Editor" actions require this to be non-null.
    /// </param>
    /// <param name="width">Image width in pixels, when known.</param>
    /// <param name="height">Image height in pixels, when known.</param>
    /// <param name="fileSizeBytes">File size in bytes, when known.</param>
    public ImageQualityFixerItemViewModel(
        PerImageQualitySummary summary,
        ImageQualityAdvice advice,
        DatasetImageViewModel? datasetImage,
        int? width,
        int? height,
        long? fileSizeBytes)
    {
        ArgumentNullException.ThrowIfNull(summary);
        ArgumentNullException.ThrowIfNull(advice);

        Summary = summary;
        Advice = advice;
        DatasetImage = datasetImage;
        Width = width;
        Height = height;
        FileSizeBytes = fileSizeBytes;
        _rating = datasetImage?.RatingStatus ?? ImageRatingStatus.Unrated;
    }

    /// <summary>Score summary that produced this row.</summary>
    public PerImageQualitySummary Summary { get; }

    /// <summary>Verdict + problems + fix suggestions for this image.</summary>
    public ImageQualityAdvice Advice { get; }

    /// <summary>Dataset image instance, or null when the file is not part of the loaded dataset.</summary>
    public DatasetImageViewModel? DatasetImage { get; }

    /// <summary>Absolute path to the image file.</summary>
    public string FilePath => Summary.FilePath;

    /// <summary>File name only (no directory) for table display.</summary>
    public string FileName => Path.GetFileName(Summary.FilePath);

    /// <summary>Image width in pixels, or null when not known.</summary>
    public int? Width { get; }

    /// <summary>Image height in pixels, or null when not known.</summary>
    public int? Height { get; }

    /// <summary>File size in bytes, or null when not known.</summary>
    public long? FileSizeBytes { get; }

    /// <summary>Resolution display string (e.g. "1024 \u00d7 768") or em-dash when unknown.</summary>
    public string ResolutionDisplay => Width is { } w && Height is { } h ? $"{w} \u00d7 {h}" : "\u2014";

    /// <summary>File size display string (e.g. "820 KB") or em-dash when unknown.</summary>
    public string FileSizeDisplay => FileSizeBytes is { } bytes ? FormatBytes(bytes) : "\u2014";

    /// <summary>Mean of all available metric scores. NaN when no checks ran.</summary>
    public double OverallScore => Summary.OverallScore;

    /// <summary>Color hex for the overall score cell.</summary>
    public string OverallScoreColor => ScoreColor(OverallScore);

    /// <summary>Color hex for the blur cell (gray when score absent).</summary>
    public string BlurScoreColor => ScoreColor(Summary.BlurScore);

    /// <summary>Color hex for the exposure cell.</summary>
    public string ExposureScoreColor => ScoreColor(Summary.ExposureScore);

    /// <summary>Color hex for the noise cell.</summary>
    public string NoiseScoreColor => ScoreColor(Summary.NoiseScore);

    /// <summary>Color hex for the JPEG cell.</summary>
    public string JpegScoreColor => ScoreColor(Summary.JpegScore);

    /// <summary>Display string for the blur cell ("12" or em-dash).</summary>
    public string BlurDisplay => FormatScore(Summary.BlurScore);

    /// <summary>Display string for the exposure cell.</summary>
    public string ExposureDisplay => FormatScore(Summary.ExposureScore);

    /// <summary>Display string for the noise cell.</summary>
    public string NoiseDisplay => FormatScore(Summary.NoiseScore);

    /// <summary>Display string for the JPEG cell.</summary>
    public string JpegDisplay => FormatScore(Summary.JpegScore);

    /// <summary>Display string for the overall score cell.</summary>
    public string OverallDisplay => double.IsNaN(OverallScore) ? "\u2014" : OverallScore.ToString("F0");

    private bool _isSelected;

    /// <summary>Whether this row is checked in the multi-select column.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Current rating mirrored from <see cref="DatasetImage"/>. Setting this property
    /// updates the in-memory state only; the parent VM is responsible for persisting
    /// to the .rating sidecar via the dataset image.
    /// </summary>
    public ImageRatingStatus Rating
    {
        get => _rating;
        set
        {
            if (SetProperty(ref _rating, value))
            {
                OnPropertyChanged(nameof(RatingLabel));
                OnPropertyChanged(nameof(RatingColor));
                OnPropertyChanged(nameof(IsApproved));
                OnPropertyChanged(nameof(IsRejected));
                OnPropertyChanged(nameof(IsUnrated));
                OnPropertyChanged(nameof(CanMutateRating));
            }
        }
    }
    private ImageRatingStatus _rating;

    /// <summary>Whether this row is currently rated Approved.</summary>
    public bool IsApproved => Rating == ImageRatingStatus.Approved;

    /// <summary>Whether this row is currently rated Rejected (Trash).</summary>
    public bool IsRejected => Rating == ImageRatingStatus.Rejected;

    /// <summary>Whether this row currently carries no rating.</summary>
    public bool IsUnrated => Rating == ImageRatingStatus.Unrated;

    /// <summary>Whether the rating can be changed (requires a resolved dataset image).</summary>
    public bool CanMutateRating => DatasetImage is not null;

    /// <summary>Short display label for the rating chip.</summary>
    public string RatingLabel => Rating switch
    {
        ImageRatingStatus.Approved => "Ready",
        ImageRatingStatus.Rejected => "Trash",
        _ => "Unrated"
    };

    /// <summary>Color hex for the rating chip background.</summary>
    public string RatingColor => Rating switch
    {
        ImageRatingStatus.Approved => "#4CAF50",
        ImageRatingStatus.Rejected => "#FF6B6B",
        _ => "#666666"
    };

    private static string FormatScore(double? score) => score is { } v ? v.ToString("F0") : "\u2014";

    private static string ScoreColor(double? score)
    {
        if (score is not { } v || double.IsNaN(v))
            return "#3A3A3A"; // neutral gray for "no data"

        return v switch
        {
            >= 80 => "#4CAF50",
            >= 65 => "#8BC34A",
            >= 40 => "#FFA726",
            _ => "#FF6B6B"
        };
    }

    private static string FormatBytes(long bytes)
    {
        const double Kb = 1024d;
        const double Mb = Kb * 1024d;
        const double Gb = Mb * 1024d;

        return bytes switch
        {
            >= (long)Gb => $"{bytes / Gb:F1} GB",
            >= (long)Mb => $"{bytes / Mb:F1} MB",
            >= (long)Kb => $"{bytes / Kb:F0} KB",
            _ => $"{bytes} B"
        };
    }
}
