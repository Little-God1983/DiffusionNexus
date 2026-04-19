using System.Collections.ObjectModel;
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

    /// <summary>Whether this image has a color cast issue.</summary>
    public bool HasColorCast => Detail.Contains("color tint", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this image is too dark.</summary>
    public bool IsDark => Detail.Contains("very dark", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this image is too bright.</summary>
    public bool IsBright => Detail.Contains("very bright", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this image has a brightness issue (dark or bright).</summary>
    public bool HasBrightnessIssue => IsDark || IsBright;

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

    /// <summary>Temp file path for the corrected preview image.</summary>
    [ObservableProperty]
    private string? _afterPreviewPath;

    /// <summary>Whether the preview has been generated.</summary>
    public bool HasPreview => !string.IsNullOrEmpty(AfterPreviewPath);

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
    private bool _isOptimizing;

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
                OnPropertyChanged(nameof(ShowTintSlider));
                OnPropertyChanged(nameof(ShowBrightnessSliders));
                AutoSetCommand.NotifyCanExecuteChanged();
                ApplyValuesCommand.NotifyCanExecuteChanged();
                SkipSelectedCommand.NotifyCanExecuteChanged();
                // Reset sliders to defaults for new image
                _tintRemoval = 50;
                _brightness = 50;
                _contrast = 50;
                OnPropertyChanged(nameof(TintRemoval));
                OnPropertyChanged(nameof(Brightness));
                OnPropertyChanged(nameof(Contrast));
                EstimatedScore = value?.Score ?? 0;
                OnPropertyChanged(nameof(EstimatedScoreColor));
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

    /// <summary>Whether the auto-set optimization is currently running.</summary>
    public bool IsOptimizing
    {
        get => _isOptimizing;
        private set => SetProperty(ref _isOptimizing, value);
    }

    private double _tintRemoval = 50;
    private double _brightness = 50;
    private double _contrast = 50;

    /// <summary>Tint removal strength (0–100). Shown for color-cast images.</summary>
    public double TintRemoval
    {
        get => _tintRemoval;
        set
        {
            if (SetProperty(ref _tintRemoval, value))
                InvalidatePreviewAndRegenerate();
        }
    }

    /// <summary>Brightness adjustment (0–100). Shown for dark/bright images.</summary>
    public double Brightness
    {
        get => _brightness;
        set
        {
            if (SetProperty(ref _brightness, value))
                InvalidatePreviewAndRegenerate();
        }
    }

    /// <summary>Contrast adjustment (0–100). Shown for dark/bright images.</summary>
    public double Contrast
    {
        get => _contrast;
        set
        {
            if (SetProperty(ref _contrast, value))
                InvalidatePreviewAndRegenerate();
        }
    }

    /// <summary>Whether the selected image has a color cast issue.</summary>
    public bool ShowTintSlider => _selectedImage?.HasColorCast == true;

    /// <summary>Whether the selected image has a brightness issue.</summary>
    public bool ShowBrightnessSliders => _selectedImage?.HasBrightnessIssue == true;

    private double _estimatedScore;

    /// <summary>Estimated score if the current correction is applied.</summary>
    public double EstimatedScore
    {
        get => _estimatedScore;
        private set => SetProperty(ref _estimatedScore, value);
    }

    /// <summary>Color hex for the estimated score.</summary>
    public string EstimatedScoreColor => EstimatedScore switch
    {
        >= 80 => "#4CAF50",
        >= 65 => "#8BC34A",
        >= 40 => "#FFA726",
        _ => "#FF6B6B"
    };

    /// <summary>Summary of progress.</summary>
    public string ProgressText => $"{FixedCount} fixed · {SkippedCount} skipped · {Images.Count(i => !i.IsResolved)} remaining";

    /// <summary>Finds and sets the optimal slider values without saving the image.</summary>
    public IAsyncRelayCommand AutoSetCommand { get; }

    /// <summary>Applies the current slider values and saves the corrected image.</summary>
    public IAsyncRelayCommand ApplyValuesCommand { get; }

    /// <summary>Skips the selected image without fixing.</summary>
    public IRelayCommand SkipSelectedCommand { get; }

    /// <summary>Auto-fixes all remaining (unfixed, unskipped) images.</summary>
    public IAsyncRelayCommand FixAllCommand { get; }

    /// <summary>
    /// Dialog service for showing confirmation dialogs.
    /// </summary>
    public IDialogService? DialogService { get; set; }

    private CancellationTokenSource? _previewCts;

    /// <summary>
    /// Invalidates the current preview and regenerates it with the updated strength.
    /// Uses debouncing to avoid excessive regeneration during slider drag.
    /// </summary>
    private async void InvalidatePreviewAndRegenerate()
    {
        if (_selectedImage is null)
            return;

        // Cancel any pending debounce/generation
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        var ct = _previewCts.Token;

        try
        {
            // Debounce: wait 250ms before regenerating
            await Task.Delay(250, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _selectedImage.AfterPreviewPath = null;
        _selectedImage.NotifyPreviewChanged();
        await GeneratePreviewAsync(_selectedImage);
    }

    /// <summary>
    /// Creates a new <see cref="ColorFixerViewModel"/>.
    /// </summary>
    public ColorFixerViewModel()
    {
        AutoSetCommand = new AsyncRelayCommand(AutoSetAsync, () => _selectedImage is not null && !_selectedImage.IsResolved);
        ApplyValuesCommand = new AsyncRelayCommand(ApplyValuesAsync, () => _selectedImage is not null && !_selectedImage.IsResolved);
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

    /// <summary>Finds optimal slider values and sets them (does not save the image).</summary>
    private async Task AutoSetAsync()
    {
        if (_selectedImage is null || _selectedImage.IsResolved)
            return;

        IsOptimizing = true;
        try
        {
            await OptimizeSliderValuesAsync(_selectedImage);
        }
        finally
        {
            IsOptimizing = false;
        }
    }

    /// <summary>Applies the current slider values, saves the corrected image, and advances.</summary>
    private async Task ApplyValuesAsync()
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
                await OptimizeSliderValuesAsync(image);
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
        AutoSetCommand.NotifyCanExecuteChanged();
        ApplyValuesCommand.NotifyCanExecuteChanged();
        SkipSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Finds the optimal slider values (TintRemoval, Brightness, Contrast) that maximize
    /// the estimated score. Uses a coarse-to-fine grid search: first samples at 25% intervals,
    /// then refines around the best candidate at 5% intervals.
    /// </summary>
    private async Task OptimizeSliderValuesAsync(ColorFixerImageItem item)
    {
        if (!File.Exists(item.FilePath))
            return;

        var (bestTint, bestBright, bestContrast) = await Task.Run(() =>
        {
            using var image = Image.Load<Rgba32>(item.FilePath);

            double bestScore = -1;
            double bTint = 50, bBright = 50, bContrast = 50;

            // Coarse pass: sample at 25% intervals (0, 25, 50, 75, 100)
            for (int t = 0; t <= 100; t += 25)
            {
                for (int br = 0; br <= 100; br += 25)
                {
                    for (int co = 0; co <= 100; co += 25)
                    {
                        using var clone = image.Clone();
                        ApplyColorCorrection(clone, item.Detail, t / 100.0, br / 100.0, co / 100.0);
                        double score = EstimateScore(clone);
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bTint = t;
                            bBright = br;
                            bContrast = co;
                        }
                    }
                }
            }

            // Fine pass: refine ±20 around best at 10% steps
            double fTint = bTint, fBright = bBright, fContrast = bContrast;
            double fineScore = bestScore;

            for (int t = (int)Math.Max(0, bTint - 20); t <= Math.Min(100, bTint + 20); t += 10)
            {
                for (int br = (int)Math.Max(0, bBright - 20); br <= Math.Min(100, bBright + 20); br += 10)
                {
                    for (int co = (int)Math.Max(0, bContrast - 20); co <= Math.Min(100, bContrast + 20); co += 10)
                    {
                        using var clone = image.Clone();
                        ApplyColorCorrection(clone, item.Detail, t / 100.0, br / 100.0, co / 100.0);
                        double score = EstimateScore(clone);
                        if (score > fineScore)
                        {
                            fineScore = score;
                            fTint = t;
                            fBright = br;
                            fContrast = co;
                        }
                    }
                }
            }

            return (fTint, fBright, fContrast);
        });

        // Update sliders to optimal values (triggers preview regeneration)
        TintRemoval = bestTint;
        Brightness = bestBright;
        Contrast = bestContrast;
    }

    /// <summary>
    /// Applies automated color correction to a single image using ImageSharp.
    /// Routes to issue-specific algorithms based on the detail string from the analyzer.
    /// </summary>
    private async Task ApplyColorFixAsync(ColorFixerImageItem item)
    {
        item.IsProcessing = true;
        try
        {
            await Task.Run(() =>
            {
                using var image = Image.Load<Rgba32>(item.FilePath);
                ApplyColorCorrection(image, item.Detail, _tintRemoval / 100.0, _brightness / 100.0, _contrast / 100.0);
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

        item.IsGeneratingPreview = true;
        try
        {
            var (previewPath, estimatedScore) = await Task.Run<(string, double)>(() =>
            {
                using var image = Image.Load<Rgba32>(item.FilePath);

                using var corrected = image.Clone();
                ApplyColorCorrection(corrected, item.Detail, _tintRemoval / 100.0, _brightness / 100.0, _contrast / 100.0);

                var tempPath = Path.Combine(Path.GetTempPath(), "DiffusionNexus_ColorFixer",
                    $"{Path.GetFileNameWithoutExtension(item.FilePath)}_preview_{DateTime.UtcNow.Ticks}{Path.GetExtension(item.FilePath)}");
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                corrected.Save(tempPath);

                double score = EstimateScore(corrected);
                return (tempPath, score);
            });

            item.AfterPreviewPath = previewPath;
            item.NotifyPreviewChanged();
            EstimatedScore = estimatedScore;
            OnPropertyChanged(nameof(EstimatedScoreColor));
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
    /// Routes to issue-specific correction algorithms based on the analyzer detail string.
    /// </summary>
    /// <param name="image">The image to correct in-place.</param>
    /// <param name="detail">The detail string from the color distribution analyzer.</param>
    /// <param name="tintRemoval">Tint removal strength 0.0–1.0.</param>
    /// <param name="brightness">Brightness adjustment strength 0.0–1.0.</param>
    /// <param name="contrast">Contrast adjustment strength 0.0–1.0.</param>
    private static void ApplyColorCorrection(Image<Rgba32> image, string detail, double tintRemoval, double brightness, double contrast)
    {
        if (tintRemoval > 0.001)
        {
            ApplyColorCastCorrection(image, tintRemoval);
        }

        if (brightness > 0.001 || contrast > 0.001)
        {
            bool isDark = detail.Contains("very dark", StringComparison.OrdinalIgnoreCase);
            bool isBright = detail.Contains("very bright", StringComparison.OrdinalIgnoreCase);
            // Default to brighten if no specific direction detected
            bool brighten = !isBright || isDark;
            ApplyBrightnessCorrection(image, brightness, contrast, brighten);
        }
    }

    /// <summary>
    /// Corrects color cast by finding the dominant hue via a 12-bin hue histogram,
    /// then applying per-pixel white balance that specifically targets the cast color.
    /// Unlike gray-world, this preserves natural color variation while neutralizing the tint.
    /// </summary>
    private static void ApplyColorCastCorrection(Image<Rgba32> image, double strength)
    {
        // Step 1: Build hue histogram to find the dominant cast hue
        const int hueBins = 12;
        var hueCounts = new long[hueBins];
        long totalR = 0, totalG = 0, totalB = 0, pixelCount = 0;
        long saturatedPixelCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    totalR += p.R;
                    totalG += p.G;
                    totalB += p.B;
                    pixelCount++;

                    RgbToHsv(p.R, p.G, p.B, out float h, out float s, out _);
                    if (s > 0.15f)
                    {
                        int bin = Math.Clamp((int)(h / 360f * hueBins), 0, hueBins - 1);
                        hueCounts[bin]++;
                        saturatedPixelCount++;
                    }
                }
            }
        });

        if (pixelCount == 0) return;

        double avgR = totalR / (double)pixelCount;
        double avgG = totalG / (double)pixelCount;
        double avgB = totalB / (double)pixelCount;
        double avgGray = (avgR + avgG + avgB) / 3.0;

        // Step 2: Compute per-channel white balance scales (gray-world)
        float scaleR = avgGray > 0 ? (float)(avgGray / Math.Max(avgR, 1)) : 1f;
        float scaleG = avgGray > 0 ? (float)(avgGray / Math.Max(avgG, 1)) : 1f;
        float scaleB = avgGray > 0 ? (float)(avgGray / Math.Max(avgB, 1)) : 1f;

        // Step 3: Find dominant hue and how dominant the cast is
        int dominantBin = 0;
        long maxCount = 0;
        for (int i = 0; i < hueBins; i++)
        {
            if (hueCounts[i] > maxCount)
            {
                maxCount = hueCounts[i];
                dominantBin = i;
            }
        }

        float dominantHue = (dominantBin + 0.5f) * (360f / hueBins);
        double castDominance = saturatedPixelCount > 0 ? (double)maxCount / saturatedPixelCount : 0;

        // Step 4: Apply correction — blend original toward white-balanced based on
        // how close each pixel's hue is to the cast hue.
        // Pixels near the cast hue get full correction; others get partial.
        float userStrength = (float)strength;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];

                    // Compute how much this pixel should be corrected
                    RgbToHsv(pixel.R, pixel.G, pixel.B, out float pixelHue, out float pixelSat, out _);

                    // Pixels with low saturation (near gray) get full white-balance correction
                    // Pixels with high saturation near cast hue get full correction
                    // Pixels with high saturation far from cast get reduced correction (preserve their color)
                    float hueProximity = 1.0f;
                    if (pixelSat > 0.15f)
                    {
                        float hueDist = HueDistance(pixelHue, dominantHue);
                        // Full correction within 60° of cast; tapers to 30% correction at 180°
                        hueProximity = Math.Clamp(1.0f - hueDist / 180f * 0.7f, 0.3f, 1.0f);
                    }

                    float pixelStrength = userStrength * hueProximity * (float)Math.Min(castDominance + 0.3, 1.0);

                    float newR = pixel.R * (1f + pixelStrength * (scaleR - 1f));
                    float newG = pixel.G * (1f + pixelStrength * (scaleG - 1f));
                    float newB = pixel.B * (1f + pixelStrength * (scaleB - 1f));

                    pixel.R = ClampByte(newR);
                    pixel.G = ClampByte(newG);
                    pixel.B = ClampByte(newB);
                }
            }
        });
    }

    /// <summary>
    /// Corrects brightness and contrast using histogram-based stretching with gamma correction.
    /// Uses percentile-based range detection (1st/99th) for robust clipping.
    /// </summary>
    /// <param name="image">The image to correct in-place.</param>
    /// <param name="brightnessStrength">Brightness adjustment 0.0–1.0.</param>
    /// <param name="contrastStrength">Contrast stretch strength 0.0–1.0.</param>
    /// <param name="brighten">True to brighten dark images, false to darken bright images.</param>
    private static void ApplyBrightnessCorrection(Image<Rgba32> image, double brightnessStrength, double contrastStrength, bool brighten)
    {
        // Build luminance histogram
        var histogram = new int[256];
        long pixelCount = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    int lum = (int)(0.299 * p.R + 0.587 * p.G + 0.114 * p.B);
                    histogram[Math.Clamp(lum, 0, 255)]++;
                    pixelCount++;
                }
            }
        });

        if (pixelCount == 0) return;

        // Find 1st and 99th percentile
        long threshold1 = pixelCount / 100;
        long threshold99 = pixelCount * 99 / 100;
        long cumulative = 0;
        int lowClip = 0, highClip = 255;

        for (int i = 0; i < 256; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= threshold1 && lowClip == 0) lowClip = i;
            if (cumulative >= threshold99) { highClip = i; break; }
        }

        if (highClip <= lowClip) highClip = lowClip + 1;

        // Gamma controls brightness; contrast stretch range controls contrast
        double gamma = brighten
            ? Math.Max(0.3, 1.0 - brightnessStrength * 0.7)
            : Math.Min(2.5, 1.0 + brightnessStrength * 1.5);

        // Blend contrast stretch range: at 0 contrast -> no stretch (identity), at 1 -> full percentile stretch
        int effectiveLow = (int)(lowClip * contrastStrength);
        int effectiveHigh = (int)(255 + (highClip - 255) * contrastStrength);
        if (effectiveHigh <= effectiveLow) effectiveHigh = effectiveLow + 1;

        var lut = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            // Contrast stretch to [0..1]
            double normalized = Math.Clamp((i - effectiveLow) / (double)(effectiveHigh - effectiveLow), 0, 1);
            // Apply gamma for brightness
            double corrected = Math.Pow(normalized, gamma) * 255.0;
            // Blend: use max of brightness/contrast strength for overall blend
            double blendFactor = Math.Max(brightnessStrength, contrastStrength);
            double blended = i + blendFactor * (corrected - i);
            lut[i] = (byte)Math.Clamp((int)Math.Round(blended), 0, 255);
        }

        // Apply LUT
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var pixel = ref row[x];
                    pixel.R = lut[pixel.R];
                    pixel.G = lut[pixel.G];
                    pixel.B = lut[pixel.B];
                }
            }
        });
    }

    /// <summary>Converts RGB (0-255) to HSV (H: 0-360, S: 0-1, V: 0-1).</summary>
    private static void RgbToHsv(byte r, byte g, byte b, out float h, out float s, out float v)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        v = max;
        s = max > 0 ? delta / max : 0;

        if (delta < 0.0001f)
        {
            h = 0;
        }
        else if (max == rf)
        {
            h = 60f * (((gf - bf) / delta) % 6);
        }
        else if (max == gf)
        {
            h = 60f * (((bf - rf) / delta) + 2);
        }
        else
        {
            h = 60f * (((rf - gf) / delta) + 4);
        }

        if (h < 0) h += 360f;
    }

    /// <summary>Computes the angular distance between two hues (0-360).</summary>
    private static float HueDistance(float h1, float h2)
    {
        float diff = Math.Abs(h1 - h2);
        return diff > 180f ? 360f - diff : diff;
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);

    /// <summary>
    /// Estimates the color distribution score for a corrected image using the same
    /// thresholds as <c>ColorDistributionAnalyzer</c>: color cast (−25), very dark (−10), very bright (−10).
    /// </summary>
    private static double EstimateScore(Image<Rgba32> image)
    {
        const int hueBins = 12;
        const double grayscaleThreshold = 0.1;
        const double colorCastThreshold = 0.6;
        const double veryDarkThreshold = 0.15;
        const double veryBrightThreshold = 0.9;
        const double minSatForHue = 0.15;

        var hueCounts = new long[hueBins];
        double satSum = 0, valSum = 0;
        long totalPixels = 0, saturatedPixels = 0;

        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    var p = row[x];
                    RgbToHsv(p.R, p.G, p.B, out float h, out float s, out float v);
                    satSum += s;
                    valSum += v;
                    totalPixels++;

                    if (s >= minSatForHue)
                    {
                        int bin = Math.Clamp((int)(h / 360f * hueBins), 0, hueBins - 1);
                        hueCounts[bin]++;
                        saturatedPixels++;
                    }
                }
            }
        });

        if (totalPixels == 0) return 100;

        double satMean = satSum / totalPixels;
        double valMean = valSum / totalPixels;

        double score = 100;
        bool isGrayscale = satMean < grayscaleThreshold;

        // Continuous color cast penalty: any hue dominance above 25% starts reducing score,
        // reaching full −25 penalty at 75%+ dominance. This provides smooth feedback as
        // the user adjusts tint removal.
        if (!isGrayscale && saturatedPixels > 0)
        {
            long maxHue = hueCounts.Max();
            double dominance = (double)maxHue / saturatedPixels;
            if (dominance > 0.25)
            {
                double castSeverity = Math.Clamp((dominance - 0.25) / 0.50, 0, 1);
                score -= 25 * castSeverity;
            }
        }

        // Continuous brightness penalties with wide ramps
        if (valMean < 0.4)
        {
            double darkSeverity = Math.Clamp((0.4 - valMean) / 0.3, 0, 1);
            score -= 10 * darkSeverity;
        }

        if (valMean > 0.7)
        {
            double brightSeverity = Math.Clamp((valMean - 0.7) / 0.25, 0, 1);
            score -= 10 * brightSeverity;
        }

        return Math.Max(0, Math.Round(score, 1));
    }
}
