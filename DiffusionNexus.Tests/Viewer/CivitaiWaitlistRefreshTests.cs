using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the API re-check outcome matrix: deadline extended, confirmed free,
/// switched to permanent, deleted (404 → null), and network failure. Errors keep
/// the old data so a flaky connection can't wipe a countdown.
/// </summary>
public sealed class CivitaiWaitlistRefreshTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-refresh").FullName;
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiWaitlist CreateWithEntry(out CivitaiWaitlistEntry entry, DateTimeOffset? deadline = null)
    {
        var wl = new CivitaiWaitlist(_client.Object, null,
            persistPathOverride: Path.Combine(_tempDir, $"{Guid.NewGuid():N}.json"));
        var (result, pick) = CivitaiWaitlistTests.Card(
            CivitaiWaitlistTests.Version(500, deadline ?? Now.AddDays(2)));
        wl.TryAdd(result, pick, Now);
        entry = wl.Entries.Single();
        return wl;
    }

    private void ClientReturns(CivitaiModelVersion? version) =>
        _client.Setup(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(version);

    [Fact]
    public async Task ExtendedDeadline_UpdatesDeadlineAndStaysWaiting()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(CivitaiWaitlistTests.Version(500, Now.AddDays(14)));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(14));
        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
        entry.LastCheckedAt.Should().Be(Now);
    }

    [Fact]
    public async Task ConfirmedFree_BecomesAvailable()
    {
        var wl = CreateWithEntry(out var entry);
        // No EA signals at all → IsEarlyAccessActive == false.
        ClientReturns(new CivitaiModelVersion
        {
            Id = 500,
            Name = "v500",
            BaseModel = "Krea 2",
            DownloadUrl = "https://civitai.example/api/download/models/500"
        });

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Available);
        entry.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task SwitchedToPermanent_IsFlaggedNotDeleted()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(CivitaiWaitlistTests.Version(500, deadline: null, permanent: true));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.PermanentlyPaid);
        entry.IsAvailable.Should().BeFalse();
        wl.Entries.Should().ContainSingle("flagged entries stay listed until the user removes them");
    }

    [Fact]
    public async Task DeletedVersion_404_IsFlaggedUnavailable()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(null);

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        entry.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task NetworkError_KeepsOldDeadlineAndLastChecked()
    {
        var wl = CreateWithEntry(out var entry, deadline: Now.AddDays(2));
        var beforeChecked = entry.LastCheckedAt;
        _client.Setup(c => c.GetModelVersionAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("boom"));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.CheckFailed);
        entry.StatusDetail.Should().Contain("boom");
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(2), "a flaky connection must not wipe the countdown");
        entry.LastCheckedAt.Should().Be(beforeChecked);
    }

    [Fact]
    public async Task RefreshAll_ChecksEveryEntryAndUpdatesCounts()
    {
        var wl = new CivitaiWaitlist(_client.Object, null,
            persistPathOverride: Path.Combine(_tempDir, "all.json"));
        var (r1, p1) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(1, Now.AddDays(2)), modelId: 1);
        var (r2, p2) = CivitaiWaitlistTests.Card(CivitaiWaitlistTests.Version(2, Now.AddDays(2)), modelId: 2);
        wl.TryAdd(r1, p1, Now);
        wl.TryAdd(r2, p2, Now);
        // Entry 1 is now free; entry 2 got extended.
        _client.Setup(c => c.GetModelVersionAsync(1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new CivitaiModelVersion { Id = 1, Name = "v1", BaseModel = "Krea 2" });
        _client.Setup(c => c.GetModelVersionAsync(2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(CivitaiWaitlistTests.Version(2, Now.AddDays(30)));

        await wl.RefreshAllAsync(apiKey: null, utcNow: Now);

        wl.AvailableCount.Should().Be(1);
        wl.Entries.Single(e => e.VersionId == 2).EarlyAccessDeadline.Should().Be(Now.AddDays(30));
    }
}
