using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the Browse Civitai tab's "Clear all" guard: when jobs are still queued or
/// mid-download the user must confirm before the queue is cleared (clearing cancels
/// them), while a queue with only finished jobs clears without any prompt.
/// Also covers the page-level Refresh command.
/// </summary>
public sealed class CivitaiBrowserClearQueueTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-queue-tests").FullName;
    private readonly Mock<IDialogService> _dialog = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiDownloadQueue Queue) Create()
    {
        // Persist path redirected into a temp dir so tests never read or clobber
        // the real LocalAppData queue snapshot.
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue,
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_tempDir, "waitlist.json")), null)
        {
            DialogService = _dialog.Object
        };
        return (vm, queue);
    }

    private static CivitaiDownloadJob Job(JobStatus status) => new()
    {
        ModelName = "Model",
        VersionName = "v1",
        Status = status
    };

    [Fact]
    public async Task WhenDownloadsAreQueuedOrActive_AndUserDeclines_ThenQueueIsUntouched()
    {
        var (vm, queue) = Create();
        queue.Jobs.Add(Job(JobStatus.Queued));
        queue.Jobs.Add(Job(JobStatus.Downloading));
        _dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync(false);

        await vm.ClearQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().HaveCount(2, "declining the warning must keep every job");
    }

    [Fact]
    public async Task WhenDownloadsAreQueuedOrActive_AndUserConfirms_ThenQueueIsCleared()
    {
        var (vm, queue) = Create();
        queue.Jobs.Add(Job(JobStatus.Queued));
        queue.Jobs.Add(Job(JobStatus.Downloading));
        _dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync(true);

        await vm.ClearQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenOnlyFinishedJobsExist_ThenClearAllDoesNotPrompt()
    {
        var (vm, queue) = Create();
        queue.Jobs.Add(Job(JobStatus.Completed));
        queue.Jobs.Add(Job(JobStatus.Failed));
        queue.Jobs.Add(Job(JobStatus.Cancelled));

        await vm.ClearQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().BeEmpty();
        _dialog.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WhenNoDialogServiceIsAvailable_ThenClearAllProceeds()
    {
        // Headless fallback: without a dialog service the command must not deadlock
        // or silently do nothing — it clears, matching the early-access prompt's fallback.
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue-headless.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue,
            new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_tempDir, "waitlist-headless.json")), null);
        queue.Jobs.Add(Job(JobStatus.Downloading));

        await vm.ClearQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshCommand_RunsWithoutServices()
    {
        var (vm, _) = Create();

        var act = () => vm.RefreshCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync("refresh must be safe with no client (design-time/offline)");
    }
}
