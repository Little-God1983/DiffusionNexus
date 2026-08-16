using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers the waitlist service: add/dedup, permanent-paid rejection, available
/// counting, and the JSON persist/restore round-trip via the path override.
/// </summary>
public sealed class CivitaiWaitlistTests : IDisposable
{
    /// <summary>
    /// The fixtures' "now", anchored to today's date rather than a fixed calendar day.
    ///
    /// These tests describe deadlines relative to <c>Now</c> ("three days out", "one day ago"),
    /// but the service recomputes availability against the real <see cref="DateTimeOffset.UtcNow"/>
    /// when it restores persisted entries. A pinned date therefore rots: once the real clock passes
    /// it, every "future deadline" in the fixtures silently becomes a past one and entries restore
    /// as Available instead of Waiting. Anchoring to today keeps the relative intent true forever.
    ///
    /// Whole seconds, so a JSON persist/restore round-trip compares exactly.
    /// </summary>
    private static readonly DateTimeOffset Now =
        new(DateTime.UtcNow.Date.AddHours(10), TimeSpan.Zero);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dn-waitlist-tests").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string PersistPath(string name = "waitlist.json") => Path.Combine(_tempDir, name);

    private CivitaiWaitlist Create(string? file = null)
        => new(null, null, persistPathOverride: PersistPath(file ?? "waitlist.json"));

    internal static CivitaiModelVersion Version(
        int id,
        DateTimeOffset? deadline,
        bool? permanent = null)
        => new()
        {
            Id = id,
            Name = $"v{id}",
            BaseModel = "Krea 2",
            DownloadUrl = $"https://civitai.example/api/download/models/{id}",
            EarlyAccessDeadline = deadline,
            PaidAccess = permanent is null && deadline is null
                ? null
                : new CivitaiPaidAccess { Permanent = permanent, EndsAt = deadline }
        };

    internal static (CivitaiResultViewModel Result, CivitaiVersionPickItemViewModel Pick) Card(
        CivitaiModelVersion version, int modelId = 77, string name = "Test LoRA")
    {
        var model = new CivitaiModel { Id = modelId, Name = name, ModelVersions = [version] };
        var result = new CivitaiResultViewModel(model, showNsfwPreviews: false);
        return (result, result.Versions[0]);
    }

    [Fact]
    public void TryAdd_CapturesDeadlineAndPayloadFromBrowseData()
    {
        var wl = Create();
        var (result, pick) = Card(Version(3224172, Now.AddDays(7)));

        wl.TryAdd(result, pick, Now).Should().BeTrue();

        var e = wl.Entries.Single();
        e.VersionId.Should().Be(3224172);
        e.ModelId.Should().Be(77);
        e.ModelName.Should().Be("Test LoRA");
        e.EarlyAccessDeadline.Should().Be(Now.AddDays(7));
        e.AddedAt.Should().Be(Now);
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void TryAdd_SameVersionTwice_IsRejected()
    {
        var wl = Create();
        var (result, pick) = Card(Version(10, Now.AddDays(7)));

        wl.TryAdd(result, pick, Now).Should().BeTrue();
        wl.TryAdd(result, pick, Now).Should().BeFalse();
        wl.Entries.Should().HaveCount(1);
    }

    [Fact]
    public void TryAdd_PermanentlyPaidVersion_IsRejected()
    {
        var wl = Create();
        var (result, pick) = Card(Version(11, deadline: null, permanent: true));

        wl.TryAdd(result, pick, Now).Should().BeFalse("permanently paid versions never become free");
        wl.Entries.Should().BeEmpty();
    }

    [Fact]
    public void AvailableCount_TracksDeadlines()
    {
        var wl = Create();
        var (r1, p1) = Card(Version(1, Now.AddDays(-1)), modelId: 1);
        wl.TryAdd(r1, p1, Now);
        var (r2, p2) = Card(Version(2, Now.AddDays(5)), modelId: 2);
        wl.TryAdd(r2, p2, Now);

        wl.RefreshAvailability(Now);

        wl.AvailableCount.Should().Be(1);
        wl.HasAvailable.Should().BeTrue();
    }

    [Fact]
    public void PersistRestore_RoundTripsEntries()
    {
        var file = "roundtrip.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(42, Now.AddDays(3)));
        wl.TryAdd(result, pick, Now);

        var restored = Create(file);

        var e = restored.Entries.Single();
        e.VersionId.Should().Be(42);
        e.ModelName.Should().Be("Test LoRA");
        e.EarlyAccessDeadline.Should().Be(Now.AddDays(3));
        e.Status.Should().Be(WaitlistEntryStatus.Waiting);
        e.DownloadUrl.Should().Be("https://civitai.example/api/download/models/42");
    }

    [Fact]
    public void Restore_RecomputesAvailability_ForDeadlinesThatPassedWhileClosed()
    {
        var file = "stale.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(43, DateTimeOffset.UtcNow.AddMilliseconds(-1)));
        wl.TryAdd(result, pick);

        var restored = Create(file);

        restored.Entries.Single().IsAvailable.Should().BeTrue();
        restored.AvailableCount.Should().Be(1);
    }

    [Fact]
    public void Remove_DropsEntryAndPersists()
    {
        var file = "remove.json";
        var wl = Create(file);
        var (result, pick) = Card(Version(50, Now.AddDays(2)));
        wl.TryAdd(result, pick, Now);

        wl.Remove(wl.Entries.Single());

        wl.Entries.Should().BeEmpty();
        Create(file).Entries.Should().BeEmpty("removal must be persisted");
    }
}
