using Avalonia.Threading;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.Download;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Task 7 migration: <see cref="CivitaiDownloadQueue"/> no longer drives its own collision
/// policy / coordinator wrap / SHA256 verification — it hands the job to
/// <see cref="ICivitaiModelDownloader"/> (the one Civitai download path, spec §4.4) and maps the
/// returned <see cref="DownloadOutcome"/> onto <see cref="JobStatus"/>. Covers that mapping table
/// plus the D3 guarantee that the queue never wraps the call in an <c>IDownloadCoordinator</c>
/// enqueue itself (the downloader owns that).
/// <para>
/// The terminal-state assignment is posted through <c>Dispatcher.UIThread.Post</c> (review fix:
/// same queue as the progress adapter, so a late progress update can never land after and
/// clobber it) — this test host never pumps that queue on its own, so every test that inspects
/// job state after the awaited call drains it once with <c>Dispatcher.UIThread.RunJobs()</c>.
/// </para>
/// </summary>
public sealed class CivitaiDownloadQueueOutcomeMappingTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-queue-outcome-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Records every call so a test can assert the queue invokes the downloader exactly once
    /// per job (D3: no coordinator double-wrap) and inspect what it was asked to download.
    /// </summary>
    private sealed class FakeDownloader : ICivitaiModelDownloader
    {
        public int CallCount { get; private set; }
        public DownloadRequest? LastRequest { get; private set; }
        public Func<DownloadRequest, DownloadOutcome> Respond { get; set; } =
            _ => new DownloadOutcome(DownloadStatus.Completed, "C:\\x\\model.safetensors", 1, false, null);

        /// <summary>Opt-in: some tests need to observe the progress adapter wiring.</summary>
        public bool ReportProgress { get; set; }

        public Task<DownloadOutcome> DownloadAsync(
            DownloadRequest request, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
        {
            CallCount++;
            LastRequest = request;
            if (ReportProgress) progress?.Report(new DownloadProgress(50, "Downloading"));
            return Task.FromResult(Respond(request));
        }
    }

    private static CivitaiModelVersion Version(int id) => new() { Id = id, Name = $"v{id}" };

    private CivitaiDownloadQueue Queue(FakeDownloader downloader) => new(
        downloader, logger: null, civitaiClient: null, destination: null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    /// <summary>
    /// A job that skips destination resolution (CustomTargetDirectory) and version rehydration
    /// (CivitaiVersion pre-set) — isolates the test to the outcome-mapping step under test.
    /// </summary>
    private CivitaiDownloadJob NewJob(int versionId = 1, string? expectedSha256 = null) => new()
    {
        ModelId = 100,
        VersionId = versionId,
        ModelName = "Test Model",
        VersionName = $"v{versionId}",
        BaseModel = "SDXL",
        Category = "LORA",
        FileName = "test.safetensors",
        DownloadUrl = "https://civitai.test/api/download/models/1",
        ExpectedSha256 = expectedSha256,
        CustomTargetDirectory = _tempDir,
        CivitaiVersion = Version(versionId),
        Status = JobStatus.Queued,
    };

    [Fact]
    public async Task Completed_MapsToCompletedDone_AndAdoptsExpectedShaAsActual()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.Completed, "final.safetensors", 1, false, null)
        };
        var queue = Queue(downloader);
        var job = NewJob(expectedSha256: "AAAA1111BBBB2222");
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed);
        job.StatusMessage.Should().Be("Done");
        job.TargetPath.Should().Be("final.safetensors");
        job.ActualSha256.Should().Be("AAAA1111BBBB2222", "the downloader already verified the hash");
        job.ProgressPercent.Should().Be(100, "the throttled progress adapter's last report may sit below 100");
    }

    [Fact]
    public async Task CompletedMetadataIncomplete_MapsToCompletedWithNoMetadataMessage()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.CompletedMetadataIncomplete, "final.safetensors", 1, false, null)
        };
        var queue = Queue(downloader);
        var job = NewJob(expectedSha256: "AAAA1111");
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed);
        job.StatusMessage.Should().Be("Done — no metadata");
        job.ActualSha256.Should().Be("AAAA1111");
        job.ProgressPercent.Should().Be(100);
    }

    [Fact]
    public async Task ReusedExisting_MapsToCompletedAlreadyDownloaded_AndDoesNotStampActualSha()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.ReusedExisting, "existing.safetensors", 1, false, null)
        };
        var queue = Queue(downloader);
        var job = NewJob(expectedSha256: "AAAA1111");
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed);
        job.StatusMessage.Should().Be("Already downloaded");
        job.ActualSha256.Should().BeNull("no fresh transfer happened, so nothing was verified this run");
        job.ProgressPercent.Should().Be(100,
            "no transfer means the throttled progress adapter never reported anything — the tile must not sit at 0% next to 'Already downloaded'");
    }

    [Fact]
    public async Task HashMismatch_MapsToFailed_WithHashMessage()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.HashMismatch, "bad.safetensors", null, false, "hash mismatch")
        };
        var queue = Queue(downloader);
        var job = NewJob(expectedSha256: "AAAA1111BBBB2222");
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Failed);
        job.StatusMessage.Should().Contain("Hash mismatch");
        job.ActualSha256.Should().BeNull("a mismatched transfer must not be adopted as verified");
    }

    [Fact]
    public async Task Cancelled_MapsToCancelled()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.Cancelled, null, null, false, "cancelled")
        };
        var queue = Queue(downloader);
        var job = NewJob();
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Cancelled);
        job.StatusMessage.Should().Be("Cancelled");
    }

    [Fact]
    public async Task JobCancelledByUser_MapsToCancelled_EvenWhenOutcomeReportsFailed()
    {
        // Defensive OR: a race between the user's Cancel click and the downloader's own
        // status must still read as "Cancelled" on the tile, not "Failed".
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.Failed, null, null, false, "transfer failed")
        };
        var queue = Queue(downloader);
        var job = NewJob();
        job.CancelByUser();
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Cancelled);
        job.StatusMessage.Should().Be("Cancelled");
    }

    [Fact]
    public async Task Failed_KeepsConnectingFixup_ReplacedByOutcomeError()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.Failed, null, null, false, "no download URL")
        };
        var queue = Queue(downloader);
        var job = NewJob();
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Failed);
        job.StatusMessage.Should().Be("no download URL",
            "the 'Connecting...' placeholder must be replaced by the downloader's error, not left showing");
    }

    [Fact]
    public async Task RunJobAsync_CallsDownloaderExactlyOnce_NoCoordinatorDoubleWrap()
    {
        // D3: the queue's own coordinator wrap is gone — the downloader owns the coordinator
        // enqueue. If the queue still wrapped this call in one itself (the old bug pattern),
        // the two competing gates would deadlock or the transport would run twice.
        var downloader = new FakeDownloader();
        var queue = Queue(downloader);
        var job = NewJob();
        queue.Jobs.Add(job);

        await queue.StartAllAsync();
        Dispatcher.UIThread.RunJobs();

        downloader.CallCount.Should().Be(1);
        downloader.LastRequest.Should().NotBeNull();
        downloader.LastRequest!.Trigger.Should().Be(DownloadTrigger.BrowseQueue);
        downloader.LastRequest!.TargetDirectory.Should().Be(_tempDir);
        downloader.LastRequest!.FileNameOverride.Should().Be("test.safetensors");
    }

    [Fact]
    public async Task RetryJobAsync_AlsoRoutesThroughTheDownloader_ExactlyOnce()
    {
        var downloader = new FakeDownloader
        {
            Respond = _ => new DownloadOutcome(DownloadStatus.Completed, "final.safetensors", 1, false, null)
        };
        var queue = Queue(downloader);
        var job = NewJob();
        job.Status = JobStatus.Failed;
        job.StatusMessage = "Failed";
        queue.Jobs.Add(job);

        await queue.RetryJobAsync(job);
        Dispatcher.UIThread.RunJobs();

        job.Status.Should().Be(JobStatus.Completed);
        downloader.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task StartAllAsync_WithNoDownloader_FailsQueuedJobs()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "q-no-downloader.json"));
        var job = NewJob();
        queue.Jobs.Add(job);

        await queue.StartAllAsync();

        job.Status.Should().Be(JobStatus.Failed);
        job.StatusMessage.Should().Be("Download service unavailable.");
    }
}
