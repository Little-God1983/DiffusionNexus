using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.UI.ImageEditor;
using SkiaSharp;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel wrapper for a Layer, providing UI-bindable properties.
/// </summary>
public partial class LayerViewModel : ObservableObject
{
    private readonly Layer _layer;
    private readonly Action<LayerViewModel>? _onSelectionRequested;
    private readonly Action<LayerViewModel>? _onDeleteRequested;
    private Bitmap? _thumbnailBitmap;
    private bool _isSelected;

    public LayerViewModel(Layer layer, Action<LayerViewModel>? onSelectionRequested = null, Action<LayerViewModel>? onDeleteRequested = null)
    {
        _layer = layer;
        _onSelectionRequested = onSelectionRequested;
        _onDeleteRequested = onDeleteRequested;

        // Subscribe to layer property changes
        _layer.PropertyChanged += OnLayerPropertyChanged;
        _layer.ContentChanged += OnLayerContentChanged;

        UpdateThumbnail();
    }

    /// <summary>
    /// Gets the underlying layer.
    /// </summary>
    public Layer Layer => _layer;

    /// <summary>
    /// Gets or sets the layer name.
    /// </summary>
    public string Name
    {
        get => _layer.Name;
        set
        {
            if (_layer.Name != value)
            {
                _layer.Name = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets whether the layer is visible.
    /// </summary>
    public bool IsVisible
    {
        get => _layer.IsVisible;
        set
        {
            if (_layer.IsVisible != value)
            {
                _layer.IsVisible = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the layer opacity (0-100 for UI slider).
    /// </summary>
    public int OpacityPercent
    {
        get => (int)(_layer.Opacity * 100);
        set
        {
            var normalized = Math.Clamp(value / 100f, 0f, 1f);
            if (Math.Abs(_layer.Opacity - normalized) > 0.001f)
            {
                _layer.Opacity = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OpacityText));
            }
        }
    }

    /// <summary>
    /// Gets the opacity as display text.
    /// </summary>
    public string OpacityText => $"{OpacityPercent}%";

    /// <summary>
    /// Gets or sets whether the layer is locked.
    /// </summary>
    public bool IsLocked
    {
        get => _layer.IsLocked;
        set
        {
            if (_layer.IsLocked != value)
            {
                _layer.IsLocked = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Gets or sets the blend mode.
    /// </summary>
    public BlendMode BlendMode
    {
        get => _layer.BlendMode;
        set
        {
            if (_layer.BlendMode != value)
            {
                _layer.BlendMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(BlendModeText));
            }
        }
    }

    /// <summary>
    /// Gets the blend mode as display text.
    /// </summary>
    public string BlendModeText => _layer.BlendMode.GetDisplayName();

    /// <summary>
    /// Gets or sets whether this layer is selected.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>
    /// Gets the thumbnail for UI display.
    /// </summary>
    public Bitmap? Thumbnail => _thumbnailBitmap;

    /// <summary>
    /// Command to select this layer.
    /// </summary>
    [RelayCommand]
    private void Select()
    {
        _onSelectionRequested?.Invoke(this);
    }

    /// <summary>
    /// Command to delete this layer.
    /// </summary>
    [RelayCommand]
    private void Delete()
    {
        _onDeleteRequested?.Invoke(this);
    }

    /// <summary>
    /// Available blend modes for UI binding.
    /// </summary>
    public static IEnumerable<BlendMode> AvailableBlendModes => Enum.GetValues<BlendMode>();

    private void OnLayerPropertyChanged(Layer layer, string propertyName)
    {
        // Forward property changes
        OnPropertyChanged(propertyName);

        if (propertyName == nameof(Layer.Opacity))
        {
            OnPropertyChanged(nameof(OpacityPercent));
            OnPropertyChanged(nameof(OpacityText));
        }
        else if (propertyName == nameof(Layer.BlendMode))
        {
            OnPropertyChanged(nameof(BlendModeText));
        }
    }

    private void OnLayerContentChanged(object? sender, EventArgs e)
    {
        UpdateThumbnail();
    }

    private void UpdateThumbnail()
    {
        var oldThumbnail = _thumbnailBitmap;

        if (_layer.Thumbnail != null)
        {
            try
            {
                // Convert SKBitmap to Avalonia Bitmap
                using var image = SKImage.FromBitmap(_layer.Thumbnail);
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                using var stream = new MemoryStream();
                data.SaveTo(stream);
                stream.Position = 0;
                _thumbnailBitmap = new Bitmap(stream);
            }
            catch
            {
                _thumbnailBitmap = null;
            }
        }
        else
        {
            _thumbnailBitmap = null;
        }

        OnPropertyChanged(nameof(Thumbnail));
        oldThumbnail?.Dispose();
    }

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        _layer.PropertyChanged -= OnLayerPropertyChanged;
        _layer.ContentChanged -= OnLayerContentChanged;
        _thumbnailBitmap?.Dispose();
        _thumbnailBitmap = null;
    }
}
