using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Issue #379: the queue's disk-space warning is only recomputed when the queue,
/// a job or the destination changes — nothing watches the drive itself. Freeing
/// space outside the app therefore left a stale warning on screen with Start
/// disabled, and no way to re-run the check. Covers the on-demand recheck.
/// </summary>
public sealed class CivitaiDownloadQueueSpaceRecheckTests : IDisposable
{
    private const long Gb = 1L << 30;

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-queue-space-recheck").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiDownloadQueue Queue() => new(
        downloader: null, logger: null, civitaiClient: null, destination: null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    /// <summary>A queued job whose files land on the temp dir's drive.</summary>
    private CivitaiDownloadJob Job(long sizeBytes) => new()
    {
        ModelName = "Model",
        VersionName = "v1",
        SizeBytes = sizeBytes,
        CustomTargetDirectory = _tempDir,
        Status = JobStatus.Queued,
    };

    private string Root => Path.GetPathRoot(_tempDir)!;

    [Fact]
    public void RecheckSpace_ClearsTheWarning_WhenTheDriveGainedRoomOutsideTheApp()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = _ => 1 * Gb;
        queue.Jobs.Add(Job(5 * Gb));

        queue.HasSpaceWarning.Should().BeTrue("5 GB of downloads do not fit in 1 GB of free space");

        // The user goes and deletes files. Nothing in the app observes that.
        queue.FreeSpaceProbe = _ => 100 * Gb;
        queue.HasSpaceWarning.Should().BeTrue("nothing recomputes until the user asks");

        queue.RecheckSpace();

        queue.HasSpaceWarning.Should().BeFalse("the recheck must re-read the drive and clear the stale warning");
        queue.SpaceWarning.Should().BeNull();
    }

    [Fact]
    public void RecheckSpace_RaisesTheWarning_WhenTheDriveFilledUpOutsideTheApp()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = _ => 100 * Gb;
        queue.Jobs.Add(Job(5 * Gb));

        queue.HasSpaceWarning.Should().BeFalse("5 GB fits in 100 GB");

        // Something else on the machine ate the drive.
        queue.FreeSpaceProbe = _ => 1 * Gb;

        queue.RecheckSpace();

        queue.HasSpaceWarning.Should().BeTrue("the recheck must catch a drive that filled up after queueing");
        queue.SpaceWarning.Should().Contain(Root);
    }

    [Fact]
    public void RecheckSpaceCommand_RefreshesTheQueueWarning()
    {
        var queue = Queue();
        queue.FreeSpaceProbe = _ => 1 * Gb;
        queue.Jobs.Add(Job(5 * Gb));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue,
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_tempDir, "waitlist.json")), null);

        queue.FreeSpaceProbe = _ => 100 * Gb;
        vm.RecheckSpaceCommand.Execute(null);

        queue.HasSpaceWarning.Should().BeFalse("the queue panel's recheck button must re-run the check");
    }
}
