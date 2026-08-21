using DiffusionNexus.UI.Services.Lora.Sorting;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public class LoraSortPlannerTests
{
    // Real-shaped hashes: the planner only trusts a stored value that is exactly
    // 64 hex digits (review 4.6), anything else is hashed lazily instead.
    private const string ShaA = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string ShaB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

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
    public void UnknownCategoryPlansIntoTheBaseModelFolderItself()
    {
        // Downloader parity (review 4.1): no category segment for an unresolved category.
        var plan = Planner().BuildPlan(
            [Candidate(@"E:\Loras\flat\a.safetensors", category: "Unknown")], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\a.safetensors");
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
            Candidate(@"E:\Loras\x\V1.safetensors", versionId: 111, sha: ShaA),
            Candidate(@"E:\Loras\y\V1.safetensors", versionId: 222, sha: ShaB),
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
            Candidate(@"E:\Loras\x\V1.safetensors", sha: ShaA),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: ShaA.ToUpperInvariant()), // hash compare is case-insensitive
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
    public void UnreadableCollisionTargetIsRenamedNotSkippedAndNeverEscapes()
    {
        // Review 2.4: hashing the on-disk claimant threw IOException straight out of
        // BuildPlan when a backend held the file open — "Preview failed", no plan at all.
        // A file we cannot read is a file we cannot prove is ours: rename, never overwrite.
        var planner = Planner(
            hash: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors"
                ? throw new IOException("locked by another process")
                : "mine",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors", versionId: 9)], Options());

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_9.safetensors");
        plan.SkippedDuplicateCount.Should().Be(0);
    }

    [Fact]
    public void UnreadableCandidateIsRenamedNotSkipped()
    {
        var planner = Planner(
            hash: p => p == @"E:\Loras\x\V1.safetensors"
                ? throw new UnauthorizedAccessException("denied")
                : "disk",
            exists: p => p == @"E:\Loras\SDXL 1.0\Character\V1.safetensors");

        var plan = planner.BuildPlan([Candidate(@"E:\Loras\x\V1.safetensors", versionId: 9)], Options());

        plan.Moves.Single().Action.Should().Be(PlannedAction.Transfer);
        plan.Moves.Single().WasRenamed.Should().BeTrue();
    }

    [Fact]
    public void CopyModeReRunTransfersNothing()
    {
        // Review 4.3: run 1 copied V1 (content A, version 42) in as V1_42 because the plain
        // name held someone else's content B. Copy mode leaves the source in place, so run 2
        // collided again, found V1_42 "taken" — never comparing content — and copied A in a
        // SECOND time as V1_2. Run 3 → V1_3, unbounded. Disk state below is post-run-1.
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\Sorted\SDXL 1.0\Character\V1.safetensors",     // a different model's file
            @"E:\Sorted\SDXL 1.0\Character\V1_42.safetensors",  // our copy from run 1
        };
        var planner = Planner(
            hash: p => p == @"E:\Sorted\SDXL 1.0\Character\V1.safetensors" ? ShaB : ShaA,
            exists: onDisk.Contains);

        var plan = planner.BuildPlan(
            [Candidate(@"E:\Loras\x\V1.safetensors", versionId: 42, sha: ShaA)],
            Options(isMove: false, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.TransferCount.Should().Be(0);
        plan.SkippedDuplicateCount.Should().Be(1);
        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Sorted\SDXL 1.0\Character\V1_42.safetensors");
    }

    [Fact]
    public void DifferentContentAtTheVersionSuffixedNameStillFallsToNumericSuffix()
    {
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\Sorted\SDXL 1.0\Character\V1.safetensors",
            @"E:\Sorted\SDXL 1.0\Character\V1_42.safetensors",
        };
        var planner = Planner(hash: _ => ShaB, exists: onDisk.Contains);

        var plan = planner.BuildPlan(
            [Candidate(@"E:\Loras\x\V1.safetensors", versionId: 42, sha: ShaA)],
            Options(isMove: false, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors");
        plan.Moves.Single().WasRenamed.Should().BeTrue();
    }

    [Fact]
    public void CopyModeReRunWithNoVersionIdAlsoTransfersNothing()
    {
        // Review-2 A3, case A: no version id at all — the case the numeric convention exists for.
        // Run 1 copied V1 (content A) in as V1_2 because the plain name held content B. Run 2 hit
        // the plain name again, skipped the _{versionId} slot entirely (there is no version id),
        // and saw its own run-1 copy at V1_2 as merely "taken" — never hashed — so it produced
        // V1_3. Run 3 produced V1_4. Unbounded, identical to the bug 4.3 was raised for.
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\Sorted\SDXL 1.0\Character\V1.safetensors",   // a different model's file
            @"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors", // our copy from run 1
        };
        var planner = Planner(
            hash: p => p == @"E:\Sorted\SDXL 1.0\Character\V1.safetensors" ? ShaB : ShaA,
            exists: onDisk.Contains);

        var plan = planner.BuildPlan(
            [Candidate(@"E:\Loras\x\V1.safetensors", versionId: null, sha: ShaA)],
            Options(isMove: false, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.TransferCount.Should().Be(0);
        plan.SkippedDuplicateCount.Should().Be(1);
        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors");
    }

    [Fact]
    public void CopyModeReRunWithAThirdModelInTheVersionSlotAlsoTransfersNothing()
    {
        // Review-2 A3, case B: the _{versionId} slot is held by a THIRD model, so run 1 landed at
        // V1_2. Run 2 used to walk plain (different) → V1_42 (different) → V1_2 "taken", uncompared,
        // and copy itself in a second time as V1_3.
        var onDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"E:\Sorted\SDXL 1.0\Character\V1.safetensors",
            @"E:\Sorted\SDXL 1.0\Character\V1_42.safetensors",
            @"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors", // our copy from run 1
        };
        var planner = Planner(
            hash: p => p == @"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors" ? ShaA : ShaB,
            exists: onDisk.Contains);

        var plan = planner.BuildPlan(
            [Candidate(@"E:\Loras\x\V1.safetensors", versionId: 42, sha: ShaA)],
            Options(isMove: false, source: @"E:\Loras", target: @"E:\Sorted"));

        plan.TransferCount.Should().Be(0);
        plan.SkippedDuplicateCount.Should().Be(1);
        plan.Moves.Single().TargetFilePath.Should().Be(@"E:\Sorted\SDXL 1.0\Character\V1_2.safetensors");
    }

    [Fact]
    public void LegacyDashedUppercaseStoredHashStillMatchesAFreshlyComputedOne()
    {
        // Review 4.6: ModelFile.HashSHA256 has been stored with separators by older import
        // paths (LoraDuplicateFinder.NormalizeHash exists for exactly this), so the stored
        // value never equalled a fresh hash and the duplicate was renamed and copied in.
        var dashed = string.Join('-', Enumerable.Range(0, 8)
            .Select(i => ShaA.Substring(i * 8, 8))).ToUpperInvariant();
        var planner = Planner(hash: _ => ShaA);

        var plan = planner.BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", sha: dashed),
            Candidate(@"E:\Loras\y\V1.safetensors"), // no stored hash → computed as ShaA
        ], Options());

        plan.Moves[1].Action.Should().Be(PlannedAction.SkippedDuplicate);
        plan.RenamedCount.Should().Be(0);
    }

    [Theory]
    [InlineData("not-a-hash")]                                                   // non-hex
    [InlineData("0123456789abcdef")]                                             // truncated
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefff")] // too long
    public void GarbageStoredHashIsTreatedAsAbsentAndHashedLazily(string stored)
    {
        var hashed = new List<string>();
        var planner = Planner(hash: p => { hashed.Add(p); return p.Contains(@"\x\") ? ShaA : ShaB; });

        var plan = planner.BuildPlan(
        [
            Candidate(@"E:\Loras\x\V1.safetensors", sha: stored),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: stored, versionId: 7),
        ], Options());

        hashed.Should().Contain(@"E:\Loras\y\V1.safetensors");
        plan.Moves[1].Action.Should().Be(PlannedAction.Transfer); // NOT a bogus duplicate skip
        plan.Moves[1].TargetFilePath.Should().Be(@"E:\Loras\SDXL 1.0\Character\V1_7.safetensors");
    }

    [Fact]
    public void CancellationStopsPlanning()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => Planner().BuildPlan([Candidate(@"E:\Loras\x\a.safetensors")], Options(), cts.Token);

        act.Should().Throw<OperationCanceledException>();
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
            Candidate(@"E:\Loras\x\V1.safetensors", sha: ShaA, size: 700),
            Candidate(@"E:\Loras\y\V1.safetensors", sha: ShaA, size: 700), // duplicate → skipped
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
