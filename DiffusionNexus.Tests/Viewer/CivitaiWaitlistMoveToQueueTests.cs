using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers "Move ready to queue": only deadline-passed entries move, each is
/// re-verified against the API first, confirmed-free ones become queue jobs and
/// leave the waitlist, still-gated ones stay with a corrected deadline.
/// </summary>
public sealed class CivitaiWaitlistMoveToQueueTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-move").FullName;
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiDownloadQueue Queue() => new(null, null, null, null,
        persistPathOverride: Path.Combine(_tempDir, $"q-{Guid.NewGuid():N}.json"));

    private CivitaiWaitlist Waitlist(ICivitaiClient? client) => new(client, null,
        persistPathOverride: Path.Combine(_tempDir, $"w-{Guid.NewGuid():N}.json"));

    private static CivitaiModelVersion FreeVersion(int id) => new()
    {
        Id = id,
        Name = $"v{id}",
        BaseModel = "Krea 2",
        DownloadUrl = $"https://civitai.example/api/download/models/{id}"
    };

    [Fact]
    public async Task ReadyEntry_ConfirmedFree_MovesToQueueAndLeavesWaitlist()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(600, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        _client.Setup(c => c.GetModelVersionAsync(600, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FreeVersion(600));
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1);
        wl.Entries.Should().BeEmpty();
        var job = queue.Jobs.Single();
        job.VersionId.Should().Be(600);
        job.IsEarlyAccess.Should().BeFalse("the version was just verified free");
        job.Status.Should().Be(JobStatus.Queued);
        job.CivitaiVersion.Should().NotBeNull("the fresh version avoids a re-fetch at download time");
    }

    [Fact]
    public async Task ReadyEntry_StillGatedAfterReVerify_StaysWithCorrectedDeadline()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(601, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        // Creator extended EA: API now says 10 more days.
        _client.Setup(c => c.GetModelVersionAsync(601, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CivitaiWaitlistTests.Version(601, Now.AddDays(10)));
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(0);
        queue.Jobs.Should().BeEmpty();
        var entry = wl.Entries.Single();
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(10));
        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
    }

    [Fact]
    public async Task NotYetReadyEntries_AreNeverTouched()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(602, Now.AddDays(3)));
        wl.TryAdd(r, p, Now);
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(0);
        _client.Verify(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never, "entries still counting down must not trigger API calls");
        wl.Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task VersionAlreadyInQueue_EntryIsRemovedWithoutDuplicateJob()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(603, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        _client.Setup(c => c.GetModelVersionAsync(603, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(FreeVersion(603));
        var queue = Queue();
        queue.Jobs.Add(new CivitaiDownloadJob { VersionId = 603, ModelName = "Test LoRA", VersionName = "v603" });

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1, "the entry's goal (version queued) is met either way");
        queue.Jobs.Should().HaveCount(1);
        wl.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadyEntry_ApiThrowsDuringReVerify_StaysOnWaitlistUnenqueued()
    {
        var wl = Waitlist(_client.Object);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(605, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        _client.Setup(c => c.GetModelVersionAsync(605, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("network down"));
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(0, "a failed re-check must never enqueue an unverified entry");
        queue.Jobs.Should().BeEmpty();
        var entry = wl.Entries.Single();
        // Status itself reverts to Available here — CheckFailedEntry_WithPassedDeadline_StillBecomesAvailable
        // (CivitaiWaitlistEntryTests) documents that as deliberate: a stale network failure must not pin the
        // entry, since move-to-queue always re-verifies. StatusDetail is what proves the re-check actually
        // failed and that the guard above kept it off the queue via the returned version, not via Status.
        entry.StatusDetail.Should().Be("network down", "the failed re-check's diagnostic must survive");
    }

    [Fact]
    public async Task NoClient_MovesFromStoredDataOnly()
    {
        // Headless/design-time: no API to verify against — trust the local countdown.
        var wl = Waitlist(null);
        var (r, p) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(604, Now.AddMinutes(-5)));
        wl.TryAdd(r, p, Now);
        var queue = Queue();

        var moved = await wl.MoveReadyToQueueAsync(queue, apiKey: null, utcNow: Now);

        moved.Should().Be(1);
        queue.Jobs.Single().DownloadUrl.Should().Be("https://civitai.example/api/download/models/604");
    }
}
