using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services.DatasetQuality;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Covers <see cref="AnalysisRunStore"/> — JSON run persistence into a <c>.quality-runs</c> subfolder,
/// plus the 50-file retention prune that DELETES user run files (issue #443). All I/O runs against a
/// throwaway temp directory; the prune selection/ordering is pinned exactly, including its lexicographic
/// (not chronological) file selection.
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
    public async Task SaveAsync_PrunesOldestBeyond50_KeepingNewest50()
    {
        // Save 51 runs at the same day/hour/minute, differing only by second (00..50). Because the file
        // name is dd-MM-yyyy-HH-mm-ss-…, differing only in the trailing seconds makes lexicographic
        // (the prune's OrderByDescending(f => f)) order identical to chronological order here — so the
        // second-00 run is both the oldest and the lexicographically smallest, and is the one deleted.
        for (int s = 0; s <= 50; s++)
            await _store.SaveAsync(_tempDir, MakeRecord(1, LocalSecond(s)));

        var remaining = Directory.GetFiles(_runsDir, "*-run.json");
        remaining.Should().HaveCount(50);

        var names = remaining.Select(Path.GetFileName).ToList();
        names.Should().NotContain(n => n!.Contains("-00-Version-V1-run.json"), "second-00 is the oldest and is pruned");
        names.Should().Contain(n => n!.Contains("-01-Version-V1-run.json"), "second-01 is the new oldest kept");
        names.Should().Contain(n => n!.Contains("-50-Version-V1-run.json"), "second-50 is the newest");
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
