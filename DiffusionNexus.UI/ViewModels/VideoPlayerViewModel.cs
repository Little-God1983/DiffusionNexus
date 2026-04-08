using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibVLCSharp.Shared;

namespace DiffusionNexus.UI.ViewModels;

// TODO: Linux Implementation for LibVLC initialization (may need different native library path)

/// <summary>
/// ViewModel for the video player control.
/// Manages LibVLC playback state and exposes bindable properties for transport controls.
/// </summary>
public sealed partial class VideoPlayerViewModel : ObservableObject, IDisposable
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private bool _disposed;
    private bool _isPlaying;
    private bool _hasVideo;
    private double _position;
    private long _duration;
    private int _volume = 75;
    private string? _videoPath;
    private string _timeDisplay = "00:00 / 00:00";
    private static bool s_coreInitialized;

    public VideoPlayerViewModel()
    {
        InitializeCommands();
    }

    /// <summary>
    /// Whether a video is currently loaded.
    /// </summary>
    public bool HasVideo
    {
        get => _hasVideo;
        private set
        {
            if (SetProperty(ref _hasVideo, value))
                NotifyCommandStates();
        }
    }

    /// <summary>
    /// Whether the video is currently playing.
    /// </summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>
    /// Current playback position as a 0.0–1.0 fraction.
    /// </summary>
    public double Position
    {
        get => _position;
        set
        {
            if (SetProperty(ref _position, value) && _mediaPlayer is not null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Position = (float)value;
            }
        }
    }

    /// <summary>
    /// Video duration in milliseconds.
    /// </summary>
    public long Duration
    {
        get => _duration;
        private set => SetProperty(ref _duration, value);
    }

    /// <summary>
    /// Volume level (0–100).
    /// </summary>
    public int Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 100)))
            {
                if (_mediaPlayer is not null)
                    _mediaPlayer.Volume = _volume;
            }
        }
    }

    /// <summary>
    /// Formatted time display (e.g., "01:23 / 04:56").
    /// </summary>
    public string TimeDisplay
    {
        get => _timeDisplay;
        private set => SetProperty(ref _timeDisplay, value);
    }

    /// <summary>
    /// Path to the currently loaded video file.
    /// </summary>
    public string? VideoPath
    {
        get => _videoPath;
        private set => SetProperty(ref _videoPath, value);
    }

    /// <summary>
    /// The underlying LibVLC MediaPlayer instance for binding to the VideoView control.
    /// </summary>
    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set => SetProperty(ref _mediaPlayer, value);
    }

    /// <summary>
    /// Loads and starts playing a video from the specified path.
    /// </summary>
    /// <param name="videoPath">Absolute path to the video file.</param>
    public void LoadVideo(string videoPath)
    {
        ArgumentNullException.ThrowIfNull(videoPath);

        Stop();

        if (!s_coreInitialized)
        {
            Core.Initialize();
            s_coreInitialized = true;
        }

        _libVLC ??= new LibVLC();

        var media = new Media(_libVLC, new Uri(videoPath));
        var player = new MediaPlayer(media) { Volume = _volume };

        player.PositionChanged += OnPositionChanged;
        player.LengthChanged += OnLengthChanged;
        player.EndReached += OnEndReached;
        player.Playing += OnPlaying;
        player.Paused += OnPaused;
        player.Stopped += OnStopped;

        MediaPlayer = player;
        VideoPath = videoPath;
        HasVideo = true;

        media.Dispose();
    }

    /// <summary>
    /// Command for stopping playback.
    /// </summary>
    public IRelayCommand StopCommand { get; private set; } = null!;

    /// <summary>
    /// Toggles between play and pause.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasVideo))]
    private void PlayPause()
    {
        if (_mediaPlayer is null) return;

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else if (_mediaPlayer.State == VLCState.Ended)
        {
            _mediaPlayer.Stop();
            _mediaPlayer.Play();
        }
        else
        {
            _mediaPlayer.Play();
        }
    }

    /// <summary>
    /// Stops playback, disposes the media player, and resets all state.
    /// LibVLC requires the VideoView to be unbound (MediaPlayer = null) before
    /// calling Stop/Dispose to avoid crashes. The actual stop runs on a
    /// thread-pool thread because libvlc_media_player_stop blocks and can
    /// deadlock when called from a VLC callback or the UI thread.
    /// </summary>
    public void Stop()
    {
        var player = _mediaPlayer;
        if (player is not null)
        {
            // Unbind from VideoView BEFORE stopping — LibVLCSharp requirement
            _mediaPlayer = null;
            MediaPlayer = null;

            player.PositionChanged -= OnPositionChanged;
            player.LengthChanged -= OnLengthChanged;
            player.EndReached -= OnEndReached;
            player.Playing -= OnPlaying;
            player.Paused -= OnPaused;
            player.Stopped -= OnStopped;

            // Stop and dispose on a thread-pool thread to avoid VLC deadlock
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    player.Stop();
                }
                finally
                {
                    player.Dispose();
                }
            });
        }

        IsPlaying = false;
        Position = 0;
        Duration = 0;
        TimeDisplay = "00:00 / 00:00";
        HasVideo = false;
        VideoPath = null;
    }

    /// <summary>
    /// Toggles mute (volume 0 vs previous volume).
    /// </summary>
    [RelayCommand]
    private void ToggleMute()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.ToggleMute();
    }

    private void OnPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        if (_disposed) return;
        // Avoid re-entrant property change from slider → player → slider
        _position = e.Position;
        OnPropertyChanged(nameof(Position));
        UpdateTimeDisplay();
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        if (_disposed) return;
        Duration = e.Length;
        UpdateTimeDisplay();
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        if (!_disposed) IsPlaying = true;
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        if (!_disposed) IsPlaying = false;
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        if (!_disposed) IsPlaying = false;
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        if (!_disposed) IsPlaying = false;
    }

    private void UpdateTimeDisplay()
    {
        var current = TimeSpan.FromMilliseconds(_duration * _position);
        var total = TimeSpan.FromMilliseconds(_duration);
        TimeDisplay = $"{FormatTime(current)} / {FormatTime(total)}";
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");
    }

    private void NotifyCommandStates()
    {
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Initializes the manually-created StopCommand after construction.
    /// Must be called in the constructor or during initialization.
    /// </summary>
    private void InitializeCommands()
    {
        StopCommand = new RelayCommand(Stop, () => HasVideo);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var player = _mediaPlayer;
        if (player is not null)
        {
            _mediaPlayer = null;
            MediaPlayer = null;

            player.PositionChanged -= OnPositionChanged;
            player.LengthChanged -= OnLengthChanged;
            player.EndReached -= OnEndReached;
            player.Playing -= OnPlaying;
            player.Paused -= OnPaused;
            player.Stopped -= OnStopped;

            // Stop/dispose player, then dispose LibVLC on thread pool
            // to avoid deadlocking the UI thread during VLC shutdown.
            var libVlc = _libVLC;
            _libVLC = null;

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    player.Stop();
                }
                finally
                {
                    player.Dispose();
                    libVlc?.Dispose();
                }
            });
        }
        else
        {
            _libVLC?.Dispose();
            _libVLC = null;
        }

        IsPlaying = false;
        HasVideo = false;
    }
}
