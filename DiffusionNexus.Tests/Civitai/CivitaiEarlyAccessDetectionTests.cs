using System.Text.Json;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;

namespace DiffusionNexus.Tests.Civitai;

/// <summary>
/// Civitai migrated early-access signaling a third time: current /models payloads
/// send <c>availability: "Public"</c> even for gated versions, dropped
/// <c>earlyAccessTimeFrame</c> entirely, and instead carry
/// <c>earlyAccessDeadline</c> (ISO date) plus <c>paidAccess</c>
/// (<c>{"permanent": bool, "endsAt": date|null}</c>). Both legacy signals the app
/// checked were dead, so every EA model showed unbadged and enqueued without the
/// EA warning (user-reported after null-stats-tolerant parsing made fresh — i.e.
/// EA-heavy — pages visible at all). Detection must honor all signal generations
/// and be time-aware: an expired deadline means the version is free now.
/// Shapes verified against the live API on 2026-08-12 (100-model Newest page:
/// 155 versions, all "Public", 8 gated only via paidAccess/earlyAccessDeadline).
/// </summary>
public class CivitaiEarlyAccessDetectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Reconstructed from the live probe: version 3220184 (published 2026-08-11, 12-day EA).</summary>
    private const string GatedVersionJson =
        """
        {"id":3220184,"index":0,"name":"v1.0","baseModel":"Illustrious","baseModelType":"Standard",
         "publishedAt":"2026-08-11T20:55:59.248Z","flags":0,"availability":"Public","nsfwLevel":0,
         "description":null,"trainedWords":[],"vaeId":null,
         "earlyAccessDeadline":"2026-08-23T20:55:59.248Z",
         "paidAccess":{"permanent":false,"endsAt":"2026-08-23T20:55:59.248Z"},
         "stats":{"downloadCount":null,"thumbsUpCount":null,"thumbsDownCount":null},
         "supportsGeneration":true,"files":[],"images":[]}
        """;

    [Fact]
    public void GatedVersion_CurrentApiShape_IsDetectedWhileDeadlineInFuture()
    {
        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(GatedVersionJson);

        version!.Availability.Should().Be("Public", "the availability field no longer signals EA");
        version.EarlyAccessDeadline.Should().Be(DateTimeOffset.Parse("2026-08-23T20:55:59.248Z"));
        version.PaidAccess.Should().NotBeNull();
        version.PaidAccess!.Permanent.Should().BeFalse();
        version.PaidAccess.EndsAt.Should().Be(DateTimeOffset.Parse("2026-08-23T20:55:59.248Z"));

        version.IsEarlyAccessActive(Now).Should().BeTrue();
    }

    [Fact]
    public void GatedVersion_AfterDeadlinePasses_IsFreeAgain()
    {
        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(GatedVersionJson);

        var afterExpiry = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        version!.IsEarlyAccessActive(afterExpiry).Should().BeFalse(
            "once the early-access period ends the version downloads without entitlement");
    }

    [Fact]
    public void PermanentPaidAccess_IsAlwaysGated()
    {
        var json = """{"id":1,"name":"v1","paidAccess":{"permanent":true,"endsAt":null}}""";

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.IsEarlyAccessActive(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero))
            .Should().BeTrue("permanent paid access never expires");
    }

    [Fact]
    public void PaidAccessWithoutEndDate_IsTreatedAsGated()
    {
        // Seen live (version 3201148): {"permanent":false,"endsAt":null}. No announced
        // end — flag it so the user gets the EA warning rather than a silent 401.
        var json = """{"id":1,"name":"v1","paidAccess":{"permanent":false,"endsAt":null}}""";

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.IsEarlyAccessActive(Now).Should().BeTrue();
    }

    [Fact]
    public void PlainPublicVersion_IsNotGated()
    {
        // Ordinary free version from the same live page: no deadline, paidAccess null.
        var json = """
            {"id":3220243,"name":"v1.0","availability":"Public","paidAccess":null,
             "stats":{"downloadCount":2,"thumbsUpCount":1,"thumbsDownCount":0}}
            """;

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.IsEarlyAccessActive(Now).Should().BeFalse();
    }

    [Fact]
    public void LegacyAvailabilitySignal_IsStillDetected()
    {
        // Older payloads (and sidecar files written from them) said availability=EarlyAccess.
        var json = """{"id":1,"name":"v1","availability":"EarlyAccess"}""";

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.IsEarlyAccessActive(Now).Should().BeTrue();
    }

    [Fact]
    public void LegacyTimeFrameSignal_IsStillDetected()
    {
        var json = """{"id":1,"name":"v1","earlyAccessTimeFrame":7}""";

        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json);

        version!.IsEarlyAccessActive(Now).Should().BeTrue();
    }

    [Fact]
    public void NullVersion_IsNotGated()
    {
        ((CivitaiModelVersion?)null).IsEarlyAccessActive(Now).Should().BeFalse();
    }

    [Fact]
    public void VersionPickItem_FlagsGatedVersion()
    {
        // Wiring check for the browser's version picker. Far-future deadline so this
        // test doesn't rot when the real 2026 dates pass (the VM uses UtcNow).
        var json = """
            {"id":1,"name":"v1","baseModel":"Illustrious","availability":"Public",
             "earlyAccessDeadline":"2126-01-01T00:00:00Z",
             "paidAccess":{"permanent":false,"endsAt":"2126-01-01T00:00:00Z"},"files":[]}
            """;
        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json)!;

        new CivitaiVersionPickItemViewModel(version).IsEarlyAccess.Should().BeTrue();
    }

    [Fact]
    public void VersionPickItem_DoesNotFlagExpiredGate()
    {
        var json = """
            {"id":1,"name":"v1","baseModel":"Illustrious","availability":"Public",
             "earlyAccessDeadline":"2026-01-01T00:00:00Z",
             "paidAccess":{"permanent":false,"endsAt":"2026-01-01T00:00:00Z"},"files":[]}
            """;
        var version = JsonSerializer.Deserialize<CivitaiModelVersion>(json)!;

        new CivitaiVersionPickItemViewModel(version).IsEarlyAccess.Should().BeFalse();
    }
}
