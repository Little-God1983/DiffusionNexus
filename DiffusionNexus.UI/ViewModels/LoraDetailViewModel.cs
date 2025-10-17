using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Utilities;
using OpenCvSharp;

namespace DiffusionNexus.UI.ViewModels;

public class LoraDetailViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] ModelFileExtensions = [".safetensors", ".pt", ".ckpt", ".pth"];
    private readonly object _videoLock = new();
    private string? _modelVersionId;
    private string? _description;
    private string? _previewMediaPath;
    private string? _descriptionHtml;
    private VideoCapture? _videoCapture;
    private DispatcherTimer? _videoTimer;
    private Bitmap? _videoFrame;

    public LoraDetailViewModel(LoraCardViewModel card)
    {
        Card = card;
        Card.PropertyChanged += OnCardPropertyChanged;
        UpdateMetadata();
        UpdatePreviewMediaPath();
    }

    public LoraCardViewModel Card { get; }

    public string ModelName => Card.Model?.ModelVersionName ?? Card.Model?.SafeTensorFileName ?? "Unknown Model";

    public IEnumerable<string> Tags => Card.Model?.Tags ?? Enumerable.Empty<string>();

    public bool HasTags => Tags.Any();

    public string ModelId => string.IsNullOrWhiteSpace(Card.Model?.ModelId) ? "Unknown" : Card.Model!.ModelId!;

    public string ModelVersionId => string.IsNullOrWhiteSpace(_modelVersionId) ? "Unknown" : _modelVersionId!;

    public string BaseModel => string.IsNullOrWhiteSpace(Card.Model?.DiffusionBaseModel)
        ? "Unknown"
        : Card.Model!.DiffusionBaseModel;

    public string FileName => GetModelFileName() ?? Card.Model?.SafeTensorFileName ?? "Unknown";

    public string ModelType => Card.Model?.ModelType switch
    {
        null => "Unknown",
        DiffusionTypes.UNASSIGNED => "Unknown",
        _ => Card.Model!.ModelType.ToString()
    };

    public string Description => string.IsNullOrWhiteSpace(_description)
        ? "No description available."
        : _description!;

    public string? DescriptionHtml => _descriptionHtml;

    public bool HasDescriptionHtml => !string.IsNullOrWhiteSpace(_descriptionHtml);

    public Bitmap? VideoFrame
    {
        get => _videoFrame;
        private set
        {
            if (ReferenceEquals(_videoFrame, value))
                return;

            _videoFrame?.Dispose();
            SetProperty(ref _videoFrame, value, nameof(VideoFrame));
        }
    }

    public bool HasVideo => _videoCapture != null;

    public bool HasImage => Card.PreviewImage != null;

    public bool ShouldShowImage => !HasVideo && HasImage;

    public bool ShowPlaceholder => !HasVideo && !HasImage;

    public void Dispose()
    {
        Card.PropertyChanged -= OnCardPropertyChanged;
        StopVideoPlayback();
        VideoFrame = null;
    }

    private void OnCardPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LoraCardViewModel.Model))
        {
            OnPropertyChanged(nameof(ModelName));
            OnPropertyChanged(nameof(ModelId));
            OnPropertyChanged(nameof(BaseModel));
            OnPropertyChanged(nameof(FileName));
            OnPropertyChanged(nameof(ModelType));
            OnPropertyChanged(nameof(Tags));
            OnPropertyChanged(nameof(HasTags));
            UpdateMetadata();
            UpdatePreviewMediaPath();
        }
        else if (e.PropertyName == nameof(LoraCardViewModel.PreviewImage))
        {
            OnPropertyChanged(nameof(HasImage));
            OnPropertyChanged(nameof(ShouldShowImage));
            OnPropertyChanged(nameof(ShowPlaceholder));
        }
    }

    private void UpdateMetadata()
    {
        var versionId = TryLoadModelVersionId();
        SetProperty(ref _modelVersionId, versionId, nameof(ModelVersionId));

        var description = TryLoadDescription();
        SetProperty(ref _description, description, nameof(Description));
        var sanitizedHtml = HtmlDescriptionFormatter.Sanitize(description);
        SetProperty(ref _descriptionHtml, sanitizedHtml, nameof(DescriptionHtml));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasDescriptionHtml));
    }

    private void UpdatePreviewMediaPath()
    {
        _previewMediaPath = Card.GetPreviewMediaPath();
        RestartVideoPlayback();
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(ShouldShowImage));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private void RestartVideoPlayback()
    {
        StopVideoPlayback();
        if (string.IsNullOrWhiteSpace(_previewMediaPath) || !File.Exists(_previewMediaPath))
            return;

        try
        {
            var capture = new VideoCapture(_previewMediaPath);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                return;
            }

            _videoCapture = capture;
            var fps = capture.Fps;
            if (fps <= 0 || double.IsNaN(fps) || double.IsInfinity(fps))
            {
                fps = 24;
            }

            _videoTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(Math.Max(15, 1000.0 / fps))
            };
            _videoTimer.Tick += OnVideoTick;
            _videoTimer.Start();
            OnPropertyChanged(nameof(HasVideo));
            OnPropertyChanged(nameof(ShouldShowImage));
            OnPropertyChanged(nameof(ShowPlaceholder));
            OnVideoTick(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log($"Failed to start video playback: {ex.Message}", LogSeverity.Warning);
            StopVideoPlayback();
        }
    }

    private void StopVideoPlayback()
    {
        if (_videoTimer != null)
        {
            _videoTimer.Stop();
            _videoTimer.Tick -= OnVideoTick;
            _videoTimer = null;
        }

        if (_videoCapture != null)
        {
            try
            {
                _videoCapture.Release();
            }
            catch
            {
                // ignored
            }
            _videoCapture.Dispose();
            _videoCapture = null;
        }

        VideoFrame = null;
        OnPropertyChanged(nameof(HasVideo));
        OnPropertyChanged(nameof(ShouldShowImage));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    private unsafe void OnVideoTick(object? sender, EventArgs e)
    {
        if (_videoCapture == null)
            return;

        lock (_videoLock)
        {
            try
            {
                using var frame = new Mat();
                if (!_videoCapture.Read(frame) || frame.Empty())
                {
                    _videoCapture.Set(VideoCaptureProperties.PosFrames, 0);
                    if (!_videoCapture.Read(frame) || frame.Empty())
                    {
                        return;
                    }
                }

                using var converted = new Mat();
                Cv2.CvtColor(frame, converted, ColorConversionCodes.BGR2BGRA);

                var size = new PixelSize(converted.Width, converted.Height);
                var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var fb = bitmap.Lock())
                {
                    var sourceStride = (int)converted.Step();
                    var targetStride = fb.RowBytes;
                    var rows = converted.Height;
                    var srcPtr = (byte*)converted.DataPointer;
                    var dstPtr = (byte*)fb.Address;

                    for (var y = 0; y < rows; y++)
                    {
                        var srcOffset = y * sourceStride;
                        var dstOffset = y * targetStride;
                        Buffer.MemoryCopy(srcPtr + srcOffset, dstPtr + dstOffset, targetStride, sourceStride);
                    }
                }

                VideoFrame = bitmap;
            }
            catch (Exception ex)
            {
                Log($"Error updating video frame: {ex.Message}", LogSeverity.Warning);
            }
        }
    }

    private string? TryLoadModelVersionId()
    {
        try
        {
            var infoFile = Card.Model?.AssociatedFilesInfo
                .FirstOrDefault(f => f.Name.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase));
            if (infoFile == null || !File.Exists(infoFile.FullName))
                return null;

            using var stream = File.OpenRead(infoFile.FullName);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (root.TryGetProperty("modelVersionId", out var mvId))
            {
                return mvId.ValueKind switch
                {
                    JsonValueKind.String => mvId.GetString(),
                    JsonValueKind.Number => mvId.GetInt64().ToString(),
                    _ => null
                };
            }

            if (root.TryGetProperty("id", out var id))
            {
                return id.ValueKind switch
                {
                    JsonValueKind.String => id.GetString(),
                    JsonValueKind.Number => id.GetInt64().ToString(),
                    _ => null
                };
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to parse model version id: {ex.Message}", LogSeverity.Warning);
        }

        return null;
    }

    private string? TryLoadDescription()
    {
        try
        {
            var model = Card.Model;
            if (model == null)
                return null;

            var jsonFile = model.AssociatedFilesInfo
                .FirstOrDefault(f =>
                    f.Extension.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                    !f.Name.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase));

            if (jsonFile != null && File.Exists(jsonFile.FullName))
            {
                var text = File.ReadAllText(jsonFile.FullName);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;

                if (root.TryGetProperty("description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    return NormalizeDescription(description.GetString());
                }

                if (root.TryGetProperty("modelVersions", out var versions) && versions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var version in versions.EnumerateArray())
                    {
                        if (version.TryGetProperty("description", out var versionDescription) &&
                            versionDescription.ValueKind == JsonValueKind.String)
                        {
                            return NormalizeDescription(versionDescription.GetString());
                        }
                    }
                }
            }

            var infoFile = model.AssociatedFilesInfo
                .FirstOrDefault(f => f.Name.EndsWith(".civitai.info", StringComparison.OrdinalIgnoreCase));
            if (infoFile != null && File.Exists(infoFile.FullName))
            {
                using var stream = File.OpenRead(infoFile.FullName);
                using var doc = JsonDocument.Parse(stream);
                var root = doc.RootElement;
                if (root.TryGetProperty("description", out var infoDescription) &&
                    infoDescription.ValueKind == JsonValueKind.String)
                {
                    return NormalizeDescription(infoDescription.GetString());
                }
            }
        }
        catch (Exception ex)
        {
            Log($"Failed to load description: {ex.Message}", LogSeverity.Warning);
        }

        return null;
    }

    private static string? NormalizeDescription(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Replace("\r\n", "\n").Trim();
    }

    private string? GetModelFileName()
    {
        var model = Card.Model;
        if (model == null)
            return null;

        var file = model.AssociatedFilesInfo
            .FirstOrDefault(f => ModelFileExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
        return file?.Name;
    }
}
