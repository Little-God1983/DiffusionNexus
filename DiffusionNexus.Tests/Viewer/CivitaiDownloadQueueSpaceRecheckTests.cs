using Avalonia.Threading;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Utilities;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.Download;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #379: the queue's disk-space warning is only recomputed when the queue,
/// a job or the destination changes — nothing watches the drives themselves. Freeing
/// space outside the app therefore left a stale warning on screen with Start
/// disabled, and no way to re-run the check. Covers the on-demand recheck and the
/// gate's job set, margin, unreachable/unknown split and Start-time re-verification.
/// </summary>
public sealed class CivitaiDownloadQueueSpaceRecheckTests : IDisposable
{
    private const long Gb = 1L << 30;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-queue-space-recheck").FullName;

    public void Dispose()
    {
        // Unlink the fake mount point with a NON-recursive delete before the temp dir goes, so
        // the teardown can only ever remove the reparse point and never the drive behind it.
        try { Directory.Delete(Path.Combine(_tempDir, MountPointProbe.LinkName)); } catch { /* not every test makes one */ }
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    /// <summary>A real reading of <paramref name="freeBytes"/> free.</summary>
    private static FreeSpaceResult Free(long freeBytes) => new(FreeSpaceKind.Known, freeBytes);

    /// <summary>A destination that will not say how much room it has — fail open.</summary>
    private static FreeSpaceResult Unknown => new(FreeSpaceKind.Unknown, 0);

    /// <summary>A destination with no volume behind it — block.</summary>
    private static FreeSpaceResult Unreachable => new(FreeSpaceKind.Unreachable, 0);

    /// <summary>Completes instantly; counts calls so a refused Start is provable.</summary>
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

    private CivitaiDownloadQueue Queue(ICivitaiModelDownloader? downloader = null) => new(
        downloader, logger: null, civitaiClient: null, destination: null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    /// <summary>A queued job whose files land on the temp dir's drive unless told otherwise.</summary>
    private CivitaiDownloadJob Job(long sizeBytes, string? targetDir = null, JobStatus status = JobStatus.Queued) => new()
    {
        ModelId = 100,
        VersionId = 1,
        ModelName = "Model",
        VersionName = "v1",
        SizeBytes = sizeBytes,
        DownloadUrl = "https://civitai.test/api/download/models/1",
        CivitaiVersion = new CivitaiModelVersion { Id = 1, Name = "v1" },
        CustomTargetDirectory = targetDir ?? _tempDir,
        Status = status,
    };

    private string Root => Path.GetPathRoot(_tempDir)!;

    [Fact]
    public async Task RecheckSpace_ClearsTheWarning_WhenTheDriveGainedRoomOutsideTheApp()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb));

        queue.HasSpaceWarning.Should().BeTrue("5 GB of downloads do not fit in 1 GB of free space");

        // The user goes and deletes files. Nothing in the app observes that.
        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        queue.HasSpaceWarning.Should().BeTrue("nothing recomputes until the user asks");

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeFalse("the recheck must re-read the drive and clear the stale warning");
        queue.SpaceWarning.Should().BeNull();
    }

    [Fact]
    public async Task RecheckSpace_RaisesTheWarning_WhenTheDriveFilledUpOutsideTheApp()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        queue.Jobs.Add(Job(5 * Gb));

        queue.HasSpaceWarning.Should().BeFalse("5 GB fits in 100 GB");

        // Something else on the machine ate the drive.
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeTrue("the recheck must catch a drive that filled up after queueing");
        queue.SpaceWarning.Should().Contain(Root);
    }

    [Fact]
    public async Task RecheckSpaceCommand_RefreshesTheQueueWarning()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue,
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_tempDir, "waitlist.json")), null);

        queue.HasSpaceWarning.Should().BeTrue("5 GB of downloads do not fit in 1 GB of free space");

        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        await vm.RecheckSpaceCommand.ExecuteAsync(null);

        queue.HasSpaceWarning.Should().BeFalse("the queue panel's recheck button must re-run the check");
    }

    [Fact]
    public async Task RecheckSpace_CountsCancelledJobs_BecauseStartReRunsThem()
    {
        // StartAllAsync runs `Queued or Cancelled` — AbortAllActive's documented contract.
        // A check that only counts Queued clears the warning after an Abort and re-arms
        // Start for bytes it never measured.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(10 * Gb);
        queue.Jobs.Add(Job(40 * Gb, status: JobStatus.Cancelled));

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeTrue("cancelled jobs re-run on Start, so their bytes must be checked");
    }

    [Fact]
    public async Task RecheckSpace_KeepsASafetyMargin_SoTheQueueCannotFillTheVolumeToZero()
    {
        // The partial (.download), the .civitai.json/.preview.png sidecars and the OS all
        // need room the exact file size does not account for. Siblings keep 256 MB
        // (CaptioningModelManager) and 1 GB (LoraSorterViewModel).
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(5 * Gb);
        queue.Jobs.Add(Job(5 * Gb));

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeTrue("a queue that exactly fills the volume must not pass the check");
    }

    [Fact]
    public async Task RecheckSpace_ReportsAnUnreachableDrive_WithoutArmingTheQueueWideGate()
    {
        // It must be stated — failing open silently is what produced LoraSorter's "0 sorted,
        // 412 failed" — but it must not gate Start: the queue is restored across restarts, so
        // one leftover job bound for an unplugged stick would leave Start permanently dead with
        // no way back.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Unreachable;
        queue.Jobs.Add(Job(5 * Gb, targetDir: "Z:/models"));

        await queue.RecheckSpaceAsync();

        queue.SpaceNote.Should().Contain("reach", "the user has to be told which destination is dead");
        queue.HasSpaceWarning.Should().BeFalse("one dead root must not block jobs bound for healthy drives");
    }

    [Fact]
    public async Task StartAllAsync_FailsOnlyTheJobOnTheUnreachableDrive()
    {
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);
        queue.FreeSpaceProbe = (root, _) =>
            root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? Unreachable : Free(100 * Gb);
        var dead = Job(5 * Gb, targetDir: "Z:/models");
        queue.Jobs.Add(dead);
        queue.Jobs.Add(Job(5 * Gb));

        await queue.StartAllAsync();

        dead.Status.Should().Be(JobStatus.Failed);
        dead.StatusMessage.Should().Contain("not reachable");
        downloader.CallCount.Should().Be(1, "the healthy job must still run");
    }

    [Fact]
    public async Task RetryJobAsync_RefusesTheJob_WhenItsDriveFilledUp()
    {
        // The per-tile Retry is a documented re-run path that never passes the batch gate, so
        // without a commit-time check the user can push every download through it while the
        // red banner is up.
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        var job = Job(5 * Gb, status: JobStatus.Cancelled);
        queue.Jobs.Add(job);

        await queue.RetryJobAsync(job);

        downloader.CallCount.Should().Be(0, "there is no room for this job");
        job.Status.Should().Be(JobStatus.Failed);
        job.StatusMessage.Should().Contain("free space");
    }

    [Fact]
    public async Task StartAllAsync_StartsOnAFreshVerdict_EvenWhenTheStoredWarningIsStale()
    {
        // The stored verdict is exactly what #379 is about: it can be a warning for room that
        // has since been freed. Gating on it (rather than on the verdict this Start computed)
        // refuses the user's fix.
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        queue.HasSpaceWarning.Should().BeTrue("the stale warning is in place");

        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        await queue.StartAllAsync();

        downloader.CallCount.Should().Be(1);
    }

    [Fact]
    public void TotalQueuedBytes_CountsCancelledJobs_LikeTheGateDoes()
    {
        // The pill sits directly above the verdict. After an Abort it read "0 B" next to a
        // banner demanding 40 GB.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        queue.Jobs.Add(Job(40 * Gb, status: JobStatus.Cancelled));

        queue.TotalQueuedBytes.Should().Be(40 * Gb);
    }

    [Fact]
    public async Task RecheckSpace_NotesJobsOfUnknownSize_RatherThanIgnoringThem()
    {
        // A version whose primary file carries no sizeKB is enqueued with SizeBytes = 0 and is
        // invisible to a byte comparison, while the stamp claims the destinations were checked.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        queue.Jobs.Add(Job(0));

        await queue.RecheckSpaceAsync();

        queue.SpaceNote.Should().Contain("unknown size");
    }

    [Fact]
    public async Task RecheckSpace_SaysNothingToCheck_WhenNoRootWasProbed()
    {
        // ComputeSpaceState short-circuits on an empty snapshot without touching a drive, so
        // stamping "Checked 09:14" there claims a verification that never happened.
        var queue = Queue();
        queue.Clock = () => new DateTimeOffset(2026, 9, 20, 9, 14, 0, TimeSpan.Zero);

        await queue.RecheckSpaceAsync();

        queue.LastSpaceCheckDisplay.Should().Be("Nothing to check");
    }

    [Fact]
    public void ApplySpaceState_GoesThroughTheUiMarshal_NotStraightToTheBoundProperties()
    {
        // RunJobAsync writes job.Status after ConfigureAwait(false), which lands here through
        // OnJobPropertyChanged. LastSpaceCheckDisplay is bound and — unlike the warning — changes
        // on nearly every recompute, so an unmarshalled write raises PropertyChanged on a pool
        // thread and Avalonia throws "Call from invalid thread" inside a running download.
        var queue = Queue();
        var deferred = new List<Action>();
        queue.UiInvoke = deferred.Add;
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Clock = () => new DateTimeOffset(2026, 9, 20, 14, 40, 0, TimeSpan.Zero);

        queue.Jobs.Add(Job(5 * Gb));

        queue.HasSpaceWarning.Should().BeFalse("no bound property may be touched before the marshal runs");
        deferred.Should().NotBeEmpty("the verdict has to be handed to the UI thread, not written in place");

        foreach (var apply in deferred) apply();

        queue.HasSpaceWarning.Should().BeTrue();
        queue.LastSpaceCheckDisplay.Should().Contain("14:40:00");
    }

    [Fact]
    public async Task RecheckSpace_NotesUnknownFreeSpace_WithoutBlockingStart()
    {
        // A share that will not report its size is unknowable, not unreachable. Blocking on it
        // would ban network destinations outright.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Unknown;
        queue.Jobs.Add(Job(5 * Gb));

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeFalse("an unknowable free-space reading must not block the queue");
        queue.SpaceNote.Should().Contain("unknown", "but the user has to be told the gate could not run");
    }

    [Fact]
    public async Task RecheckSpace_KeepsCheckingTheOtherDrives_WhenOneIsUnreachable()
    {
        // One bad destination costs that destination's verdict, not the whole check.
        var queue = Queue();
        queue.FreeSpaceProbe = (root, _) =>
            root.StartsWith("Z", StringComparison.OrdinalIgnoreCase) ? Unreachable : Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb, targetDir: "Z:/models"));
        queue.Jobs.Add(Job(5 * Gb));

        await queue.RecheckSpaceAsync();

        queue.SpaceWarning.Should().Contain(Root, "the reachable drive is still short and must still be reported");
    }

    [MountPointFact]
    public async Task RecheckSpace_ChecksTwoVolumesSeparately_WhenTheyShareADriveLetter()
    {
        // Issue #581: grouping destinations by Path.GetPathRoot puts an 8 TB disk mounted at
        // C:\Models in the same bucket as C:\Users, because both roots read "C:\". Their bytes
        // were summed and measured against whichever volume the letter happened to name.
        var (_, mounted) = MountPointProbe.Create(_tempDir);
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        queue.Jobs.Add(Job(5 * Gb, targetDir: mounted));

        await queue.RecheckSpaceAsync();

        queue.SpaceWarning.Should().Contain(Root).And.Contain(MountPointProbe.TargetRoot,
            "each volume has to be named, because they are the two places the user must free room");
        queue.SpaceWarning!.Split('\n').Should().HaveCount(2, "two volumes are two verdicts, not one doubled one");
    }

    [Fact]
    public async Task RecheckSpace_ResolvesRelativeTargets_InsteadOfDroppingThem()
    {
        // Path.GetPathRoot("models/loras") is "", and the empty-root filter drops that job's
        // bytes out of the check entirely.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb, targetDir: Path.Combine("models", "loras")));

        await queue.RecheckSpaceAsync();

        queue.HasSpaceWarning.Should().BeTrue("a relative destination still lands on a real drive");
    }

    [Fact]
    public async Task RecheckSpace_StampsTheCheckTime_EvenWhenTheVerdictIsUnchanged()
    {
        // SpaceWarning is set through SetProperty, so an identical verdict raises no
        // PropertyChanged and nothing on screen moves — the button looks broken.
        var queue = Queue();
        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        queue.Clock = () => new DateTimeOffset(2026, 9, 20, 14, 32, 10, TimeSpan.Zero);
        await queue.RecheckSpaceAsync();
        queue.LastSpaceCheckDisplay.Should().Contain("14:32:10");

        // The user frees too little and presses again seconds later, so the warning string is
        // byte-identical — and minute resolution made the stamp identical too.
        queue.Clock = () => new DateTimeOffset(2026, 9, 20, 14, 32, 20, TimeSpan.Zero);
        await queue.RecheckSpaceAsync();

        queue.SpaceWarning.Should().NotBeNull("the verdict has not changed");
        queue.LastSpaceCheckDisplay.Should().Contain("14:32:20", "the press itself must leave a visible trace");
    }

    [Fact]
    public async Task StartAllAsync_RefusesToRun_WhenTheDriveFilledUpSinceTheLastCheck()
    {
        // Start's gate is a binding on the last-computed warning. Nothing re-reads the drive
        // at the moment the bytes are actually committed.
        var downloader = new InstantDownloader();
        var queue = Queue(downloader);
        queue.FreeSpaceProbe = (_, _) => Free(100 * Gb);
        queue.Jobs.Add(Job(5 * Gb));
        queue.HasSpaceWarning.Should().BeFalse("5 GB fits in 100 GB at queue time");

        queue.FreeSpaceProbe = (_, _) => Free(1 * Gb);
        await queue.StartAllAsync();

        downloader.CallCount.Should().Be(0, "Start must re-verify free space before committing the bytes");
        queue.HasSpaceWarning.Should().BeTrue("and the refusal must be visible in the panel");
    }
}
