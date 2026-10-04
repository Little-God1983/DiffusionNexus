using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DiffusionNexus.UI.ViewModels.DiffusionCanvas;

/// <summary>
/// What a queued batch does, supplied by its owner as closures so the queue carries no untyped payload.
/// </summary>
/// <param name="Run">Runs the batch to its end. It should handle its own failures; one that throws is logged and the queue carries on.</param>
/// <param name="OnDropped">The batch left the queue without running (removed, cleared, or nothing left to make).</param>
/// <param name="OnCancelling">The running batch is about to be cancelled; called before its token fires.</param>
/// <param name="PendingImages">
/// How many of the batch's images are still to start. The user can discard a slot before it runs, so
/// this is asked for rather than counted down from the image count. Null means "none were discarded".
/// </param>
public sealed record CanvasBatchWork(
    Func<CancellationToken, Task> Run,
    Action? OnDropped = null,
    Action? OnCancelling = null,
    Func<int>? PendingImages = null);

/// <summary>
/// One Generate press waiting in, or running from, the <see cref="CanvasGenerationQueue"/>.
/// </summary>
public sealed partial class CanvasQueuedBatchViewModel : ObservableObject
{
    private const int PromptPreviewLength = 60;

    /// <param name="number">Session-wide batch number, 1-based, shown in the queue list and the console.</param>
    /// <param name="imageCount">How many images the batch was queued with.</param>
    /// <param name="steps">Sampling steps per image, which is what the ETA is counted in.</param>
    /// <param name="prompt">The batch's prompt; the list shows its start.</param>
    /// <param name="paceKey">
    /// What the batch's speed depends on (backend, model, size). A batch with another key than the one
    /// before it starts with no known pace instead of inheriting a wrong one.
    /// </param>
    public CanvasQueuedBatchViewModel(int number, int imageCount, int steps, string prompt, string paceKey = "")
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(imageCount, 1);

        Number = number;
        ImageCount = imageCount;
        Steps = Math.Max(1, steps);
        PaceKey = paceKey ?? string.Empty;

        var flat = (prompt ?? string.Empty).ReplaceLineEndings(" ").Trim();
        PromptPreview = flat.Length <= PromptPreviewLength ? flat : flat[..PromptPreviewLength] + "…";
    }

    public int Number { get; }

    public int ImageCount { get; }

    public int Steps { get; }

    public string PaceKey { get; }

    public string PromptPreview { get; }

    /// <summary>The list row's first line, e.g. <c>#3 · ×4 · a lighthouse at dusk</c>.</summary>
    public string Label => $"#{Number} · ×{ImageCount} · {PromptPreview}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(RemoveText))]
    private bool _isRunning;

    /// <summary>
    /// The batch was cancelled and its backend is still unwinding. The local backend cannot stop a
    /// native sampling call, so this can last tens of seconds; the row says so instead of offering a
    /// Cancel that would do nothing.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private bool _isCancelling;

    partial void OnIsCancellingChanged(bool value) => RemoveCommand?.NotifyCanExecuteChanged();

    /// <summary>Images of this batch that are over: finished, failed or skipped.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText))]
    private int _imagesDone;

    public string StateText => IsCancelling ? "Cancelling…"
        : IsRunning ? $"Running · {Math.Min(ImagesDone + 1, ImageCount)}/{ImageCount}" : "Queued";

    /// <summary>The row button's caption: a running batch is cancelled, a queued one removed.</summary>
    public string RemoveText => IsRunning ? "Cancel" : "Remove";

    /// <summary>Removes this batch from its queue (cancels it when it is the running one). Set by the queue on enqueue.</summary>
    public IRelayCommand? RemoveCommand { get; internal set; }

    internal CanvasBatchWork? Work { get; set; }

    internal TaskCompletionSource Completion { get; } = new();
}

/// <summary>
/// The canvas's generation queue: Generate adds a batch, one worker runs them in order, and the status
/// bar reads its count, throughput and ETA from here (issue #598).
/// </summary>
/// <remarks>
/// Batches run one at a time on purpose. <c>DiffusionContextHost</c> keeps a single model resident, so
/// two batches at once would either serialise behind its lock or thrash VRAM. The queue does not know
/// what a batch does; the owner supplies a <see cref="CanvasBatchWork"/> per batch and reports progress
/// back.
/// <para>
/// <b>Not thread-safe: use it from one thread</b>, the UI thread in the app. Its collection is bound to
/// the view and its worker resumes on the caller's context, so there is no lock to trust.
/// </para>
/// </remarks>
public sealed partial class CanvasGenerationQueue : ObservableObject
{
    private readonly Action<string> _trace;
    private readonly Func<TimeSpan> _clock;

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
    private TimeSpan? _samplingStartedAt;
    private int _firstStep;
    private int _step;
    private int _totalSteps;

    // The pace, valid for _paceKey only.
    private string? _paceKey;
    private double _iterationsPerSecond;
    /// <summary>Seconds per step the backend really sampled, the decode spread over them.</summary>
    private double? _secondsPerStep;

    /// <summary>The backend reported no steps, so the pace was measured on a whole image.</summary>
    private bool _paceIsPerImage;

    /// <summary>
    /// How many steps the running batch samples per step it asked for. Image to image on the local
    /// backend samples only steps × denoise of them and reports that smaller total, so an estimate in
    /// asked-for steps would be several times too long. 1 until the batch reports a total.
    /// </summary>
    private double _stepShare = 1;

    /// <param name="trace">Unified Console sink (standing rule: every step is traced).</param>
    /// <param name="clock">Monotonic time source; a test seam for the ETA.</param>
    public CanvasGenerationQueue(Action<string> trace, Func<TimeSpan>? clock = null)
    {
        ArgumentNullException.ThrowIfNull(trace);

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
    public int QueuedCount => Batches.Count(b => !ReferenceEquals(b, _running));

    /// <summary>Removes every queued batch; the running one carries on.</summary>
    public IRelayCommand ClearQueuedCommand { get; }

    /// <summary>
    /// Adds a batch. The returned task completes when the batch is over: finished, cancelled or removed.
    /// </summary>
    public Task Enqueue(CanvasQueuedBatchViewModel batch, CanvasBatchWork work)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(work);

        batch.Work = work;
        batch.RemoveCommand = new RelayCommand(() => Remove(batch), () => !batch.IsCancelling);
        Batches.Add(batch);

        var start = !_pumping;
        _pumping = true;

        Trace(start
            ? $"Batch #{batch.Number} enqueued ({batch.ImageCount} image(s)); the queue was idle, starting it."
            : $"Batch #{batch.Number} enqueued ({batch.ImageCount} image(s)); {QueuedCount} waiting.");

        // Guarded like every notification here: a listener that threw at this point would leave the
        // worker flag set with no worker behind it.
        RaiseReadout();

        if (start)
            _ = PumpAsync();

        return batch.Completion.Task;
    }

    /// <summary>
    /// The worker. It is fire-and-forget, so nothing here may leave <see cref="_pumping"/> set with no
    /// worker behind it: every later Generate would then stage slots that never run. Each batch is run
    /// and tidied by <see cref="RunOneAsync"/>, which does not throw.
    /// </summary>
    private async Task PumpAsync()
    {
        try
        {
            // Re-checked after every batch: completing a batch's task can enqueue the next one inline.
            while (Batches.Count > 0)
                await RunOneAsync(Batches[0]).ConfigureAwait(true);
        }
        finally
        {
            _pumping = false;
        }
    }

    private async Task RunOneAsync(CanvasQueuedBatchViewModel batch)
    {
        var cts = new CancellationTokenSource();
        _runCts = cts;
        _running = batch;

        try
        {
            // Each notification guarded: a listener that threw here would skip the run, and with it
            // the runner's own clean-up of the batch's slots.
            AdoptPaceOf(batch);
            _stepShare = 1;
            ResetImageProgress();
            Guard(() => batch.IsRunning = true);
            Guard(() => IsBusy = true);
            Trace($"Batch #{batch.Number} started.");
            RaiseReadout();

            await batch.Work!.Run(cts.Token).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The runner reports its own failures; this only keeps one broken batch from stranding
            // every batch queued behind it.
            Trace($"Batch #{batch.Number} ended with an unhandled error: {ex.Message}");
        }

        // Cancel nulls the field itself, so a batch that was cancelled no longer owns it.
        var cancelled = !ReferenceEquals(_runCts, cts);
        if (!cancelled)
            _runCts = null;

        cts.Dispose();
        _running = null;

        // Each step on its own: a handler that throws on one notification must not skip the rest, and
        // above all not the completion that whoever awaits this batch is waiting for.
        Guard(() => Batches.Remove(batch));
        ResetImageProgress();
        Trace(cancelled ? $"Batch #{batch.Number} cancelled." : $"Batch #{batch.Number} finished.");

        if (Batches.Count == 0)
        {
            ForgetPace();
            _paceKey = null;
            Guard(() => IsBusy = false);
            Trace("The generation queue is empty.");
        }

        RaiseReadout();

        // Last, so whoever awaits the batch sees the queue's state already settled.
        batch.Completion.TrySetResult();
    }

    /// <summary>
    /// Cancels the running batch; the worker then moves on to the next one. Returns the batch that was
    /// cancelled, or null when nothing was running.
    /// </summary>
    public CanvasQueuedBatchViewModel? CancelRunning()
    {
        var cts = _runCts;
        _runCts = null;
        if (cts is null)
            return null;

        // Read before cancelling: the batch can unwind inside Cancel(), and the worker would then
        // already be on the next one. The owner is told first for the same reason.
        var cancelled = _running;
        if (cancelled is not null)
        {
            Trace($"Cancel requested for batch #{cancelled.Number}.");
            Guard(() => cancelled.IsCancelling = true);
            Guard(() => cancelled.Work?.OnCancelling?.Invoke());
        }

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
            CancelRunning();
            return;
        }

        if (Drop(batch))
            Trace($"Batch #{batch.Number} removed from the queue.");
    }

    private void ClearQueued()
    {
        var queued = Batches.Where(b => !ReferenceEquals(b, _running)).ToList();
        foreach (var batch in queued)
            Drop(batch);

        if (queued.Count > 0)
            Trace($"Cleared the queue: {queued.Count} batch(es) removed.");
    }

    /// <summary>
    /// The owner's slots changed (one was discarded). A queued batch with nothing left to make leaves
    /// the queue, and the count and the ETA are read again.
    /// </summary>
    public void SlotsChanged()
    {
        var empty = Batches
            .Where(b => !ReferenceEquals(b, _running) && b.Work?.PendingImages is { } pending && pending() == 0)
            .ToList();

        foreach (var batch in empty)
        {
            if (Drop(batch))
                Trace($"Batch #{batch.Number} left the queue: every one of its slots was discarded.");
        }

        RaiseReadout();
    }

    /// <summary>
    /// Re-reads the ETA against the clock. The image in flight counts down by elapsed time, which no
    /// progress event announces; the view calls this once a second while the queue is busy.
    /// </summary>
    public void Tick()
    {
        if (_running is not null)
            Raise(nameof(EtaText));
    }

    /// <summary>Cancels the running batch and drops every queued one. For the owner's teardown.</summary>
    public void Shutdown()
    {
        ClearQueued();
        CancelRunning();
    }

    private bool Drop(CanvasQueuedBatchViewModel batch)
    {
        if (ReferenceEquals(batch, _running) || !Batches.Remove(batch))
            return false;

        RaiseReadout();
        Guard(() => batch.Work?.OnDropped?.Invoke());
        batch.Completion.TrySetResult();
        return true;
    }

    private void Trace(string message) => Guard(() => _trace(message));

    /// <summary>Runs a notification that must not be able to break the queue's own bookkeeping.</summary>
    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"CanvasGenerationQueue: a notification handler threw: {ex}");
        }
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
        if (_imageInFlight && _samplingStartedAt is null)
        {
            // Everything before the first step is loading and encoding. A cold start loads the model
            // for a minute; counting that into the pace would make every later image look as slow.
            _samplingStartedAt = _clock();
            _firstStep = step;
        }

        _step = step;
        _totalSteps = totalSteps;
        if (totalSteps > 0 && _running is { } running)
            _stepShare = (double)totalSteps / running.Steps;
        if (iterationsPerSecond > 0 && double.IsFinite(iterationsPerSecond))
            _iterationsPerSecond = iterationsPerSecond;
        RaiseReadout();
    }

    /// <summary>The image in flight is over. Only a successful one sets the measured pace.</summary>
    public void ImageFinished(bool succeeded)
    {
        if (_imageInFlight && succeeded && _running is { } batch)
        {
            var now = _clock();
            if (_samplingStartedAt is { } samplingStarted)
            {
                // From the first reported step to the end: sampling plus the decode, without the load.
                // Counted in the steps the backend reported, which is what _stepShare converts to.
                // The first report comes after its step has run, so that step is not in the elapsed
                // time. With 20 steps that is noise; with a 4-step model it is a quarter of the image,
                // and with one step all of it. It is added at the reported rate; without a rate the
                // measured time is spread over the steps it covers.
                var elapsed = (now - samplingStarted).TotalSeconds;
                var total = Math.Max(1, _totalSteps);
                _secondsPerStep = _iterationsPerSecond > 0
                    ? (elapsed + _firstStep / _iterationsPerSecond) / total
                    : elapsed / Math.Max(1, total - _firstStep);
                _paceIsPerImage = false;
            }
            else
            {
                // A backend that reports no steps: the whole image is all there is to measure. The
                // latest image replaces the one before, so a cold first image stops counting after it.
                _secondsPerStep = (now - _imageStartedAt).TotalSeconds / batch.Steps;
                _paceIsPerImage = true;
            }
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
    /// Forgets the image in flight, but not the pace. The pace outlives an image: the next one has not
    /// reported a step yet, and showing nothing until it does would make the readout blink.
    /// </summary>
    private void ResetImageProgress()
    {
        _imageInFlight = false;
        _samplingStartedAt = null;
        _firstStep = 0;
        _step = 0;
        _totalSteps = 0;
    }

    /// <summary>
    /// Keeps the pace for a batch that runs like the one before it and drops it otherwise. Another
    /// model, size or backend runs at another speed, and a stale rate is worse than none: the bar would
    /// show it as this batch's.
    /// </summary>
    private void AdoptPaceOf(CanvasQueuedBatchViewModel batch)
    {
        if (!string.Equals(_paceKey, batch.PaceKey, StringComparison.Ordinal))
            ForgetPace();

        _paceKey = batch.PaceKey;
    }

    private void ForgetPace()
    {
        _iterationsPerSecond = 0;
        _secondsPerStep = null;
        _paceIsPerImage = false;
    }

    // ────────────────────────────── Readout ──────────────────────────────

    /// <summary>Images of a waiting batch that will still run.</summary>
    private static int PendingOf(CanvasQueuedBatchViewModel batch) =>
        Math.Max(0, batch.Work?.PendingImages?.Invoke() ?? batch.ImageCount);

    /// <summary>Images of the running batch that have not started.</summary>
    private int NotStartedOfRunning(CanvasQueuedBatchViewModel running) =>
        Math.Max(0, running.Work?.PendingImages?.Invoke()
                    ?? running.ImageCount - running.ImagesDone - (_imageInFlight ? 1 : 0));

    /// <summary>e.g. <c>Image 2/4 · 2 batches queued (8 images)</c>; empty while idle.</summary>
    public string QueueText
    {
        get
        {
            if (_running is not { } batch)
                return string.Empty;

            var text = $"Image {Math.Min(batch.ImagesDone + 1, batch.ImageCount)}/{batch.ImageCount}";
            var queued = Batches.Where(b => !ReferenceEquals(b, batch)).ToList();
            if (queued.Count == 0)
                return text;

            var images = queued.Sum(PendingOf);
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
            if (_running is not { } running)
                return string.Empty;

            if (_iterationsPerSecond >= 1)
                return string.Create(CultureInfo.InvariantCulture, $"{_iterationsPerSecond:0.0} it/s");
            if (_iterationsPerSecond > 0)
                return string.Create(CultureInfo.InvariantCulture, $"{1 / _iterationsPerSecond:0.0} s/it");
            // From the per-step pace and this batch's steps, so it agrees with the ETA when the pace was
            // measured on a batch with another step count.
            if (_paceIsPerImage && _secondsPerStep is { } perStep)
                return string.Create(CultureInfo.InvariantCulture, $"{perStep * running.Steps:0.#} s/image");

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
    /// queued batch with more steps weighs more. Waiting batches are estimated at the running batch's
    /// pace and its share of sampled steps, which are the only ones known; a batch that runs differently
    /// corrects it once it starts.
    /// </summary>
    internal TimeSpan? EstimateRemaining()
    {
        if (_running is not { } running)
            return null;

        // The measured time per step carries the decode a bare step rate leaves out.
        double? ratePerStep = _iterationsPerSecond > 0 ? 1 / _iterationsPerSecond : null;
        var perStep = _secondsPerStep ?? ratePerStep;
        if (perStep is null)
            return null;

        double stepsAhead = (double)NotStartedOfRunning(running) * running.Steps;
        foreach (var batch in Batches)
        {
            if (!ReferenceEquals(batch, running))
                stepsAhead += (double)PendingOf(batch) * batch.Steps;
        }

        // Asked-for steps into the steps the backend samples, the unit the pace is in.
        stepsAhead *= _stepShare;

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
                current = Math.Max(0, running.Steps * _stepShare * perStep.Value - elapsed);
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

    /// <summary>Each notification on its own guard, so one throwing listener cannot skip the rest or its caller.</summary>
    private void RaiseReadout()
    {
        Raise(nameof(QueueText));
        Raise(nameof(ThroughputText));
        Raise(nameof(EtaText));
        Raise(nameof(QueuedCount));
        Guard(ClearQueuedCommand.NotifyCanExecuteChanged);
    }

    private void Raise(string propertyName) => Guard(() => OnPropertyChanged(propertyName));
}
