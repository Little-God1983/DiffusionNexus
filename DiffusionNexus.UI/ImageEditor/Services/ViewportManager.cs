using DiffusionNexus.UI.ImageEditor.Events;

namespace DiffusionNexus.UI.ImageEditor.Services;

/// <summary>
/// Manages viewport state: zoom, pan, fit mode.
/// Publishes <see cref="ViewportChangedEvent"/> via the event bus on changes.
/// </summary>
internal sealed class ViewportManager : IViewportManager
{
    private readonly IEventBus _eventBus;
    private float _zoomLevel = 1f;
    private float _panX;
    private float _panY;
    private bool _isFitMode = true;

    private const float ZoomStep = 0.1f;

    public ViewportManager(IEventBus eventBus)
    {
        ArgumentNullException.ThrowIfNull(eventBus);
        _eventBus = eventBus;
    }

    /// <inheritdoc />
    public float MinZoom => 0.1f;

    /// <inheritdoc />
    public float MaxZoom => 10f;

    /// <inheritdoc />
    public float ZoomLevel
    {
        get => _zoomLevel;
        set
        {
            var clamped = Math.Clamp(value, MinZoom, MaxZoom);
            if (Math.Abs(_zoomLevel - clamped) < 0.0001f) return;
            _zoomLevel = clamped;
            _isFitMode = false;
            OnChanged();
        }
    }

    /// <inheritdoc />
    public int ZoomPercentage => (int)Math.Round(_zoomLevel * 100);

    /// <inheritdoc />
    public float PanX
    {
        get => _panX;
        set => _panX = value;
    }

    /// <inheritdoc />
    public float PanY
    {
        get => _panY;
        set => _panY = value;
    }

    /// <inheritdoc />
    public bool IsFitMode
    {
        get => _isFitMode;
        set
        {
            if (_isFitMode == value) return;
            _isFitMode = value;
            if (value)
            {
                _panX = 0;
                _panY = 0;
            }
            OnChanged();
        }
    }

    /// <inheritdoc />
    public void ZoomIn() => ZoomLevel += ZoomStep;

    /// <inheritdoc />
    public void ZoomOut() => ZoomLevel -= ZoomStep;

    /// <inheritdoc />
    public void ZoomToFit() => IsFitMode = true;

    /// <inheritdoc />
    public void ZoomToActual()
    {
        _zoomLevel = 1f;
        _panX = 0;
        _panY = 0;
        _isFitMode = false;
        OnChanged();
    }

    /// <inheritdoc />
    public void Reset()
    {
        _zoomLevel = 1f;
        _panX = 0;
        _panY = 0;
        _isFitMode = true;
        OnChanged();
    }

    /// <inheritdoc />
    public void SetFitModeWithZoom(float fitZoom)
    {
        _zoomLevel = Math.Clamp(fitZoom, MinZoom, MaxZoom);
        _isFitMode = true;
        _panX = 0;
        _panY = 0;
        OnChanged();
    }

    /// <inheritdoc />
    public void Pan(float deltaX, float deltaY)
    {
        if (_isFitMode) return;
        _panX += deltaX;
        _panY += deltaY;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    private void OnChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        _eventBus.Publish(new ViewportChangedEvent(_zoomLevel, _isFitMode, _panX, _panY));
    }
}
