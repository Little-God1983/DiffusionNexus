using System.Runtime.CompilerServices;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.Models;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// A backend whose <c>GenerateAsync</c> is a real async iterator, so the view model's
/// <c>await foreach</c>, its cancellation and its progress mapping are all exercised for real. Moq
/// cannot conveniently return an async iterator, so this is hand-rolled following the repo's
/// FakeDownloader convention.
/// </summary>
/// <remarks>
/// This and <see cref="TempCanvasFile"/> began as private nested classes inside
/// <c>DiffusionCanvasBatchTests</c>. They were lifted out when the generate panel (issue #518 region B)
/// added a second test file needing the same fakes — nesting them meant any new canvas test could only
/// reuse them by growing the batch-test file indefinitely.
/// </remarks>
internal sealed class FakeDiffusionBackend : IDiffusionBackend
{
    /// <summary>The one model key <see cref="Catalog"/> resolves. Tests select it through the dropdown.</summary>
    public const string ModelKey = "fake-model";

    private int _concurrent;

    public string DisplayName => "Fake backend";

    public int DimensionAlignment { get; init; } = 64;

    public bool IsAvailable { get; init; } = true;

    public List<string> Missing { get; } = [];

    /// <summary>
    /// What this fake claims to honour. Defaults to everything, so tests that do not care about gating
    /// are unaffected; a gating test supplies a restricted set.
    /// </summary>
    public BackendCapabilities Capabilities { get; init; } = BackendCapabilities.All;

    /// <summary>Run index (1-based) that should throw, simulating a backend blowing up.</summary>
    public int? FailOnRun { get; init; }

    /// <summary>Run index (1-based) that should report a failure message with no result.</summary>
    public int? ReportFailureMessageOnRun { get; init; }

    /// <summary>Called at the start of each run with its 1-based index.</summary>
    public Action<int>? BeforeRun { get; set; }

    public int RunCount { get; private set; }

    public int MaxConcurrentRuns { get; private set; }

    public DiffusionRequest? LastRequest { get; private set; }

    /// <summary>Every request the backend was handed, in order — a batch produces one per candidate.</summary>
    public List<DiffusionRequest> Requests { get; } = [];

    public List<long?> RequestedSeeds { get; } = [];

    public IModelCatalog Catalog => new FakeCatalog(DimensionAlignment);

    public IReadOnlyList<string> MissingRequirements => Missing;

    public IReadOnlyList<string> Warnings => [];

    /// <summary>Runs at the top of the availability probe, so a test can cancel during pre-flight.</summary>
    public Action? BeforeAvailabilityCheck { get; set; }

    /// <summary>True when the pre-flight token could actually carry a cancellation.</summary>
    public bool AvailabilityTokenWasCancellable { get; private set; }

    /// <summary>
    /// The init image's bytes read at the moment the backend was called. Captured because the view
    /// model deletes the scratch file once the batch ends, so a later read would always fail.
    /// </summary>
    public byte[]? InitImageBytesAtCallTime { get; private set; }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        AvailabilityTokenWasCancellable = ct.CanBeCanceled;
        BeforeAvailabilityCheck?.Invoke();
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(IsAvailable);
    }

    public async IAsyncEnumerable<DiffusionStreamItem> GenerateAsync(
        DiffusionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var run = ++RunCount;
        LastRequest = request;
        Requests.Add(request);
        RequestedSeeds.Add(request.Seed);

        if (request.InitImage is { } init && File.Exists(init.FilePath))
            InitImageBytesAtCallTime = File.ReadAllBytes(init.FilePath);

        MaxConcurrentRuns = Math.Max(MaxConcurrentRuns, ++_concurrent);
        try
        {
            BeforeRun?.Invoke(run);
            cancellationToken.ThrowIfCancellationRequested();

            yield return new DiffusionStreamItem(new DiffusionProgress
            {
                Phase = DiffusionPhase.Loading,
                Message = "Loading…",
            });

            if (FailOnRun == run)
                throw new InvalidOperationException("the fake backend exploded");

            yield return new DiffusionStreamItem(new DiffusionProgress
            {
                Phase = DiffusionPhase.Sampling,
                Step = 1,
                TotalSteps = 2,
            });

            cancellationToken.ThrowIfCancellationRequested();

            if (ReportFailureMessageOnRun == run)
            {
                yield return new DiffusionStreamItem(new DiffusionProgress
                {
                    Phase = DiffusionPhase.Completed,
                    Message = "the engine said no",
                });
                yield break;
            }

            yield return new DiffusionStreamItem(
                new DiffusionProgress { Phase = DiffusionPhase.Completed, Step = 2, TotalSteps = 2 },
                new DiffusionResult(
                    // Not a decodable PNG: the view model must survive a decode failure, and there
                    // is no Avalonia platform here to decode a real one anyway.
                    [1, 2, 3, 4],
                    request.Width,
                    request.Height,
                    request.Seed ?? 42,
                    TimeSpan.FromSeconds(1)));
        }
        finally
        {
            _concurrent--;
        }
    }

    private sealed class FakeCatalog(int alignment) : IModelCatalog
    {
        private readonly ModelDescriptor _descriptor = new()
        {
            Key = ModelKey,
            DisplayName = "Fake Model",
            Kind = ModelKind.Krea2,
            DimensionAlignment = alignment,
            DefaultWidth = 1024,
            DefaultHeight = 1024,
        };

        public IReadOnlyList<ModelDescriptor> ListAvailable() => [_descriptor];

        public ModelDescriptor? TryGet(string key) =>
            string.Equals(key, ModelKey, StringComparison.Ordinal) ? _descriptor : null;
    }
}

/// <summary>
/// A real PNG on disk plus the accepted raster that points at it. The compositor reads rasters back
/// from their saved file, so an image-to-image test needs genuine bytes rather than a stand-in.
/// </summary>
internal sealed class TempCanvasFile : IDisposable
{
    public TempCanvasFile(int width, int height, SKColor colour)
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"dn-canvas-test-{Guid.NewGuid():N}.png");

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(colour);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path, data.ToArray());
    }

    public string Path { get; }

    public GenerationFrameViewModel AsFrame(double x, double y, int width, int height) => new()
    {
        CanvasX = x,
        CanvasY = y,
        Width = width,
        Height = height,
        ImagePath = Path,
        State = GenerationFrameState.Completed,
    };

    public void Dispose()
    {
        try
        {
            File.Delete(Path);
        }
        catch (IOException)
        {
            // Best-effort test cleanup.
        }
    }
}
