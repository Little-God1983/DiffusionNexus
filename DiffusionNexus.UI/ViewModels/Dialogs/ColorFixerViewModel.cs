using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using Serilog;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace DiffusionNexus.UI.ViewModels.Dialogs;

/// <summary>
/// Represents a single image with color issues in the fixer view.
/// Shows a before/after preview and allows automated correction.
/// </summary>
public partial class ColorFixerImageItem : ObservableObject
{
    private bool _isFixed;
    private bool _isSkipped;
    private bool _isProcessing;

    /// <summary>Full path to the image file.</summary>
    public required string FilePath { get; init; }

    /// <summary>File name for display.</summary>
    public string FileName => Path.GetFileName(FilePath);

    /// <summary>Score from the analysis (0–100).</summary>
    public required double Score { get; init; }

    /// <summary>Human-readable detail about the color issue.</summary>
    public required string Detail { get; init; }

    /// <summary>Color hex for the score.</summary>
    public string ScoreColor => Score switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    /// <summary>Whether this image has been auto-fixed.</summary>
    public bool IsFixed
    {
        get => _isFixed;
        set
        {
            if (SetProperty(ref _isFixed, value))
                OnPropertyChanged(nameof(IsResolved));
        }
    }

    /// <summary>Whether this image was skipped by the user.</summary>
    public bool IsSkipped
    {
        get => _isSkipped;
        set
        {
            if (SetProperty(ref _isSkipped, value))
                OnPropertyChanged(nameof(IsResolved));
        }
    }

    /// <summary>Whether this image is currently being processed.</summary>
    public bool IsProcessing
    {
        get => _isProcessing;
        set => SetProperty(ref _isProcessing, value);
    }

    /// <summary>Whether this image has been resolved (fixed or skipped).</summary>
    public bool IsResolved => IsFixed || IsSkipped;

    /// <summary>Status label for display.</summary>
    public string StatusLabel => IsFixed ? "Fixed" : IsSkipped ? "Skipped" : "Pending";

    /// <summary>Status color for display.</summary>
    public string StatusColor => IsFixed ? "#4CAF50" : IsSkipped ? "#666" : "#FFA726";

    /// <summary>Avalonia bitmap showing the predicted result after auto-fix.</summary>
    [ObservableProperty]
    private Bitmap? _afterBitmap;

    /// <summary>Whether the preview has been generated.</summary>
    public bool HasPreview => AfterBitmap is not null;

    /// <summary>Notifies the UI that the preview state has changed.</summary>
    public void NotifyPreviewChanged() => OnPropertyChanged(nameof(HasPreview));

    /// <summary>Whether the preview is currently being generated.</summary>
    [ObservableProperty]
    private bool _isGeneratingPreview;
}

/// <summary>
/// ViewModel for the Color Fixer window.
/// Shows problematic images and allows automated color correction (white balance,
/// brightness normalization) or skipping individual images.
/// </summary>
public partial class ColorFixerViewModel : ObservableObject
{
    private static readonly ILogger Logger = Log.ForContext<ColorFixerViewModel>();

    private ColorFixerImageItem? _selectedImage;
    private int _fixedCount;
    private int _skippedCount;
    private bool _isFixingAll;

    /// <summary>All images with color issues to fix.</summary>
    public ObservableCollection<ColorFixerImageItem> Images { get; } = [];

    /// <summary>Currently selected image for preview.</summary>
    public ColorFixerImageItem? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (SetProperty(ref _selectedImage, value))
            {
                OnPropertyChanged(nameof(HasSelectedImage));
                FixSelectedCommand.NotifyCanExecuteChanged();
                SkipSelectedCommand.NotifyCanExecuteChanged();
                _ = GeneratePreviewAsync(value);
            }
        }
    }

    /// <summary>Whether an image is selected.</summary>
    public bool HasSelectedImage => _selectedImage is not null;

    /// <summary>Number of images fixed in this session.</summary>
    public int FixedCount
    {
        get => _fixedCount;
        private set => SetProperty(ref _fixedCount, value);
    }

    /// <summary>Number of images skipped in this session.</summary>
    public int SkippedCount
    {
        get => _skippedCount;
        private set => SetProperty(ref _skippedCount, value);
    }

    /// <summary>Whether a bulk fix-all operation is running.</summary>
    public bool IsFixingAll
    {
        get => _isFixingAll;
        private set => SetProperty(ref _isFixingAll, value);
    }

    /// <summary>Summary of progress.</summary>
    public string ProgressText => $"{FixedCount} fixed · {SkippedCount} skipped · {Images.Count(i => !i.IsResolved)} remaining";

    /// <summary>Auto-fixes the selected image's color issues.</summary>
    public IAsyncRelayCommand FixSelectedCommand { get; }

    /// <summary>Skips the selected image without fixing.</summary>
    public IRelayCommand SkipSelectedCommand { get; }

    /// <summary>Auto-fixes all remaining (unfixed, unskipped) images.</summary>
    public IAsyncRelayCommand FixAllCommand { get; }

    /// <summary>
    /// Dialog service for showing confirmation dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    /// <summary>
    /// Creates a new <see cref="ColorFixerViewModel"/>.
    /// </summary>
    public ColorFixerViewModel()
    {
        FixSelectedCommand = new AsyncRelayCommand(FixSelectedAsync, () => _selectedImage is not null && !_selectedImage.IsResolved);
        SkipSelectedCommand = new RelayCommand(SkipSelected, () => _selectedImage is not null && !_selectedImage.IsResolved);
        FixAllCommand = new AsyncRelayCommand(FixAllAsync, () => Images.Any(i => !i.IsResolved));
    }

    /// <summary>
    /// Populates the fixer from analyzed color distribution items.
    /// </summary>
    public void LoadImages(IEnumerable<ColorDistributionItemViewModel> items)
    {
        Images.Clear();

        foreach (var src in items)
        {
            Images.Add(new ColorFixerImageItem
            {
                FilePath = src.FilePath,
                Score = src.Score,
                Detail = src.Detail
            });
        }

        SelectedImage = Images.FirstOrDefault();
        FixAllCommand.NotifyCanExecuteChanged();
    }

    private async Task FixSelectedAsync()
    {
        if (_selectedImage is null || _selectedImage.IsResolved)
            return;

        await ApplyColorFixAsync(_selectedImage);
        AdvanceToNext();
    }

    private void SkipSelected()
    {
        if (_selectedImage is null || _selectedImage.IsResolved)
            return;

        _selectedImage.IsSkipped = true;
        SkippedCount++;
        OnPropertyChanged(nameof(ProgressText));
        FixAllCommand.NotifyCanExecuteChanged();
        AdvanceToNext();
    }

    private async Task FixAllAsync()
    {
        IsFixingAll = true;
        try
        {
            var remaining = Images.Where(i => !i.IsResolved).ToList();
            foreach (var image in remaining)
            {
                await ApplyColorFixAsync(image);
            }
        }
        finally
        {
            IsFixingAll = false;
        }
    }

    private void AdvanceToNext()
    {
        SelectedImage = Images.FirstOrDefault(i => !i.IsResolved);
        FixSelectedCommand.NotifyCanExecuteChanged();
        SkipSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Applies automated color correction to a single image using ImageSharp.
    /// Performs white balance normalization and brightness adjustment.
    /// </summary>
    private async Task ApplyColorFixAsync(ColorFixerImageItem item)
    {
        item.IsProcessing = true;
        try
        {
            await Task.Run(() =>
            {
                using var image = Image.Load<Rgba32>(item.FilePath);
                ApplyColorCorrection(image);
                image.Save(item.FilePath);
            });

            item.IsFixed = true;
            FixedCount++;
            OnPropertyChanged(nameof(ProgressText));
            FixAllCommand.NotifyCanExecuteChanged();

            Logger.Information("Auto-fixed color issues for {FilePath}", item.FilePath);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to auto-fix color for {FilePath}", item.FilePath);
        }
        finally
        {
            item.IsProcessing = false;
        }
    }

    /// <summary>
    /// Generates before/after preview bitmaps for the selected image.
    /// The "after" preview applies the same correction algorithm without saving.
    /// </summary>
    private async Task GeneratePreviewAsync(ColorFixerImageItem? item)
    {
        if (item is null || !File.Exists(item.FilePath))
            return;

        if (item.HasPreview)
            return;

        item.IsGeneratingPreview = true;
        try
        {
            var afterBmp = await Task.Run(() =>
            {
                using var image = Image.Load<Rgba32>(item.FilePath);

                // Apply the color correction to a copy for the "after" preview
                using var corrected = image.Clone();
                ApplyColorCorrection(corrected);

                using var afterStream = new MemoryStream();
                corrected.SaveAsPng(afterStream);
                afterStream.Position = 0;
                return new Bitmap(afterStream);
            });

            item.AfterBitmap = afterBmp;
            item.NotifyPreviewChanged();
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to generate preview for {FilePath}", item.FilePath);
        }
        finally
        {
            item.IsGeneratingPreview = false;
        }
    }

    /// <summary>
    /// Applies white balance and brightness correction to an image in-place.
    /// Shared logic between preview generation and actual fixing.
    /// </summary>
    private static void ApplyColorCorrection(Image<Rgba32> image)
    {
        long totalR = 0, totalG = 0, totalB = 0;
        long pixelCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var pixel = row[x];
                    totalR += pixel.R;
                    totalG += pixel.G;
                    totalB += pixel.B;
                    pixelCount++;
                }
            }
        });

        if (pixelCount == 0) return;

        double avgR = totalR / (double)pixelCount;
        double avgG = totalG / (double)pixelCount;
        double avgB = totalB / (double)pixelCount;
        double avgGray = (avgR + avgG + avgB) / 3.0;

        float scaleR = avgGray > 0 ? (float)(avgGray / Math.Max(avgR, 1)) : 1f;
        float scaleG = avgGray > 0 ? (float)(avgGray / Math.Max(avgG, 1)) : 1f;
        float scaleB = avgGray > 0 ? (float)(avgGray / Math.Max(avgB, 1)) : 1f;

        bool needsWhiteBalance = Math.Abs(scaleR - 1) > 0.05 || Math.Abs(scaleG - 1) > 0.05 || Math.Abs(scaleB - 1) > 0.05;

        bool isDark = avgGray < 38;
        bool isBright = avgGray > 230;

        if (needsWhiteBalance)
        {
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        ref var pixel = ref row[x];
                        pixel.R = ClampByte(pixel.R * scaleR);
                        pixel.G = ClampByte(pixel.G * scaleG);
                        pixel.B = ClampByte(pixel.B * scaleB);
                    }
                }
            });
        }

        if (isDark)
        {
            float brightnessFactor = (float)(128.0 / Math.Max(avgGray, 1));
            brightnessFactor = Math.Min(brightnessFactor, 2.5f);
            image.Mutate(ctx => ctx.Brightness(brightnessFactor));
        }
        else if (isBright)
        {
            float brightnessFactor = (float)(200.0 / Math.Max(avgGray, 1));
            brightnessFactor = Math.Max(brightnessFactor, 0.5f);
            image.Mutate(ctx => ctx.Brightness(brightnessFactor));
        }
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
}
