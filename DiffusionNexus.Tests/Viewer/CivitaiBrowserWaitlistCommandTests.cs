using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the browser VM's waitlist commands: remove, open-on-Civitai (with the
/// NSFW civitai.red host swap), and move-ready reporting to the status bar.
/// </summary>
public sealed class CivitaiBrowserWaitlistCommandTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-browser-waitlist").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiWaitlist Waitlist, CivitaiDownloadQueue Queue) Create()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null,
            persistPathOverride: Path.Combine(_tempDir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null,
            persistPathOverride: Path.Combine(_tempDir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null);
        return (vm, waitlist, queue);
    }

    private static CivitaiWaitlistEntry Entry(int versionId, bool nsfw = false, DateTimeOffset? deadline = null) => new()
    {
        ModelId = 900,
        VersionId = versionId,
        ModelName = "Model",
        VersionName = $"v{versionId}",
        DownloadUrl = $"https://civitai.example/api/download/models/{versionId}",
        IsNsfw = nsfw,
        EarlyAccessDeadline = deadline
    };

    [Fact]
    public void RemoveWaitlistEntry_RemovesFromService()
    {
        var (vm, waitlist, _) = Create();
        var entry = Entry(1);
        waitlist.Entries.Add(entry);

        vm.RemoveWaitlistEntryCommand.Execute(entry);

        waitlist.Entries.Should().BeEmpty();
    }

    [Fact]
    public void OpenWaitlistEntry_UsesCivitaiCom_AndVersionDeepLink()
    {
        var (vm, waitlist, _) = Create();
        string? opened = null;
        vm.UrlOpener = url => opened = url;
        var entry = Entry(2);
        waitlist.Entries.Add(entry);

        vm.OpenWaitlistEntryOnCivitaiCommand.Execute(entry);

        opened.Should().Be("https://civitai.com/models/900?modelVersionId=2");
    }

    [Fact]
    public void OpenWaitlistEntry_NsfwModel_UsesCivitaiRed()
    {
        var (vm, _, _) = Create();
        string? opened = null;
        vm.UrlOpener = url => opened = url;

        vm.OpenWaitlistEntryOnCivitaiCommand.Execute(Entry(3, nsfw: true));

        opened.Should().StartWith("https://civitai.red/");
    }

    [Fact]
    public async Task MoveReadyCommand_ReportsCountInStatusMessage()
    {
        var (vm, waitlist, queue) = Create();
        var entry = Entry(4, deadline: Now.AddMinutes(-1));
        entry.RefreshAvailability(Now);
        waitlist.Entries.Add(entry);

        await vm.MoveReadyWaitlistToQueueCommand.ExecuteAsync(null);

        queue.Jobs.Should().ContainSingle();
        vm.StatusMessage.Should().Contain("1");
    }

    [Fact]
    public async Task UpdateCommand_WithoutClient_StillRefreshesCountsWithoutThrowing()
    {
        var (vm, waitlist, _) = Create();
        var entry = Entry(5, deadline: DateTimeOffset.UtcNow.AddMilliseconds(-1));
        waitlist.Entries.Add(entry);

        await vm.UpdateWaitlistCommand.ExecuteAsync(null);

        waitlist.AvailableCount.Should().Be(1);
    }
}
