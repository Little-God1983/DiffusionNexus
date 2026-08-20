using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class LoraSortPlannerTests
{
    private static SortCandidate Candidate(
        string path, string? baseModel = "SDXL 1.0", string category = "Character",
        int? versionId = null, string? sha = null, long size = 1000)
        => new(path, baseModel, category, versionId, sha, size, []);

    private static LoraSortOptions Options(bool includeCategory = true, bool isMove = true,
        string source = @"E:\Loras", string target = @"E:\Loras")
        => new(source, target, includeCategory, isMove, DeleteEmptySourceFolders: false);

    private static LoraSortPlanner Planner(
        Func<string, string>? hash = null, Func<string, bool>? exists = null)
        => new(hash ?? (_ => throw new InvalidOperationException("hashFile must not be called")),
               exists ?? (_ => false));

    [Fact]
    public void SimpleCandidateIsPlannedIntoBaseModelCategoryFolder()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\flat\a.safetensors")], Options());

        var move = plan.Moves.Single();
        move.Action.Should().Be(PlannedAction.Transfer);
        move.TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\a.safetensors");
        move.WasRenamed.Should().BeFalse();
        plan.TransferCount.Should().Be(1);
    }

    [Fact]
    public void BaseModelOnlyStructureSkipsCategorySegment()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\flat\a.safetensors")], Options(includeCategory: false));

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\a.safetensors");
    }

    [Fact]
    public void PlaceholderBaseModelLandsInUnknown()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\flat\a.safetensors", baseModel: "???")], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\Unknown\Character\a.safetensors");
    }

    [Fact]
    public void FileAlreadyAtComputedTargetIsMarkedInPlace()
    {
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\SDXL 1.0\Character\a.safetensors")], Options());

        plan.Moves.Single().Action.Should().Be(PlannedAction.AlreadyInPlace);
        plan.AlreadyInPlaceCount.Should().Be(1);
        plan.TransferCount.Should().Be(0);
    }

    [Fact]
    public void DifferentContentCollisionGetsVersionIdRename()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", versionId: 111, sha: "aaa"),
            Candidate(@"E:\Loras\y\V1.safetensors", versionId: 222, sha: "bbb"),
        ], Options());

        plan.Moves[0].TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1.safetensors");
        plan.Moves[1].TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_222.safetensors");
        plan.Moves[1].WasRenamed.Should().BeTrue();
        plan.RenamedCount.Should().Be(1);
    }

    [Fact]
    public void IdenticalContentCollisionIsSkippedAsDuplicate()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", sha: "aaa"),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: "AAA"), // hash compare is case-insensitive
        ], Options());

        plan.Moves[1].Action.Should().Be(PlannedAction.SkippedDuplicate);
        plan.SkippedDuplicateCount.Should().Be(1);
        plan.TransferCount.Should().Be(1);
    }

    [Fact]
    public void MissingHashesAreComputedLazilyOnlyForCollidingFiles()
    {
        var hashed = new List<string>();
        var planner = Planner(hash: p => { hashed.Add(p); return p.Contains(@"\x\") ? "aaa" : "bbb"; });

        planner.BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors"),
            Candidate(@"E:\Loras\y\V1.safetensors", versionId: 5),
            Candidate(@"E:\Loras\z\unique.safetensors"),
        ], Options());

        hashed.Should().BeEquivalentTo(new[]
            { @"E:\Loras\x\V1.safetensors", @"E:\Loras\y\V1.safetensors" });
    }

    [Fact]
    public void OnDiskTargetCollisionWithDifferentContentIsRenamed()
    {
        var planner = Planner(
            hash: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors" ? "disk" : "mine",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors", versionId: 9)], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_9.safetensors");
    }

    [Fact]
    public void OnDiskTargetCollisionWithIdenticalContentIsSkipped()
    {
        var planner = Planner(
            hash: _ => "same",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors")], Options());

        plan.Moves.Single().Action.Should().Be(PlannedAction.SkippedDuplicate);
    }

    [Fact]
    public void SameVolumeMoveRequiresZeroBytes()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\x\a.safetensors", size: 5000)],
            Options(isMove: true, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.RequiredBytes.Should().Be(0);
    }

    [Fact]
    public void CopyRequiresAllPlannedBytesButNotSkippedOnes()
    {
        var plan = Planner().BuildPlan(
        [
            Candidate(@"E:\Loras\x\a.safetensors", size: 5000),
            Candidate(@"E:\Loras\x\V1.safetensors", sha: "s", size: 700),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: "s", size: 700), // duplicate → skipped
        ], Options(isMove: false, source: @"E:\Loras", target: @"D:\Backup"));

        plan.RequiredBytes.Should().Be(5700);
    }

    [Fact]
    public void CrossVolumeMoveRequiresTransferredBytes()
    {
        var plan = Planner().BuildPlan([Candidate(@"E:\Loras\x\a.safetensors", size: 5000)],
            Options(isMove: true, source: @"E:\Loras", target: @"D:\Sorted"));

        plan.RequiredBytes.Should().Be(5000);
    }
}
