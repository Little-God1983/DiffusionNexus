using System.Diagnostics;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// The plain status line under Inpaint's and Outpaint's Generate buttons: what the run is doing right
/// now, in words, plus a ticking elapsed time ("Generating · step 2 of 4 · 0:47"). The fun line above
/// it says nothing about progress, and the first run on a cold Engine spends minutes loading models
/// with the bar standing still, so this line is what tells the user the run is alive (#606 smoke).
/// </summary>
public sealed class GenerationStepText
{
    private readonly Func<TimeSpan> _clock;
    private Avalonia.Threading.DispatcherTimer? _timer;
    private TimeSpan? _startedAt;
    private string? _step;
    private bool _sampled;

    /// <param name="clock">Monotonic time source; tests pass their own.</param>
    public GenerationStepText(Func<TimeSpan>? clock = null)
    {
        _clock = clock ?? (static () => Stopwatch.GetElapsedTime(0));
    }

    /// <summary>Raised when <see cref="Text"/> changes, including every timer tick.</summary>
    public event EventHandler? Changed;

    /// <summary>True between <see cref="Start"/> and <see cref="Stop"/>.</summary>
    public bool IsRunning => _startedAt is not null;

    /// <summary>The line to show, or null when no run is in progress.</summary>
    public string? Text => _step is null || _startedAt is null
        ? null
        : $"{_step} · {FormatElapsed(_clock() - _startedAt.Value)}";

    /// <summary>Starts a run: resets the step and the elapsed time and starts ticking once a second.</summary>
    public void Start()
    {
        _startedAt = _clock();
        _sampled = false;
        _step = null;

        // No dispatcher in unit tests; the text still updates on every status change there.
        if (Avalonia.Application.Current is not null && _timer is null)
        {
            _timer = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
        _timer?.Start();
    }

    /// <summary>Maps a raw status (the view model's own phases or ComfyUI's progress) to the step shown.</summary>
    public void Update(string? status)
    {
        if (_startedAt is null || string.IsNullOrWhiteSpace(status)) return;

        var step = Describe(status, ref _sampled);
        if (step is null || step == _step) return;

        _step = step;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ends the run and clears the line.</summary>
    public void Stop()
    {
        _timer?.Stop();
        var hadText = _startedAt is not null;
        _startedAt = null;
        _step = null;
        _sampled = false;
        if (hadText) Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The step for <paramref name="status"/>, or null to keep the current one. ComfyUI only reports
    /// "Executing node N" between steps, so the words for it depend on whether sampling has started:
    /// before, the run is loading models; after, it is decoding and saving the image.
    /// </summary>
    internal static string? Describe(string status, ref bool sampled)
    {
        if (status.StartsWith("Progress:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = status["Progress:".Length..].Trim().Split('/');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var value)
                && int.TryParse(parts[1], out var max)
                && max > 0)
            {
                sampled = true;
                return $"Generating · step {value} of {max}";
            }
            return null;
        }

        if (status.StartsWith("Starting Diffusion Nexus Engine", StringComparison.OrdinalIgnoreCase))
            return "Starting the Diffusion Nexus Engine";
        if (status.StartsWith("Restarting Diffusion Nexus Engine", StringComparison.OrdinalIgnoreCase))
            return "Restarting the Diffusion Nexus Engine";
        if (status.StartsWith("Preparing", StringComparison.OrdinalIgnoreCase))
            return "Preparing the image";
        if (status.StartsWith("Uploading", StringComparison.OrdinalIgnoreCase))
            return "Uploading the image";
        if (status.StartsWith("Checking", StringComparison.OrdinalIgnoreCase))
            return "Checking the models";
        if (status.StartsWith("Queuing", StringComparison.OrdinalIgnoreCase))
            return "Sending the job";
        if (status.StartsWith("Downloading", StringComparison.OrdinalIgnoreCase))
            return "Downloading the result";
        if (status.StartsWith("Describing", StringComparison.OrdinalIgnoreCase))
            return "Describing the surroundings (Qwen3-VL)";
        if (status.StartsWith("Generating", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Executing", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Loading", StringComparison.OrdinalIgnoreCase)
            || status.StartsWith("Running", StringComparison.OrdinalIgnoreCase))
            return sampled ? "Finishing the image" : "Loading the models (slow on the first run)";

        return null;
    }

    internal static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        return elapsed.TotalHours >= 1
            ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"{elapsed.Minutes}:{elapsed.Seconds:00}";
    }
}
