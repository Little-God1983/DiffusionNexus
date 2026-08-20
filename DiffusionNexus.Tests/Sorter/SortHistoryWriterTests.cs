using System.Text.Json;
using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class SortHistoryWriterTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sorthist-");

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private static LoraSortPlan SamplePlan()
    {
        var candidate = new SortCandidate(
            @"E:\Loras\x\a.safetensors", "SDXL 1.0", "Character", null, null, 1000, []);
        var move = new PlannedMove(candidate, @"E:\Loras\SDXL 1.0\Character",
            @"E:\Loras\SDXL 1.0\Character\a.safetensors", PlannedAction.Transfer, WasRenamed: false);
        return new LoraSortPlan([move], @"E:\Loras", @"E:\Loras", IsMove: true,
            RequiredBytes: 0, TransferCount: 1, AlreadyInPlaceCount: 0,
            RenamedCount: 0, SkippedDuplicateCount: 0);
    }

    [Fact]
    public void WritePlanCreatesTimestampNamedManifestWithAllEntries()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var startedAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.FromHours(2));

        var path = writer.WritePlan(SamplePlan(), startedAt);

        Path.GetFileName(path).Should().Be("20260820-140000.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("isMove").GetBoolean().Should().BeTrue();
        var entry = doc.RootElement.GetProperty("entries")[0];
        entry.GetProperty("source").GetString().Should().Be(@"E:\Loras\x\a.safetensors");
        entry.GetProperty("completed").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void MarkCompletedFlagsOnlyTheMatchingEntry()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        writer.MarkCompleted(path, @"E:\Loras\x\a.safetensors");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.GetProperty("entries")[0].GetProperty("completed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void HistoryDirectoryIsCreatedOnDemand()
    {
        var nested = Path.Combine(_root.FullName, "does", "not", "exist");
        var writer = new SortHistoryWriter(nested);

        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        File.Exists(path).Should().BeTrue();
    }
}
