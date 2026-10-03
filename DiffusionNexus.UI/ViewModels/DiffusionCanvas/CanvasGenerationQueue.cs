using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// One Generate press waiting in, or running from, the <see cref="CanvasGenerationQueue"/>.
/// </summary>
public sealed partial class CanvasQueuedBatchViewModel : ObservableObject
{
    private const int PromptPreviewLength = 60;

    /// <param name="number">Session-wide batch number, 1-based, shown in the queue list and the console.</param>
    /// <param name="imageCount">How many images the batch produces.</param>
    /// <param name="steps">Sampling steps per image, which is what the ETA is counted in.</param>
    /// <param name="prompt">The batch's prompt; the list shows its start.</param>
    /// <param name="payload">Whatever the owner needs to run the batch. The queue never looks at it.</param>
    public CanvasQueuedBatchViewModel(int number, int imageCount, int steps, string prompt, object? payload = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageCount, 1);

        Number = number;
        ImageCount = imageCount;
        Steps = Math.Max(1, steps);
        Payload = payload;

        var flat = (prompt ?? string.Empty).ReplaceLineEndings(" ").Trim();
        PromptPreview = flat.Length <= PromptPreviewLength ? flat : flat[..PromptPreviewLength] + "…";
    }

    public int Number { get; }

    public int ImageCount { get; }

    public int Steps { get; }

    public string PromptPreview { get; }

    public object? Payload { get; }

    /// <summary>The list row's first line, e.g. <c>#3 · ×4 · a lighthouse at dusk</c>.</summary>
    public string Label => $"#{Number} · ×{ImageCount} · {PromptPreview}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(RemoveText))]
    private bool _isRunning;

    /// <summary>Images of this batch that are over: finished, failed or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private int _imagesDone;

    public string StateText => IsRunning ? $"Running · {Math.Min(ImagesDone + 1, ImageCount)}/{ImageCount}" : "Queued";

    /// <summary>The row button's caption: a running batch is cancelled, a queued one removed.</summary>
    public string RemoveText => IsRunning ? "Cancel" : "Remove";

    /// <summary>Removes this batch from its queue (cancels it when it is the running one). Set by the queue on enqueue.</summary>
    public IRelayCommand? RemoveCommand { get; internal set; }

    internal TaskCompletionSource Completion { get; } = new();
}

/// <summary>
/// The canvas's generation queue: Generate adds a batch, one worker runs them in order, and the status
/// bar reads its count, throughput and ETA from here (issue #598).
/// </summary>
/// <remarks>
/// Batches run one at a time on purpose. <c>DiffusionContextHost</c> keeps a single model resident, so
/// two batches at once would either serialise behind its lock or thrash VRAM. The queue does not know
/// what a batch does; the owner supplies the runner and reports progress back.
/// </remarks>
public sealed partial class CanvasGenerationQueue : ObservableObject
{
    private readonly Func<CanvasQueuedBatchViewModel, CancellationToken, Task> _runBatch;
    private readonly Action<string> _trace;
    private readonly Func<TimeSpan> _clock;

    /// <summary>
    /// Guards the worker flag and <see cref="_runCts"/>. Enqueue decides whether to start the worker and
    /// Cancel cancels-and-nulls the epoch; without a lock those can interleave with the worker's own exit.
    /// </summary>
    private readonly object _gate = new();

    private bool _pumping;

    /// <summary>
    /// The running batch's epoch. The invariant, copied from <c>CivitaiDownloadQueue</c>: <b>never cancel
    /// without nulling</b>. A cancelled source left installed would be cancelled a second time by the next
    /// Cancel instead of the batch that is then running.
    /// </summary>
    private CancellationTokenSource? _runCts;

    private CanvasQueuedBatchViewModel? _running;

    // Progress of the image in flight.
    private bool _imageInFlight;
    private TimeSpan _imageStartedAt;
    private int _step;
    private int _totalSteps;
    private double _iterationsPerSecond;

    // Measured over the images finished since the queue last went idle.
    private double _measuredSeconds;
    private int _measuredSteps;
    private int _measuredImages;

    /// <param name="runBatch">Runs one batch to its end. It should handle its own failures; one that throws is logged and the queue carries on.</param>
    /// <param name="trace">Unified Console sink (standing rule: every step is traced).</param>
    /// <param name="clock">Monotonic time source; a test seam for the ETA.</param>
    public CanvasGenerationQueue(
        Func<CanvasQueuedBatchViewModel, CancellationToken, Task> runBatch,
        Action<string> trace,
        Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(runBatch);
        ArgumentNullException.ThrowIfNull(trace);

        _runBatch = runBatch;
        _trace = trace;
        _clock = clock ?? (static () => Stopwatch.GetElapsedTime(0));
        ClearQueuedCommand = new RelayCommand(ClearQueued, () => QueuedCount > 0);
    }

    /// <summary>The running batch first, then the queued ones in the order they will run.</summary>
    public ObservableCollection<CanvasQueuedBatchViewModel> Batches { get; } = [];

    /// <summary>True from the first enqueue until the last batch is over.</summary>
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>The batch the worker is on, or null.</summary>
    public CanvasQueuedBatchViewModel? Running => _running;

    /// <summary>Batches waiting behind the running one.</summary>
    public int QueuedCount => Batches.Count(b => !b.IsRunning);

    /// <summary>Removes every queued batch; the running one carries on.</summary>
    public IRelayCommand ClearQueuedCommand { get; }

    /// <summary>Raised for a batch that left the queue without running. The owner removes its staging slots.</summary>
    public event EventHandler<CanvasQueuedBatchViewModel>? BatchDropped;

    /// <summary>
    /// Adds a batch. The returned task completes when the batch is over: finished, cancelled or removed.
    /// </summary>
    public Task Enqueue(CanvasQueuedBatchViewModel batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        batch.RemoveCommand = new RelayCommand(() => Remove(batch));

        bool start;
        lock (_gate)
        {
            Batches.Add(batch);
            start = !_pumping;
            _pumping = true;
        }

        _trace(start
            ? $"Batch #{batch.Number} enqueued ({batch.ImageCount} image(s)); the queue was idle, starting it."
            : $"Batch #{batch.Number} enqueued ({batch.ImageCount} image(s)); {QueuedCount} waiting.");
        RaiseReadout();

        if (start)
            _ = PumpAsync();

        return batch.Completion.Task;
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            CanvasQueuedBatchViewModel batch;
            CancellationTokenSource cts;
            lock (_gate)
            {
                if (Batches.Count == 0)
                {
                    _pumping = false;
                    return;
                }

                batch = Batches[0];
                cts = new CancellationTokenSource();
                _runCts = cts;
                _running = batch;
            }

            batch.IsRunning = true;
            ResetImageProgress();
            IsBusy = true;
            _trace($"Batch #{batch.Number} started.");
            RaiseReadout();

            try
            {
                await _runBatch(batch, cts.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                // The runner reports its own failures; this only keeps one broken batch from stranding
                // every batch queued behind it.
                _trace($"Batch #{batch.Number} ended with an unhandled error: {ex.Message}");
            }

            bool cancelled;
            lock (_gate)
            {
                // Cancel nulls the field itself, so a batch that was cancelled no longer owns it.
                cancelled = !ReferenceEquals(_runCts, cts);
                if (!cancelled)
                    _runCts = null;
            }

            cts.Dispose();

            _running = null;
            Batches.Remove(batch);
            ResetImageProgress();
            _trace(cancelled ? $"Batch #{batch.Number} cancelled." : $"Batch #{batch.Number} finished.");

            if (Batches.Count == 0)
            {
                _measuredSeconds = 0;
                _measuredSteps = 0;
                _measuredImages = 0;
                _iterationsPerSecond = 0;
                IsBusy = false;
                _trace("The generation queue is empty.");
            }

            RaiseReadout();

            // Last, so whoever awaits the batch sees the queue's state already settled.
            batch.Completion.TrySetResult();
        }
    }

    /// <summary>
    /// Cancels the running batch; the worker then moves on to the next one. Returns the batch that was
    /// cancelled, or null when nothing was running.
    /// </summary>
    public CanvasQueuedBatchViewModel? CancelRunning()
    {
        CancellationTokenSource? cts;
        CanvasQueuedBatchViewModel? cancelled;
        lock (_gate)
        {
            cts = _runCts;
            _runCts = null;
            // Read before cancelling: the batch can unwind inside Cancel(), and the worker would then
            // already be on the next one.
            cancelled = _running;
        }

        if (cts is null)
            return null;

        try
        {
            // Cancelled but deliberately NOT disposed here: the running batch still holds this token and
            // both backends register callbacks on it. The worker disposes it once the batch has unwound.
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The batch finished and the worker disposed it as we cancelled; nothing left to stop.
        }

        return cancelled;
    }

    /// <summary>Removes a queued batch, or cancels it when it is the running one.</summary>
    public void Remove(CanvasQueuedBatchViewModel batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (ReferenceEquals(batch, _running))
        {
            RunningCancelRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (Drop(batch))
            _trace($"Batch #{batch.Number} removed from the queue.");
    }

    /// <summary>
    /// Raised when the running batch's own row asks to cancel it. The owner cancels through its usual
    /// path, which also tidies the staging strip.
    /// </summary>
    public event EventHandler? RunningCancelRequested;

    private void ClearQueued()
    {
        var queued = Batches.Where(b => !b.IsRunning).ToList();
        foreach (var batch in queued)
            Drop(batch);

        if (queued.Count > 0)
            _trace($"Cleared the queue: {queued.Count} batch(es) removed.");
    }

    /// <summary>Cancels the running batch and drops every queued one. For the owner's teardown.</summary>
    public void Shutdown()
    {
        ClearQueued();
        CancelRunning();
    }

    private bool Drop(CanvasQueuedBatchViewModel batch)
    {
        bool removed;
        lock (_gate)
            removed = !ReferenceEquals(batch, _running) && Batches.Remove(batch);

        if (!removed)
            return false;

        RaiseReadout();
        BatchDropped?.Invoke(this, batch);
        batch.Completion.TrySetResult();
        return true;
    }

    // ────────────────────────────── Progress intake ──────────────────────────────

    /// <summary>The running batch started an image.</summary>
    public void ImageStarted()
    {
        ResetImageProgress();
        _imageInFlight = true;
        _imageStartedAt = _clock();
        RaiseReadout();
    }

    /// <summary>A sampling step of the image in flight. <paramref name="iterationsPerSecond"/> is 0 when the backend does not report it.</summary>
    public void ReportStep(int step, int totalSteps, double iterationsPerSecond)
    {
        _step = step;
        _totalSteps = totalSteps;
        if (iterationsPerSecond > 0 && double.IsFinite(iterationsPerSecond))
            _iterationsPerSecond = iterationsPerSecond;
        RaiseReadout();
    }

    /// <summary>The image in flight is over. Only a successful one feeds the measured pace.</summary>
    public void ImageFinished(bool succeeded)
    {
        if (_imageInFlight && succeeded && _running is { } batch)
        {
            _measuredSeconds += (_clock() - _imageStartedAt).TotalSeconds;
            _measuredSteps += batch.Steps;
            _measuredImages++;
        }

        CompleteImage();
    }

    /// <summary>An image of the running batch will not run (its slot was discarded).</summary>
    public void ImageSkipped() => CompleteImage();

    private void CompleteImage()
    {
        ResetImageProgress();

        if (_running is { } batch && batch.ImagesDone < batch.ImageCount)
            batch.ImagesDone++;

        RaiseReadout();
    }

    /// <summary>
    /// Forgets the image in flight, but not the step rate. The rate outlives an image and a batch: the
    /// next one has not reported a step yet, and showing nothing until it does would make the readout
    /// blink. It is dropped when the queue goes idle.
    /// </summary>
    private void ResetImageProgress()
    {
        _imageInFlight = false;
        _step = 0;
        _totalSteps = 0;
    }

    // ────────────────────────────── Readout ──────────────────────────────

    /// <summary>e.g. <c>Image 2/4 · 2 batches queued (8 images)</c>; empty while idle.</summary>
    public string QueueText
    {
        get
        {
            if (_running is not { } batch)
                return string.Empty;

            var text = $"Image {Math.Min(batch.ImagesDone + 1, batch.ImageCount)}/{batch.ImageCount}";
            var queued = Batches.Where(b => !b.IsRunning).ToList();
            if (queued.Count == 0)
                return text;

            var images = queued.Sum(b => b.ImageCount);
            return $"{text} · {queued.Count} {(queued.Count == 1 ? "batch" : "batches")} queued " +
                   $"({images} {(images == 1 ? "image" : "images")})";
        }
    }

    /// <summary>
    /// The pace: steps per second when the backend reports them, seconds per step below one, seconds per
    /// image when it reports no steps and an image has finished, otherwise empty.
    /// </summary>
    public string ThroughputText
    {
        get
        {
            if (_running is null)
                return string.Empty;

            if (_iterationsPerSecond >= 1)
                return string.Create(CultureInfo.InvariantCulture, $"{_iterationsPerSecond:0.0} it/s");
            if (_iterationsPerSecond > 0)
                return string.Create(CultureInfo.InvariantCulture, $"{1 / _iterationsPerSecond:0.0} s/it");
            if (_measuredImages > 0)
                return string.Create(CultureInfo.InvariantCulture, $"{_measuredSeconds / _measuredImages:0.#} s/image");

            return string.Empty;
        }
    }

    /// <summary><c>ETA 0:48</c> for the whole queue, <c>ETA —</c> while no pace is known, empty while idle.</summary>
    public string EtaText
    {
        get
        {
            if (_running is null)
                return string.Empty;

            return EstimateRemaining() is { } remaining ? $"ETA {Format(remaining)}" : "ETA —";
        }
    }

    /// <summary>
    /// Time until the queue is empty, or null when nothing has been measured yet. Counted in steps, so a
    /// queued batch with more steps weighs more.
    /// </summary>
    internal TimeSpan? EstimateRemaining()
    {
        if (_running is not { } running)
            return null;

        // Seconds per step over whole images: it carries the load, encode and decode time a bare step
        // rate leaves out.
        double? measuredPerStep = _measuredSteps > 0 ? _measuredSeconds / _measuredSteps : null;
        double? ratePerStep = _iterationsPerSecond > 0 ? 1 / _iterationsPerSecond : null;
        var perStep = measuredPerStep ?? ratePerStep;
        if (perStep is null)
            return null;

        var notStarted = running.ImageCount - running.ImagesDone - (_imageInFlight ? 1 : 0);
        double stepsAhead = Math.Max(0, notStarted) * running.Steps;
        foreach (var batch in Batches)
        {
            if (!batch.IsRunning)
                stepsAhead += (double)batch.ImageCount * batch.Steps;
        }

        double current = 0;
        if (_imageInFlight)
        {
            if (ratePerStep is { } rate && _totalSteps > 0)
            {
                current = Math.Max(0, _totalSteps - _step) * rate;
            }
            else
            {
                var elapsed = (_clock() - _imageStartedAt).TotalSeconds;
                current = Math.Max(0, running.Steps * perStep.Value - elapsed);
            }
        }

        return TimeSpan.FromSeconds(current + stepsAhead * perStep.Value);
    }

    private static string Format(TimeSpan span)
    {
        var total = (long)Math.Ceiling(span.TotalSeconds);
        var hours = total / 3600;
        var minutes = total % 3600 / 60;
        var seconds = total % 60;
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{seconds:00}");
    }

    private void RaiseReadout()
    {
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(ThroughputText));
        OnPropertyChanged(nameof(EtaText));
        OnPropertyChanged(nameof(QueuedCount));
        ClearQueuedCommand.NotifyCanExecuteChanged();
    }
}
