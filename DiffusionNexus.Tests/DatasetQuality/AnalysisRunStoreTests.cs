using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Covers <see cref="AnalysisRunStore"/> — JSON run persistence into a <c>.quality-runs</c> subfolder,
/// plus the 50-file retention prune that DELETES user run files (issue #443). All I/O runs against a
/// throwaway temp directory; the prune selection is pinned to CHRONOLOGICAL order (issue #468 — a plain
/// lexicographic sort on the dd-MM-yyyy-prefixed file name does not sort chronologically across
/// month/year boundaries, e.g. "01-08-2026…" < "28-07-2026…" as strings despite being later in time).
/// </summary>
public class AnalysisRunStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _runsDir;
    private readonly AnalysisRunStore _store;

    public AnalysisRunStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"runstore_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _runsDir = Path.Combine(_tempDir, ".quality-runs");
        _store = new AnalysisRunStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task SaveAsync_WritesFileIntoRunsFolder_AndRoundTrips()
    {
        var record = MakeRecord(version: 3, LocalSecond(5));

        var savedPath = await _store.SaveAsync(_tempDir, record);

        File.Exists(savedPath).Should().BeTrue();
        Path.GetDirectoryName(savedPath).Should().Be(_runsDir);
        Path.GetFileName(savedPath).Should().EndWith("-Version-V3-run.json");

        var loaded = await _store.LoadAllAsync(_tempDir);
        loaded.Should().ContainSingle();
        loaded[0].Version.Should().Be(3);
        loaded[0].AnalyzedAtUtc.Should().Be(record.AnalyzedAtUtc);
    }

    [Fact]
    public async Task SaveAsync_NullRecord_Throws()
    {
        var act = async () => await _store.SaveAsync(_tempDir, null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task SaveAsync_EmptyDatasetPath_Throws()
    {
        var act = async () => await _store.SaveAsync("  ", MakeRecord(1, LocalSecond(0)));
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task LoadAllAsync_MissingFolder_ReturnsEmpty()
    {
        (await _store.LoadAllAsync(_tempDir)).Should().BeEmpty();
        (await _store.LoadAllAsync("   ")).Should().BeEmpty();
    }

    [Fact]
    public async Task LoadAllAsync_OrdersByTimestampDescending()
    {
        await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(10)));
        await _store.SaveAsync(_tempDir, MakeRecord(2, LocalSecond(30)));
        await _store.SaveAsync(_tempDir, MakeRecord(3, LocalSecond(20)));

        var loaded = await _store.LoadAllAsync(_tempDir);

        loaded.Select(r => r.AnalyzedAtUtc).Should().BeInDescendingOrder();
        loaded[0].Version.Should().Be(2); // second 30 is newest
    }

    [Fact]
    public async Task LoadAllAsync_SkipsCorruptedFiles()
    {
        await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(5)));
        await File.WriteAllTextAsync(Path.Combine(_runsDir, "garbage-run.json"), "{ this is not valid json");

        var loaded = await _store.LoadAllAsync(_tempDir);

        loaded.Should().ContainSingle().Which.Version.Should().Be(1);
    }

    [Fact]
    public async Task Delete_RemovesMatchingFile_ByTimestampAndVersion()
    {
        var record = MakeRecord(7, LocalSecond(42));
        await _store.SaveAsync(_tempDir, record);
        Directory.GetFiles(_runsDir, "*-run.json").Should().ContainSingle();

        _store.Delete(_tempDir, record);

        Directory.GetFiles(_runsDir, "*-run.json").Should().BeEmpty();
    }

    [Fact]
    public void Delete_NonExistentFile_DoesNotThrow()
    {
        var act = () => _store.Delete(_tempDir, MakeRecord(1, LocalSecond(0)));
        act.Should().NotThrow();
    }

    [Fact]
    public async Task SaveAsync_PrunesOldestBeyond50_KeepingNewest50_Chronologically()
    {
        // 51 runs, one per day, spanning Jan 15 -> Mar 6 2026 (crossing both a month and a year-day
        // boundary). Version N is chronological rank N (1 = oldest), so this asserts pruning by actual
        // elapsed time rather than by which saves happen to land on lexicographically-ordered file names.
        var start = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Local);
        for (int i = 1; i <= 51; i++)
            await _store.SaveAsync(_tempDir, MakeRecord(version: i, new DateTimeOffset(start.AddDays(i - 1))));

        var remaining = Directory.GetFiles(_runsDir, "*-run.json");
        remaining.Should().HaveCount(50);

        var names = remaining.Select(Path.GetFileName).ToList();
        names.Should().NotContain(n => n!.EndsWith("-Version-V1-run.json"), "version 1 is the chronologically oldest of the 51 and must be pruned");
        for (int i = 2; i <= 51; i++)
            names.Should().Contain(n => n!.EndsWith($"-Version-V{i}-run.json"), $"version {i} is chronologically newer than the pruned oldest and must survive");
    }

    [Fact]
    public async Task SaveAsync_PruneAcrossMonthBoundary_KeepsNewestRun_NotLexicographicallySmallest()
    {
        // 49 "filler" runs from Oct/Nov 2025, each using a day-of-month between 02 and 31 (never "01"),
        // so none can tie with the Aug-1 run below on the file name's leading two digits.
        for (int i = 0; i < 49; i++)
        {
            var day = 2 + (i % 30);
            var month = 10 + (i / 30); // 10 (Oct) for i<30, 11 (Nov) for i>=30
            await _store.SaveAsync(_tempDir, MakeRecord(version: 900 + i, LocalDateTime(2025, month, day)));
        }

        // The boundary pair: Jul 28 2026 (older) and Aug 1 2026 (newer). The file name's "dd" prefix
        // resets from "28" to "01" across the month boundary, so "01-08-2026…" sorts lexicographically
        // BEFORE "28-07-2026…" even though Aug 1 is later in time — the exact bug from issue #468.
        await _store.SaveAsync(_tempDir, MakeRecord(version: 201, LocalDateTime(2026, 7, 28)));
        await _store.SaveAsync(_tempDir, MakeRecord(version: 202, LocalDateTime(2026, 8, 1)));

        var remaining = Directory.GetFiles(_runsDir, "*-run.json");
        remaining.Should().HaveCount(50, "51 runs were saved and prune keeps only the newest 50");

        var names = remaining.Select(Path.GetFileName).ToList();
        names.Should().Contain(n => n!.EndsWith("-Version-V202-run.json"), "Aug 1 2026 is the single newest run and must survive despite sorting lexicographically smallest");
        names.Should().Contain(n => n!.EndsWith("-Version-V201-run.json"), "Jul 28 2026 is also recent and must survive");
        names.Should().NotContain(n => n!.EndsWith("-Version-V900-run.json"), "Oct 2 2025 is the chronologically oldest of all 51 and must be pruned");
    }

    [Fact]
    public async Task SaveAsync_PruneWithUnparsableFileName_DoesNotCrash_AndFallsBackToLastWriteTime()
    {
        // 50 valid runs at seconds 00..49 (same day/hour/minute) - no prune yet (<=50 files).
        for (int s = 0; s < 50; s++)
            await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(s)));

        // A foreign file that matches the "*-run.json" glob but does not fit the dd-MM-yyyy-HH-mm-ss
        // naming convention. Back-date its last-write-time so it is unambiguously the OLDEST file
        // present, exercising the documented fallback (File.GetLastWriteTimeUtc) for names that fail
        // to parse - it must not crash the prune and must not be silently preferred over real runs.
        var unparsablePath = Path.Combine(_runsDir, "not-a-timestamp-run.json");
        await File.WriteAllTextAsync(unparsablePath, "{}");
        File.SetLastWriteTimeUtc(unparsablePath, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        // Saving one more valid run brings the total to 52 (50 + 1 garbage + 1 new), triggering a prune
        // of 2 files.
        var act = async () => await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(50)));
        await act.Should().NotThrowAsync();

        var remaining = Directory.GetFiles(_runsDir, "*-run.json");
        remaining.Should().HaveCount(50);

        var names = remaining.Select(Path.GetFileName).ToList();
        names.Should().NotContain("not-a-timestamp-run.json", "the unparsable file falls back to last-write-time and, backdated to year 2000, is the oldest present and must be pruned");
        names.Should().NotContain(n => n!.Contains("-00-Version-V1-run.json"), "second-00 is the next-oldest real run and must also be pruned");
        names.Should().Contain(n => n!.Contains("-01-Version-V1-run.json"), "second-01 is the new oldest kept real run");
        names.Should().Contain(n => n!.Contains("-50-Version-V1-run.json"), "second-50 is the newest real run");
    }

    [Fact]
    public async Task SaveAsync_Exactly50Runs_PrunesNothing()
    {
        for (int s = 0; s < 50; s++)
            await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(s)));

        Directory.GetFiles(_runsDir, "*-run.json").Should().HaveCount(50);
    }

    // Builds a DateTimeOffset from a LOCAL wall-clock time so the store's ToLocalTime() file naming
    // is a no-op and the produced file name is deterministic on any machine timezone.
    private static DateTimeOffset LocalSecond(int second)
        => new(new DateTime(2026, 1, 1, 12, 0, second, DateTimeKind.Local));

    // Same idea as LocalSecond, but for an arbitrary calendar date (fixed noon local time) - used to
    // build cross-month/cross-year timestamps for the chronological-prune tests.
    private static DateTimeOffset LocalDateTime(int year, int month, int day)
        => new(new DateTime(year, month, day, 12, 0, 0, DateTimeKind.Local));

    private static AnalysisRunRecord MakeRecord(int version, DateTimeOffset when) => new()
    {
        AnalyzedAtUtc = when,
        Version = version,
        DatasetLabel = $"Test — V{version}",
        LoraType = LoraType.Character,
        Summary = new AnalysisSummary
        {
            TotalCaptionFiles = 10,
            TotalImageFiles = 10,
            CountBySeverity = new Dictionary<IssueSeverity, int> { [IssueSeverity.Warning] = 2 },
            ChecksRun = 5,
            FixableIssueCount = 1
        }
    };
}
