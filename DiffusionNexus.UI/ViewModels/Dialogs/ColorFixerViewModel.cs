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

    private double _correctionStrength = 50;

    /// <summary>User-adjustable correction strength (0–100). Default is 50.</summary>
    public double CorrectionStrength
    {
        get => _correctionStrength;
        set
        {
            if (SetProperty(ref _correctionStrength, value))
            {
                InvalidatePreviewAndRegenerate();
            }
        }
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
                ApplyColorCorrection(image, _correctionStrength / 100.0, item.Detail);
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
            var previewPath = await Task.Run(() =>
            {
                using var image = Image.Load<Rgba32>(item.FilePath);

                using var corrected = image.Clone();
                ApplyColorCorrection(corrected, _correctionStrength / 100.0, item.Detail);

                var tempPath = Path.Combine(Path.GetTempPath(), "DiffusionNexus_ColorFixer",
                    $"{Path.GetFileNameWithoutExtension(item.FilePath)}_preview_{DateTime.UtcNow.Ticks}{Path.GetExtension(item.FilePath)}");
                Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);
                corrected.Save(tempPath);
                return tempPath;
            });

            item.AfterPreviewPath = previewPath;
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
    /// Routes to issue-specific correction algorithms based on the analyzer detail string.
    /// </summary>
    /// <param name="image">The image to correct in-place.</param>
    /// <param name="strength">Correction strength from 0.0 (no change) to 1.0 (full correction).</param>
    /// <param name="detail">The detail string from the color distribution analyzer.</param>
    private static void ApplyColorCorrection(Image<Rgba32> image, double strength, string detail)
    {
        bool hasColorCast = detail.Contains("color tint", StringComparison.OrdinalIgnoreCase);
        bool isDark = detail.Contains("very dark", StringComparison.OrdinalIgnoreCase);
        bool isBright = detail.Contains("very bright", StringComparison.OrdinalIgnoreCase);

        if (hasColorCast)
        {
            ApplyColorCastCorrection(image, strength);
        }

        if (isDark)
        {
            ApplyBrightnessCorrection(image, strength, brighten: true);
        }
        else if (isBright)
        {
            ApplyBrightnessCorrection(image, strength, brighten: false);
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
    /// Corrects brightness using histogram-based contrast stretching with gamma correction.
    /// Uses percentile-based range detection (1st/99th) for robust clipping.
    /// </summary>
    private static void ApplyBrightnessCorrection(Image<Rgba32> image, double strength, bool brighten)
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

        // Build LUT: contrast stretch + gamma
        double gamma = brighten ? Math.Max(0.3, 1.0 - strength * 0.7) : Math.Min(2.5, 1.0 + strength * 1.5);
        float userStrength = (float)strength;
        var lut = new byte[256];

        for (int i = 0; i < 256; i++)
        {
            // Contrast stretch to [0..1]
            double normalized = Math.Clamp((i - lowClip) / (double)(highClip - lowClip), 0, 1);
            // Apply gamma
            double corrected = Math.Pow(normalized, gamma) * 255.0;
            // Blend with original based on strength
            double blended = i + userStrength * (corrected - i);
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
}
