using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

/// <summary>
/// #651: the sorter named its duplicates and its failures only as counts, and a finished run left
/// nothing behind but a status line. These cover the two lists that now name them.
/// </summary>
public sealed class SortReportDialogViewModelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortreport-");
    private readonly Mock<IProcessLauncher> _launcher = new();

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(params string[] parts) => Path.Combine([_root.FullName, .. parts]);

    private PlannedMove Move(string sourceRel, string targetRel, PlannedAction action = PlannedAction.Transfer,
        bool renamed = false, long size = 1024)
    {
        var source = In(sourceRel.Split('\\'));
        var target = In(targetRel.Split('\\'));
        var candidate = new SortCandidate(source, "SDXL 1.0", "Character", null, null, size, []);
        return new PlannedMove(candidate, Path.GetDirectoryName(target)!, target, action, renamed);
    }

    private LoraSortPlan Plan(bool isMove, params PlannedMove[] moves)
        => new(moves, In("Loras"), In("Sorted"), isMove,
            RequiredBytes: 0,
            TransferCount: moves.Count(m => m.Action == PlannedAction.Transfer),
            AlreadyInPlaceCount: moves.Count(m => m.Action == PlannedAction.AlreadyInPlace),
            RenamedCount: moves.Count(m => m.WasRenamed),
            SkippedDuplicateCount: moves.Count(m => m.Action == PlannedAction.SkippedDuplicate));

    [Fact]
    public void TheDuplicateListNamesEachSkippedFileAndTheCopyItMatched()
    {
        var dup = Move(@"Loras\old\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors",
            PlannedAction.SkippedDuplicate, size: 2048);
        var plan = Plan(isMove: true, Move(@"Loras\b.safetensors", @"Sorted\SDXL 1.0\b.safetensors"), dup);

        var vm = new SortDuplicatesDialogViewModel(plan, _launcher.Object, logger: null);

        var row = vm.Rows.Should().ContainSingle().Subject;
        row.Path.Should().Be(dup.Candidate.FilePath);
        row.RelatedPath.Should().Be(dup.TargetFilePath);
        row.RelatedText.Should().Be($"≡ identical to {dup.TargetFilePath}");
        row.SizeText.Should().Be(FileSizeFormatter.Format(2048));
        vm.Title.Should().Be("Duplicates that will be skipped (1)");
        vm.Note.Should().Contain("stay where they are", "in move mode the copies remain in the source folder");
    }

    [Fact]
    public void ACopyRunDoesNotClaimTheDuplicatesStayBehind()
    {
        var plan = Plan(isMove: false,
            Move(@"Loras\a.safetensors", @"Sorted\SDXL 1.0\a.safetensors", PlannedAction.SkippedDuplicate));

        var vm = new SortDuplicatesDialogViewModel(plan, _launcher.Object, logger: null);

        vm.Note.Should().Contain("identical").And.NotContain("stay where they are");
        vm.Rows.Should().ContainSingle();
    }

    [Fact]
    public void AFinishedRunReportsEveryListAndItsCounts()
    {
        var moved = Move(@"Loras\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors");
        var renamed = Move(@"Loras\v5.safetensors", @"Sorted\Krea 2\Concept\v5_3261406.safetensors", renamed: true);
        var failed = Move(@"Loras\locked.safetensors", @"Sorted\Wan\locked.safetensors");
        var dup = Move(@"Loras\old\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors", PlannedAction.SkippedDuplicate);
        var settled = Move(@"Sorted\SDXL 1.0\c.safetensors", @"Sorted\SDXL 1.0\c.safetensors", PlannedAction.AlreadyInPlace);
        var plan = Plan(isMove: true, moved, renamed, failed, dup, settled);
        var result = new LoraSortResult(Moved: 2, Copied: 0, Skipped: 1, Failed: 1, Cancelled: false, ManifestPath: null)
        {
            Transferred = [moved, renamed],
            Failures = [new SortFailure(failed, "The file is in use by another process.")],
            Elapsed = TimeSpan.FromSeconds(102)
        };

        var vm = new SortReportDialogViewModel(plan, result, _launcher.Object, logger: null);

        vm.Title.Should().Be($"Sort report: move, {In("Loras")} → {In("Sorted")}");
        vm.IsCancelled.Should().BeFalse();
        (vm.TransferredCount, vm.TransferredLabel).Should().Be((2, "moved"));
        vm.RenamedCount.Should().Be(1);
        vm.DuplicateCount.Should().Be(1);
        vm.AlreadyInPlaceCount.Should().Be(1);
        vm.FailedCount.Should().Be(1);
        vm.DurationText.Should().Be("1 min 42 s");

        vm.FailedRows.Should().ContainSingle().Which.Should().Match<SortReportFileRowViewModel>(r =>
            r.Path == failed.Candidate.FilePath && r.Detail == "The file is in use by another process.");
        vm.DuplicateRows.Should().ContainSingle().Which.RelatedPath.Should().Be(dup.TargetFilePath);
        vm.RenamedRows.Should().ContainSingle().Which.RelatedText.Should().Be($"→ renamed to {renamed.TargetFilePath}");
        vm.TransferredRows.Select(r => r.Path).Should().Equal(moved.Candidate.FilePath, renamed.Candidate.FilePath);
        vm.TransferredRows[0].RelatedText.Should().Be($"→ {moved.TargetFilePath}");

        vm.FailedHeader.Should().Be("Failed (1)");
        vm.DuplicatesHeader.Should().Be("Duplicates skipped (1)");
        vm.RenamedHeader.Should().Be("Renamed (1)");
        vm.TransferredHeader.Should().Be("Moved (2)");
        (vm.HasFailures, vm.HasDuplicates, vm.HasRenamed, vm.HasTransferred).Should().Be((true, true, true, true));
    }

    [Fact]
    public void EmptySectionsAreHiddenAndACopyRunSaysCopied()
    {
        var copied = Move(@"Loras\a.safetensors", @"Sorted\SDXL 1.0\a.safetensors");
        var result = new LoraSortResult(0, 1, 0, 0, false, null) { Transferred = [copied] };

        var vm = new SortReportDialogViewModel(Plan(isMove: false, copied), result, _launcher.Object, logger: null);

        (vm.HasFailures, vm.HasDuplicates, vm.HasRenamed, vm.HasTransferred).Should().Be((false, false, false, true));
        vm.TransferredLabel.Should().Be("copied");
        vm.TransferredHeader.Should().Be("Copied (1)");
        vm.Title.Should().StartWith("Sort report: copy, ");
    }

    /// <summary>
    /// "Cancelled after X of Y" counts every file the run reached — a failure was reached too —
    /// and the remainder is what the run never touched. A plan of 4 that stopped after one
    /// success and one failure left 2 untouched, not 3.
    /// </summary>
    [Fact]
    public void ACancelledRunSaysHowFarItGotAndWhatItLeftUntouched()
    {
        var moves = Enumerable.Range(0, 4)
            .Select(i => Move($@"Loras\m{i}.safetensors", $@"Sorted\SDXL 1.0\m{i}.safetensors")).ToArray();
        var result = new LoraSortResult(1, 0, 0, 1, Cancelled: true, null)
        {
            Transferred = [moves[0]],
            Failures = [new SortFailure(moves[1], "denied")]
        };

        var vm = new SortReportDialogViewModel(Plan(isMove: true, moves), result, _launcher.Object, logger: null);

        vm.IsCancelled.Should().BeTrue();
        vm.CancelledText.Should().Be("Cancelled after 2 of 4 files. The remaining 2 were not touched.");
    }

    [Fact]
    public void ACancelledRunWithOneFileLeftSaysSoInTheSingular()
    {
        var moves = Enumerable.Range(0, 2)
            .Select(i => Move($@"Loras\m{i}.safetensors", $@"Sorted\SDXL 1.0\m{i}.safetensors")).ToArray();
        var result = new LoraSortResult(1, 0, 0, 0, Cancelled: true, null) { Transferred = [moves[0]] };

        new SortReportDialogViewModel(Plan(isMove: true, moves), result, _launcher.Object, logger: null)
            .CancelledText.Should().Be("Cancelled after 1 of 2 files. The remaining file was not touched.");
    }

    /// <summary>
    /// Review: the executor also flags a run cancelled when the token fires during the batch DB
    /// flush that follows the last transfer. Every file was reached by then, and "The remaining 0
    /// were not touched" says the run stopped early when it did not.
    /// </summary>
    [Fact]
    public void ACancelOnceEveryFileWasReachedDoesNotClaimFilesWereLeftUntouched()
    {
        var moves = Enumerable.Range(0, 2)
            .Select(i => Move($@"Loras\m{i}.safetensors", $@"Sorted\SDXL 1.0\m{i}.safetensors")).ToArray();
        var result = new LoraSortResult(2, 0, 0, 0, Cancelled: true, null) { Transferred = [.. moves] };

        new SortReportDialogViewModel(Plan(isMove: true, moves), result, _launcher.Object, logger: null)
            .CancelledText.Should().Be("Cancelled after every file had been reached. None were left untouched.");
    }

    /// <summary>
    /// Review: a duplicate can match another file of the same plan — one\a and two\a are identical,
    /// and two\a is skipped against the place one\a is going. When one\a's transfer fails, that copy
    /// does not exist, and the row must not leave the user believing it does.
    /// </summary>
    [Fact]
    public void ADuplicateWhoseCopyWasNeverCreatedSaysSo()
    {
        var first = Move(@"Loras\one\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors");
        var dup = Move(@"Loras\two\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors", PlannedAction.SkippedDuplicate);
        var result = new LoraSortResult(0, 0, 1, 1, Cancelled: false, null)
        {
            Failures = [new SortFailure(first, "The file is in use by another process.")]
        };

        var vm = new SortReportDialogViewModel(Plan(isMove: true, first, dup), result, _launcher.Object, logger: null);

        vm.DuplicateRows.Should().ContainSingle().Which.Detail
            .Should().Be("Its identical copy was not created: that file's transfer failed or did not run.");
    }

    [Fact]
    public void ADuplicateWhoseCopyWasCreatedHasNoWarning()
    {
        var first = Move(@"Loras\one\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors");
        var dup = Move(@"Loras\two\a.safetensors", @"Sorted\SDXL 1.0\Character\a.safetensors", PlannedAction.SkippedDuplicate);
        var result = new LoraSortResult(1, 0, 1, 0, Cancelled: false, null) { Transferred = [first] };

        var vm = new SortReportDialogViewModel(Plan(isMove: true, first, dup), result, _launcher.Object, logger: null);

        vm.DuplicateRows.Should().ContainSingle().Which.HasDetail.Should().BeFalse();
    }

    /// <summary>
    /// Review: the status line warns when no restore point was recorded or empty folders could not
    /// be removed, and the report — the summary the user actually reads — showed a clean run.
    /// </summary>
    [Fact]
    public void TheReportCarriesTheWarningsTheStatusLineGives()
    {
        var moved = Move(@"Loras\a.safetensors", @"Sorted\SDXL 1.0\a.safetensors");
        var result = new LoraSortResult(1, 0, 0, 0, false, ManifestPath: null) { Transferred = [moved] };

        var vm = new SortReportDialogViewModel(Plan(isMove: true, moved), result, _launcher.Object, logger: null,
            emptyFolderCleanupFailed: true);

        vm.HasWarnings.Should().BeTrue();
        vm.Warnings.Should().Equal(
            "No restore point was recorded for this run — see the log.",
            "Some empty source folders could not be removed.");
    }

    [Fact]
    public void ARunWithARestorePointAndCleanFoldersHasNoWarnings()
    {
        var moved = Move(@"Loras\a.safetensors", @"Sorted\SDXL 1.0\a.safetensors");
        var result = new LoraSortResult(1, 0, 0, 0, false, ManifestPath: In("history", "run.json")) { Transferred = [moved] };

        var vm = new SortReportDialogViewModel(Plan(isMove: true, moved), result, _launcher.Object, logger: null);

        vm.HasWarnings.Should().BeFalse();
        vm.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void OpenTargetFolderOpensThePlansTargetRoot()
    {
        Directory.CreateDirectory(In("Sorted"));
        var result = new LoraSortResult(0, 0, 0, 0, false, null);

        var vm = new SortReportDialogViewModel(Plan(isMove: true), result, _launcher.Object, logger: null);
        vm.OpenTargetFolderCommand.Execute(null);

        _launcher.Verify(l => l.OpenFolder(In("Sorted")), Times.Once);
    }

    /// <summary>
    /// A row's file may be gone by the time anyone clicks it — after a move the source path no
    /// longer exists — so the menu falls back to the folder, and to nothing when that is gone too.
    /// Paired positive and negative cases, so a no-op implementation cannot pass.
    /// </summary>
    [Fact]
    public void OpenInFolderSelectsAnExistingFileAndFallsBackToItsFolder()
    {
        var existing = In("Sorted", "SDXL 1.0", "a.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "w");
        var movedAway = In("Loras", "flat", "a.safetensors");
        Directory.CreateDirectory(Path.GetDirectoryName(movedAway)!);
        var nowhere = In("Gone", "x.safetensors");

        var row = new SortReportFileRowViewModel(movedAway, existing, "→", null, 1, _launcher.Object, logger: null);
        row.OpenInFolderCommand.Execute(null);
        row.OpenRelatedInFolderCommand.Execute(null);
        new SortReportFileRowViewModel(nowhere, null, null, null, 1, _launcher.Object, logger: null)
            .OpenInFolderCommand.Execute(null);

        _launcher.Verify(l => l.OpenFolder(In("Loras", "flat")), Times.Once);
        _launcher.Verify(l => l.OpenFolderAndSelectFile(existing), Times.Once);
        _launcher.VerifyNoOtherCalls();
    }
}
