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

    private static LoraSortPlan SamplePlan(IReadOnlyList<string>? sidecars = null)
    {
        var candidate = new SortCandidate(
            @"E:\Loras\x\a.safetensors", "SDXL 1.0", "Character", null, null, 1000, sidecars ?? []);
        var move = new PlannedMove(candidate, @"E:\Loras\SDXL 1.0\Character",
            @"E:\Loras\SDXL 1.0\Character\a_42.safetensors", PlannedAction.Transfer, WasRenamed: true);
        return new LoraSortPlan([move], @"E:\Loras", @"E:\Loras", IsMove: true,
            DeleteEmptySourceFolders: false, RequiredBytes: 0, TransferCount: 1, AlreadyInPlaceCount: 0,
            RenamedCount: 1, SkippedDuplicateCount: 0);
    }

    [Fact]
    public void WritePlanCreatesTimestampNamedManifestWithAllEntries()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var startedAt = new DateTimeOffset(2026, 8, 20, 14, 0, 0, TimeSpan.FromHours(2));

        var path = writer.WritePlan(SamplePlan(), startedAt);

        Path.GetFileName(path!).Should().Be("20260820-140000.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path!));
        doc.RootElement.GetProperty("isMove").GetBoolean().Should().BeTrue();
        var entry = doc.RootElement.GetProperty("entries")[0];
        entry.GetProperty("source").GetString().Should().Be(@"E:\Loras\x\a.safetensors");
    }

    [Fact]
    public void PlanEntriesCarrySidecarSourceAndTargetForRestore()
    {
        // Review 6.1: the schema recorded no sidecar at all, so a Restore built on it
        // would move the weights back and strand every companion file.
        var writer = new SortHistoryWriter(_root.FullName);

        var path = writer.WritePlan(SamplePlan([@"E:\Loras\x\a.civitai.info"]), DateTimeOffset.Now);

        using var doc = JsonDocument.Parse(File.ReadAllText(path!));
        var sidecar = doc.RootElement.GetProperty("entries")[0].GetProperty("sidecars")[0];
        sidecar.GetProperty("source").GetString().Should().Be(@"E:\Loras\x\a.civitai.info");
        sidecar.GetProperty("target").GetString()
            .Should().Be(@"E:\Loras\SDXL 1.0\Character\a_42.civitai.info");
    }

    [Fact]
    public void MarkCompletedAppendsOneLinePerFileWithoutRewritingThePlan()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now)!;
        var planBefore = File.ReadAllText(path);

        writer.MarkCompleted(path, @"E:\Loras\x\a.safetensors");
        writer.MarkCompleted(path, @"E:\Loras\x\b.safetensors");

        File.ReadAllText(path).Should().Be(planBefore); // plan file is written exactly once
        File.ReadAllLines(SortHistoryWriter.CompletionLogPath(path)).Should().HaveCount(2);
        SortHistoryWriter.ReadCompleted(path).Should().BeEquivalentTo(
            [@"E:\Loras\x\a.safetensors", @"E:\Loras\x\b.safetensors"]);
    }

    [Fact]
    public void ReadCompletedSkipsATornFinalLineFromAKilledRun()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now)!;
        writer.MarkCompleted(path, @"E:\Loras\x\a.safetensors");
        File.AppendAllText(SortHistoryWriter.CompletionLogPath(path), "{\"source\":\"E:\\\\Lo");

        SortHistoryWriter.ReadCompleted(path).Should().ContainSingle()
            .Which.Should().Be(@"E:\Loras\x\a.safetensors");
    }

    [Fact]
    public void ReadCompletedOfAnUntouchedManifestIsEmpty()
    {
        var writer = new SortHistoryWriter(_root.FullName);
        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now)!;

        SortHistoryWriter.ReadCompleted(path).Should().BeEmpty();
    }

    [Fact]
    public void HistoryDirectoryIsCreatedOnDemand()
    {
        var nested = Path.Combine(_root.FullName, "does", "not", "exist");
        var writer = new SortHistoryWriter(nested);

        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void UnwritableHistoryDirectoryYieldsNullInsteadOfAbortingTheSort()
    {
        // Review 6.1: WritePlan sat outside any try/catch, so a failure writing this
        // currently-unread side-file aborted the run before a single file was touched.
        var blocked = Path.Combine(_root.FullName, "blocked");
        File.WriteAllText(blocked, "not a directory");
        var writer = new SortHistoryWriter(blocked);

        var path = writer.WritePlan(SamplePlan(), DateTimeOffset.Now);

        path.Should().BeNull();
    }

    [Fact]
    public void MarkCompletedWithoutAManifestIsANoOp()
    {
        var act = () => new SortHistoryWriter(_root.FullName).MarkCompleted(null, @"E:\x\a.safetensors");

        act.Should().NotThrow();
    }
}
