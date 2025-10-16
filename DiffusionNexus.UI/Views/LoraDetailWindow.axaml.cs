using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace DiffusionNexus.UI.Views;

public partial class LoraDetailWindow : Window
{
    private VideoView? _videoView;
    private Image? _previewImage;
    private TextBlock? _placeholder;
    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private bool _videoInitialized;
    private string? _currentMediaPath;
    private Media? _media;

    public LoraDetailWindow()
    {
        InitializeComponent();
        _videoView = this.FindControl<VideoView>("PreviewVideo");
        _previewImage = this.FindControl<Image>("PreviewImageControl");
        _placeholder = this.FindControl<TextBlock>("PreviewPlaceholder");

        DataContextChanged += OnDataContextChanged;

        TryInitializeVideo();
        UpdatePreviewVisibility();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        TryInitializeVideo();
        UpdatePreviewVisibility();
    }

    private void TryInitializeVideo()
    {
        CleanupVideo();

        if (_videoView == null)
        {
            _videoInitialized = false;
            return;
        }

        if (DataContext is not ViewModels.LoraDetailViewModel vm || string.IsNullOrWhiteSpace(vm.PreviewMediaPath))
        {
            _videoInitialized = false;
            return;
        }

        try
        {
            Core.Initialize();
            _libVlc = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVlc)
            {
                EnableHardwareDecoding = true
            };

            _mediaPlayer.EndReached += OnMediaEnded;

            _videoView.MediaPlayer = _mediaPlayer;
            _currentMediaPath = vm.PreviewMediaPath;
            _media = new Media(_libVlc, _currentMediaPath, FromType.FromPath);
            if (_mediaPlayer.Play(_media))
            {
                _videoInitialized = true;
            }
            else
            {
                CleanupVideo();
            }
        }
        catch
        {
            CleanupVideo();
            _videoInitialized = false;
        }
    }

    private void UpdatePreviewVisibility()
    {
        if (_previewImage == null || _placeholder == null)
        {
            return;
        }

        bool hasImage = DataContext is ViewModels.LoraDetailViewModel vm && vm.PreviewImage != null;

        if (_videoView != null)
        {
            _videoView.IsVisible = _videoInitialized;
        }

        _previewImage.IsVisible = !_videoInitialized && hasImage;
        _placeholder.IsVisible = !_videoInitialized && !hasImage;
    }

    private void CleanupVideo()
    {
        if (_videoView != null)
        {
            _videoView.MediaPlayer = null;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.EndReached -= OnMediaEnded;
            _mediaPlayer.Dispose();
        }
        _mediaPlayer = null;
        _media?.Dispose();
        _media = null;
        _libVlc?.Dispose();
        _libVlc = null;
        _videoInitialized = false;
        _currentMediaPath = null;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        DataContextChanged -= OnDataContextChanged;
        CleanupVideo();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || _libVlc == null || string.IsNullOrWhiteSpace(_currentMediaPath))
        {
            return;
        }

        try
        {
            _media?.Dispose();
            _media = new Media(_libVlc, _currentMediaPath, FromType.FromPath);
            _mediaPlayer.Play(_media);
        }
        catch
        {
            // Ignore playback restart failures; the image fallback remains visible.
        }
    }
}
