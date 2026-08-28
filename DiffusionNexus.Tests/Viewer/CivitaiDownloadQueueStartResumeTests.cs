using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.Download;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Start/Resume interaction bugs in <see cref="CivitaiDownloadQueue"/>:
/// <list type="number">
/// <item>StartAllAsync used to begin with <c>_runCts?.Cancel()</c>, killing every in-flight
/// job — including a per-tile Retry the user just resumed — the moment Start was clicked.</item>
/// <item>StartAllAsync only picked up <c>Queued</c> jobs, so Cancelled jobs never re-ran on
/// Start even though <c>AbortAllActive</c>'s contract says they can be "re-run … by hitting
/// Start again". And their per-job CTS was still fired from the Cancel click, so even if
/// picked up they would instantly land back at Cancelled without a <c>ResetForRetry</c>.</item>
/// </list>
/// </summary>
public sealed class CivitaiDownloadQueueStartResumeTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-queue-start-resume-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>Completes instantly with a successful outcome; counts calls.</summary>
    private sealed class InstantDownloader : ICivitaiModelDownloader
    {
        public int CallCount;

        public Task<DownloadOutcome> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            return Task.FromResult(new DownloadOutcome(
                DownloadStatus.Completed, Path.Combine(request.TargetDirectory, "final.safetensors"), 1, false, null));
        }
    }

    /// <summary>
    /// Blocks each download until released, honoring the cancellation token the way the real
    /// downloader does (a cancelled transfer surfaces as a Cancelled outcome, not a throw).
    /// Records how many in-flight transfers saw their token fire — that count is the
    /// dispatcher-free evidence the in-flight-cancellation test asserts on, because this
    /// headless host cannot reliably drain terminal-state posts made from pool threads.
    /// </summary>
    private sealed class BlockingDownloader : ICivitaiModelDownloader
    {
        private readonly SemaphoreSlim _release = new(0);
        public int CallCount;
        public int CancelledCount;
        /// <summary>Calls per version id — how the duplicate-run test spots a job started twice.</summary>
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, int> CallsByVersion = new();
        public readonly TaskCompletionSource FirstCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release(int count) => _release.Release(count);

        public async Task<DownloadOutcome> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref CallCount);
            CallsByVersion.AddOrUpdate(request.Version.Id, 1, (_, n) => n + 1);
            FirstCallStarted.TrySetResult();
            try
            {
                await _release.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref CancelledCount);
                return new DownloadOutcome(DownloadStatus.Cancelled, null, null, false, "cancelled");
            }
            return new DownloadOutcome(
                DownloadStatus.Completed, Path.Combine(request.TargetDirectory, "final.safetensors"), 1, false, null);
        }
    }

    private CivitaiDownloadQueue Queue(ICivitaiModelDownloader? downloader) => new(
        downloader, logger: null, civitaiClient: null, destination: null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    private CivitaiDownloadJob NewJob(int versionId = 1) => new()
    {
        ModelId = 100,
        VersionId = versionId,
        ModelName = "Test Model",
        VersionName = $"v{versionId}",
        BaseModel = "SDXL",
        Category = "LORA",
        FileName = "test.safetensors",
        DownloadUrl = "https://civitai.test/api/download/models/1",
        CustomTargetDirectory = _tempDir,
        CivitaiVersion = new CivitaiModelVersion { Id = versionId, Name = $"v{versionId}" },
        Status = JobStatus.Queued,
    };

    [Fact]
    public async Task StartAllAsync_DoesNotCancelAnInFlightRetriedJob()
    {
        var downloader = new BlockingDownloader();
        var queue = Queue(downloader);

        // A job the user cancelled earlier, then resumed via the per-tile Retry button.
        var resumed = NewJob(versionId: 1);
        resumed.CancelByUser();
        resumed.Status = JobStatus.Cancelled;
        queue.Jobs.Add(resumed);
        var retryTask = queue.RetryJobAsync(resumed);
        await downloader.FirstCallStarted.Task;

        // While the resumed download is in flight, the user queues another model
        // and hits Start.
        var added = NewJob(versionId: 2);
        queue.Jobs.Add(added);
        var startTask = queue.StartAllAsync();

        downloader.Release(2);
        await Task.WhenAll(retryTask, startTask);

        downloader.CancelledCount.Should().Be(0,
            "hitting Start must coalesce with the in-flight retry, not fire its cancellation token");
        downloader.CallCount.Should().Be(2,
            "the retried job downloads once and the newly queued job downloads once");
    }

    [Fact]
    public async Task StartAllAsync_ReRunsCancelledJobs_WithAFreshToken()
    {
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);

        var job = NewJob();
        job.CancelByUser(); // fires the per-job CTS, exactly like the tile's Cancel button
        job.Status = JobStatus.Cancelled;
        job.StatusMessage = "Cancelled";
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed,
            "AbortAllActive's contract says cancelled jobs can be re-run by hitting Start again");
        downloader.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAllAsync_AfterClearAll_RunsNewJobsOnAFreshToken()
    {
        // ClearAll cancels the run-wide CTS. A later Start must not reuse that fired
        // token, or every new job instantly cancels.
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);
        queue.Jobs.Add(NewJob(versionId: 1));
        queue.ClearAll();

        var job = NewJob(versionId: 2);
        queue.Jobs.Add(job);
        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task StartAllAsync_DoesNotStartAJobThatIsAlreadyWaitingForAWorkerSlot()
    {
        // The queue's worker pool is two wide, so a third job sits inside RunGatedAsync's
        // _gate.WaitAsync while its Status is still Queued — Downloading is only set after
        // the slot is taken. A second Start therefore re-picks it as "pending" and schedules
        // a SECOND concurrent runner for the same job: two transfers, two coordinator
        // entries, a collision-renamed duplicate on disk.
        var downloader = new BlockingDownloader();
        var queue = Queue(downloader);
        queue.Jobs.Add(NewJob(versionId: 1));
        queue.Jobs.Add(NewJob(versionId: 2));
        queue.Jobs.Add(NewJob(versionId: 3));

        var firstStart = queue.StartAllAsync();
        await downloader.FirstCallStarted.Task;
        downloader.CallCount.Should().Be(2, "the pool is two wide; version 3 is queued behind the gate");

        // User hits Start again (e.g. after adding nothing, or from the re-run path).
        var secondStart = queue.StartAllAsync();

        downloader.Release(8);
        await Task.WhenAll(firstStart, secondStart);
        Dispatcher.UIThread.RunJobs();

        downloader.CallsByVersion.GetValueOrDefault(3).Should().Be(1,
            "the gate-waiting job must not be scheduled a second time by a coalescing Start");
    }

    [Fact]
    public async Task RetryJobAsync_DoesNotStartAJobAlreadyWaitingForAWorkerSlot()
    {
        // Same hazard as the Start/Start case, through the per-tile Retry button: the job is
        // parked in _gate.WaitAsync and still reads Queued, so an unguarded Retry would reset
        // its state under the live runner and schedule a second transfer.
        var downloader = new BlockingDownloader();
        var queue = Queue(downloader);
        queue.Jobs.Add(NewJob(versionId: 1));
        queue.Jobs.Add(NewJob(versionId: 2));
        var waiting = NewJob(versionId: 3);
        queue.Jobs.Add(waiting);

        var firstStart = queue.StartAllAsync();
        await downloader.FirstCallStarted.Task;

        await queue.RetryJobAsync(waiting);

        downloader.Release(8);
        await firstStart;
        Dispatcher.UIThread.RunJobs();

        downloader.CallsByVersion.GetValueOrDefault(3).Should().Be(1,
            "Retry on a job that is already scheduled must not run it a second time");
    }

    [Fact]
    public async Task StartAllAsync_RehydratesARestoredJobsVersion_WithTheApiKey()
    {
        // A job survives a restart with no CivitaiVersion (it isn't persisted) — the queue
        // must re-fetch it before downloading, and do so with the API key so the call gets
        // the friendlier quota and can see gated versions.
        var downloader = new InstantDownloader();
        var civitaiClient = new Mock<ICivitaiClient>();
        civitaiClient
            .Setup(c => c.GetModelVersionAsync(600, "key-a", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 600, Name = "v600" });
        var apiKeyProvider = new Mock<ICivitaiApiKeyProvider>();
        apiKeyProvider.Setup(p => p.GetApiKeyAsync(It.IsAny<CancellationToken>())).ReturnsAsync("key-a");

        var queue = new CivitaiDownloadQueue(
            downloader, logger: null, civitaiClient: civitaiClient.Object, destination: null,
            persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"),
            apiKeyProvider: apiKeyProvider.Object);

        var restored = NewJob(versionId: 600);
        restored.CivitaiVersion = null; // as it comes back from JSON restore
        queue.Jobs.Add(restored);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        civitaiClient.Verify(
            c => c.GetModelVersionAsync(600, "key-a", It.IsAny<CancellationToken>()), Times.Once);
        restored.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public async Task StartAllAsync_WithoutADownloader_FailsCancelledJobsToo()
    {
        // Start re-runs Cancelled jobs, so the no-service branch has to report on exactly the
        // set it would have run. Leaving them Cancelled says "you cancelled this", which is a
        // lie about why nothing happened.
        var queue = Queue(null);
        var queued = NewJob(versionId: 1);
        var cancelled = NewJob(versionId: 2);
        cancelled.CancelByUser();
        cancelled.Status = JobStatus.Cancelled;
        queue.Jobs.Add(queued);
        queue.Jobs.Add(cancelled);

        await queue.StartAllAsync();

        queued.Status.Should().Be(JobStatus.Failed);
        cancelled.Status.Should().Be(JobStatus.Failed);
        cancelled.StatusMessage.Should().Be("Download service unavailable.");
    }
}
