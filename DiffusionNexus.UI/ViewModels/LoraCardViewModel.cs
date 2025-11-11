using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Service.Classes;
using DiffusionNexus.UI.Classes;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using OpenCvSharp;

namespace DiffusionNexus.UI.ViewModels;

public partial class LoraCardViewModel : ViewModelBase
{
    [ObservableProperty]
    private string? _description;

    [ObservableProperty]
    private ModelClass? _model;

    [ObservableProperty]
    private Bitmap? _previewImage;

    [ObservableProperty]
    private string? folderPath;

    [ObservableProperty]
    private string? treePath;

    private readonly object _videoLock = new();
    private string? _previewMediaPath;
    private VideoCapture? _videoCapture;
    private DispatcherTimer? _videoTimer;
    private Bitmap? _videoFrame;
    private bool _isVideoPreviewEnabled;

    public IEnumerable<string> DiffusionTypes => Model is null
        ? Array.Empty<string>()
        : new[] { Model.ModelType.ToString() };

    public string DiffusionBaseModel => Model?.DiffusionBaseModel ?? string.Empty;

    public IRelayCommand EditCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand OpenWebCommand { get; }
    public IAsyncRelayCommand CopyCommand { get; }
    public IAsyncRelayCommand CopyNameCommand { get; }
    public IRelayCommand OpenFolderCommand { get; }
    public IAsyncRelayCommand OpenDetailsCommand { get; }
    public IAsyncRelayCommand DownloadMetadataCommand { get; }

    public ObservableCollection<LoraVariantViewModel> Variants { get; } = new();

    public bool HasVariants => Variants.Count > 1;

    public bool ShouldShowDownloadMetadataButton => Model != null && !Model.HasFullMetadata;

    public Bitmap? VideoFrame
    {
        get => _videoFrame;
        private set
        {
            if (ReferenceEquals(_videoFrame, value))
                return;

            _videoFrame?.Dispose();
            _videoFrame = value;
            OnPropertyChanged(nameof(VideoFrame));
        }
    }

    public bool HasVideo => _videoCapture != null;
    public bool HasImage => PreviewImage != null;
    public bool ShouldShowImage => !HasVideo && HasImage;
    public bool ShowPlaceholder => !HasVideo && !HasImage;

    public LoraHelperViewModel? Parent { get; set; }

    public LoraCardViewModel()
    {
        EditCommand = new RelayCommand(OnEdit);
        DeleteCommand = new AsyncRelayCommand(OnDeleteAsync);
        OpenWebCommand = new AsyncRelayCommand(OnOpenWebAsync);
        CopyCommand = new AsyncRelayCommand(OnCopyAsync);
        CopyNameCommand = new AsyncRelayCommand(OnCopyNameAsync);
        OpenFolderCommand = new RelayCommand(OnOpenFolder);
        OpenDetailsCommand = new AsyncRelayCommand(OnOpenDetailsAsync);
        DownloadMetadataCommand = new AsyncRelayCommand(OnDownloadMetadataAsync);
        Variants.CollectionChanged += OnVariantsCollectionChanged;
    }

    partial void OnModelChanged(ModelClass? value)
    {
        _ = LoadPreviewImageAsync();
        OnPropertyChanged(nameof(ShouldShowDownloadMetadataButton));
    }

    partial void OnPreviewImageChanged(Bitmap? value)
    {
        OnPropertyChanged(nameof(HasImage));
        OnPropertyChanged(nameof(ShouldShowImage));
        OnPropertyChanged(nameof(ShowPlaceholder));
    }

    internal void SetVariants(IReadOnlyList<LoraVariantDescriptor> variants)
    {
        Variants.CollectionChanged -= OnVariantsCollectionChanged;
        Variants.Clear();

        if (variants != null && variants.Count > 0)
        {
            foreach (var variant in variants)
            {
                var option = new LoraVariantViewModel(variant.Label, variant.Model, OnVariantSelected);
                option.IsSelected = ReferenceEquals(variant.Model, Model);
                Variants.Add(option);
            }

            if (Variants.Count > 0 && Variants.All(v => !v.IsSelected))
            {
                var preferred = Variants.FirstOrDefault(v => string.Equals(v.Label, "High", StringComparison.OrdinalIgnoreCase))
                    ?? Variants.First();
                preferred.IsSelected = true;
                ApplyVariant(preferred);
            }
            else
            {
                var selected = Variants.FirstOrDefault(v => v.IsSelected);
                if (selected != null)
                {
                    ApplyVariant(selected);
                }
            }
        }

        Variants.CollectionChanged += OnVariantsCollectionChanged;
        OnPropertyChanged(nameof(HasVariants));
    }

    private void OnVariantsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasVariants));
    }

    private void OnVariantSelected(LoraVariantViewModel option)
    {
        foreach (var variant in Variants)
        {
            variant.IsSelected = ReferenceEquals(variant, option);
        }

        ApplyVariant(option);
    }

    private void ApplyVariant(LoraVariantViewModel option)
    {
        if (option == null)
        {
            return;
        }

        if (!ReferenceEquals(Model, option.Model))
        {
            Model = option.Model;
        }
    }

    private async Task LoadPreviewImageAsync()
    {
        var path = GetPreviewImagePath();
        if (path is null || !File.Exists(path))
        {
            var media = GetPreviewMediaPath();
            if (media is not null && ThumbnailSettings.GenerateVideoThumbnails)
            {
                path = await ThumbnailGenerator.GenerateThumbnailAsync(media);
            }
        }

        Bitmap? bitmap = null;

        if (path is not null && File.Exists(path))
        {
            try
            {
                bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return new Bitmap(stream);
                });
            }
            catch
            {
                bitmap = null;
            }
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            PreviewImage = bitmap;
            _previewMediaPath = GetPreviewMediaPath();

            if (_isVideoPreviewEnabled)
            {
                StartVideoPreview();
            }
            else
            {
                StopVideoPreview();
            }
        });
    }

    public void ApplyVideoPreviewSetting(bool isEnabled)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyVideoPreviewSetting(isEnabled));
            return;
        }

        if (_isVideoPreviewEnabled == isEnabled && (!isEnabled || HasVideo))
        {
            return;
        }

        _isVideoPreviewEnabled = isEnabled;

        if (_isVideoPreviewEnabled)
        {
            StartVideoPreview();
        }
        else
        {
            StopVideoPreview();
        }
    }

    public void DisposeVideoPreview()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(StopVideoPreview);
            return;
        }

        StopVideoPreview();
    }

    private void StartVideoPreview()
    {
        StopVideoPreview();

        if (!_isVideoPreviewEnabled)
        {
            return;
        }

        _previewMediaPath = GetPreviewMediaPath();
        if (string.IsNullOrWhiteSpace(_previewMediaPath) || !File.Exists(_previewMediaPath))
        {
            OnPropertyChanged(nameof(ShouldShowImage));
            OnPropertyChanged(nameof(ShowPlaceholder));
            return;
        }

        try
        {
            var capture = new VideoCapture(_previewMediaPath);
            if (!capture.IsOpened())
            {
                capture.Dispose();
                OnPropertyChanged(nameof(ShouldShowImage));
                OnPropertyChanged(nameof(ShowPlaceholder));
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
            Log($"Failed to start video preview: {ex.Message}", LogSeverity.Warning);
            StopVideoPreview();
        }
    }

    private void StopVideoPreview()
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

    public string? GetPreviewImagePath()
    {
        if (Model == null) return null;
      
        foreach (var ext in SupportedTypes.ImageTypesByPriority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }
        
        return null;
    }

    public string? GetPreviewMediaPath()
    {
        if (Model == null) return null;


        foreach (var ext in SupportedTypes.VideoTypesByPriority)
        {
            var file = Model.AssociatedFilesInfo.FirstOrDefault(f => f.Name.EndsWith(ext, StringComparison.OrdinalIgnoreCase));
            if (file != null)
                return file.FullName;
        }

        return null;
    }

    private void OnEdit() => Log($"Edit {Model.SafeTensorFileName}", LogSeverity.Info);

    private Task OnDeleteAsync()
    {
        return Parent?.DeleteCardAsync(this) ?? Task.CompletedTask;
    }

    private Task OnOpenDetailsAsync()
    {
        return Parent?.ShowDetailsAsync(this) ?? Task.CompletedTask;
    }

    private async Task OnOpenWebAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.OpenWebForCardAsync(this);
    }

    private async Task OnCopyAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.CopyTrainedWordsAsync(this);
    }

    private async Task OnCopyNameAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.CopyModelNameAsync(this);
    }

    private async Task OnDownloadMetadataAsync()
    {
        if (Parent == null || Model == null)
            return;

        await Parent.DownloadMetadataForCardAsync(this);
        OnPropertyChanged(nameof(ShouldShowDownloadMetadataButton));
    }

    private void OnOpenFolder()
    {
        if (string.IsNullOrWhiteSpace(FolderPath))
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = FolderPath,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        catch (Exception ex)
        {
            Log($"failed to open folder: {ex.Message}", LogSeverity.Error);
        }
    }
}
