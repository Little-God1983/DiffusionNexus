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

    private const string Sha = "BF34D9BCCAFD2CCC92D83A6662A446FA12AAED03A98870E5902E2760ADBB110C";

    // Issue #617: the creator deleted the waitlisted version and published the same file
    // (same SHA256) as a new version of another model. The id lookup 404s, but the file is there.
    private CivitaiWaitlist CreateWithHashedEntry(out CivitaiWaitlistEntry entry, string path)
    {
        var wl = new CivitaiWaitlist(_client.Object, null, persistPathOverride: path);
        wl.TryAdd(866273, "Facial FLUX/KREA2", "Concept", isNsfw: false,
            Hashed(CivitaiWaitlistTests.Version(3289193, Now.AddDays(-1)), 3289193, "old.safetensors", Sha), Now);
        entry = wl.Entries.Single();
        return wl;
    }

    private static CivitaiModelVersion Hashed(CivitaiModelVersion v, int id, string fileName, string sha) => v with
    {
        Files =
        [
            new CivitaiModelFile
            {
                Name = fileName,
                Primary = true,
                SizeKB = 1024,
                DownloadUrl = $"https://civitai.example/api/download/models/{id}?fileId=1",
                Hashes = new CivitaiFileHashes { SHA256 = sha }
            }
        ]
    };

    private static CivitaiModelVersion Moved(DateTimeOffset? deadline = null) =>
        Hashed(CivitaiWaitlistTests.Version(3271224, deadline), 3271224, "new.safetensors", Sha) with
        {
            ModelId = 2893208,
            Name = "V1_Krea2",
            Model = new CivitaiVersionModelInfo { Name = "Cum_Everywhere_Krea2", Nsfw = true }
        };

    private void HashLookupReturns(CivitaiModelVersion? version) =>
        _client.Setup(c => c.GetModelVersionByHashAsync(Sha, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(version);

    [Fact]
    public async Task VersionGone_SameFileRepublished_FollowsTheFileAndBecomesAvailable()
    {
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "moved.json"));
        ClientReturns(null);
        HashLookupReturns(Moved());

        var version = await wl.RefreshEntryAsync(entry, apiKey: "key", utcNow: Now);

        version!.Id.Should().Be(3271224, "move-to-queue reuses the returned version");
        entry.Status.Should().Be(WaitlistEntryStatus.Available);
        entry.IsAvailable.Should().BeTrue();
        entry.VersionId.Should().Be(3271224);
        entry.ModelId.Should().Be(2893208);
        entry.FileName.Should().Be("new.safetensors");
        entry.DownloadUrl.Should().Be("https://civitai.example/api/download/models/3271224?fileId=1");
        entry.StatusDetail.Should().Contain("Cum_Everywhere_Krea2").And.Contain("V1_Krea2");
        entry.IsNsfw.Should().BeTrue("Open on Civitai must route by the model the entry now points at");
        _client.Verify(c => c.GetModelVersionByHashAsync(Sha, "key", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VersionGone_SameFileRepublishedStillGated_WaitsOnTheNewDeadline()
    {
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "moved-gated.json"));
        ClientReturns(null);
        HashLookupReturns(Moved(deadline: Now.AddDays(5)));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(5));
        entry.VersionId.Should().Be(3271224);
    }

    [Fact]
    public async Task VersionGone_FileGoneToo_IsFlaggedUnavailable()
    {
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "gone.json"));
        ClientReturns(null);
        HashLookupReturns(null);

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        entry.VersionId.Should().Be(3289193);
    }

    [Fact]
    public async Task VersionGone_WithoutStoredHash_SkipsTheHashLookup()
    {
        var wl = CreateWithEntry(out var entry);
        ClientReturns(null);

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        _client.Verify(c => c.GetModelVersionByHashAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task VersionGone_NewVersionAlreadyWaitlisted_StaysUnavailableWithoutADuplicate()
    {
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "dup.json"));
        wl.TryAdd(2893208, "Cum_Everywhere_Krea2", "Concept", isNsfw: true, Moved(Now.AddDays(5)), Now);
        ClientReturns(null);
        HashLookupReturns(Moved());

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        entry.VersionId.Should().Be(3289193);
        entry.StatusDetail.Should().Contain("already on your waitlist");
        wl.Entries.Count(e => e.VersionId == 3271224).Should().Be(1);
    }

    [Fact]
    public async Task VersionGone_FollowedFile_SurvivesARestart()
    {
        var path = Path.Combine(_tempDir, "persisted.json");
        var wl = CreateWithHashedEntry(out _, path);
        ClientReturns(null);
        HashLookupReturns(Moved());

        await wl.RefreshAllAsync(apiKey: null, utcNow: Now);

        var restored = new CivitaiWaitlist(_client.Object, null, persistPathOverride: path).Entries.Single();
        restored.VersionId.Should().Be(3271224);
        restored.ModelId.Should().Be(2893208);
        restored.DownloadUrl.Should().Be("https://civitai.example/api/download/models/3271224?fileId=1");
        restored.IsNsfw.Should().BeTrue();
        restored.StatusDetail.Should().Contain("Cum_Everywhere_Krea2");
    }

    [Fact]
    public async Task VersionGone_FollowedFile_KeepsTheMoveNoteOnTheNextUpdate()
    {
        // Review #624: the card keeps the old names, so without the note the globe button
        // would open a model the user never added, with nothing explaining why.
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "note.json"));
        _client.Setup(c => c.GetModelVersionAsync(3289193, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync((CivitaiModelVersion?)null);
        _client.Setup(c => c.GetModelVersionAsync(3271224, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(Moved());
        HashLookupReturns(Moved());

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);
        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Available);
        entry.StatusDetail.Should().Contain("Cum_Everywhere_Krea2");
    }

    [Fact]
    public async Task VersionGone_HashLookupFails_StaysUnavailableNotCheckFailed()
    {
        // Review #624: CheckFailed promotes to "Available now" once the old deadline has passed
        // (CreateWithHashedEntry's deadline is yesterday) — wrong for a version known to 404.
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "hash-error.json"));
        ClientReturns(null);
        _client.Setup(c => c.GetModelVersionByHashAsync(Sha, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
               .ThrowsAsync(new HttpRequestException("429 cooldown"));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);
        wl.RefreshAvailability(Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Unavailable);
        entry.IsAvailable.Should().BeFalse();
        entry.StatusDetail.Should().Contain("429 cooldown");
        entry.LastCheckedAt.Should().Be(Now);
    }

    [Fact]
    public async Task VersionLookup404_HashFindsTheSameVersion_JudgesIt()
    {
        var wl = CreateWithHashedEntry(out var entry, Path.Combine(_tempDir, "same-id.json"));
        ClientReturns(null);
        HashLookupReturns(Hashed(CivitaiWaitlistTests.Version(3289193, Now.AddDays(3)), 3289193, "old.safetensors", Sha));

        await wl.RefreshEntryAsync(entry, apiKey: null, utcNow: Now);

        entry.Status.Should().Be(WaitlistEntryStatus.Waiting);
        entry.EarlyAccessDeadline.Should().Be(Now.AddDays(3));
        entry.VersionId.Should().Be(3289193);
        entry.StatusDetail.Should().BeNull();
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
