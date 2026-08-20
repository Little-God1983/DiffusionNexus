using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sorter;

public sealed class LoraSortExecutorTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortexec-");
    private readonly List<(string OldPath, string NewPath)> _dbChanges = [];

    private sealed class RecordingPathUpdater(List<(string, string)> sink) : ILocalPathUpdater
    {
        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
            CancellationToken ct = default)
        {
            sink.AddRange(changes.Select(c => (c.OldPath, c.NewPath)));
            return Task.CompletedTask;
        }
    }

    public void Dispose()
    {
        try { _root.Delete(recursive: true); } catch (IOException) { }
    }

    private string In(params string[] parts) => Path.Combine([_root.FullName, .. parts]);

    private string Write(string relative, string content = "weights")
    {
        var path = In(relative.Split('\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private LoraSortExecutor Executor()
        => new(new FileOperations(), new RecordingPathUpdater(_dbChanges),
               new SortHistoryWriter(In("history")), logger: null);

    private LoraSortPlan Plan(bool isMove, params PlannedMove[] moves)
        => new(moves, _root.FullName, _root.FullName, isMove,
            RequiredBytes: 0,
            TransferCount: moves.Count(m => m.Action == PlannedAction.Transfer),
            AlreadyInPlaceCount: moves.Count(m => m.Action == PlannedAction.AlreadyInPlace),
            RenamedCount: moves.Count(m => m.WasRenamed),
            SkippedDuplicateCount: moves.Count(m => m.Action == PlannedAction.SkippedDuplicate));

    private PlannedMove Move(string sourceRel, string targetRel, params string[] sidecarRels)
    {
        var source = In(sourceRel.Split('\\'));
        var target = In(targetRel.Split('\\'));
        var candidate = new SortCandidate(source, "SDXL 1.0", "Character", null, null,
            File.Exists(source) ? new FileInfo(source).Length : 0,
            sidecarRels.Select(r => In(r.Split('\\'))).ToList());
        return new PlannedMove(candidate, Path.GetDirectoryName(target)!, target,
            PlannedAction.Transfer, WasRenamed: false);
    }

    [Fact]
    public async Task MoveTransfersModelAndSidecarsAndReportsDbChange()
    {
        var model = Write(@"flat\a.safetensors");
        Write(@"flat\a.civitai.info", "meta");
        var move = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors",
            @"flat\a.civitai.info");

        var result = await Executor().ExecuteAsync(Plan(isMove: true, move));

        result.Moved.Should().Be(1);
        File.Exists(model).Should().BeFalse();
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        File.Exists(In("SDXL 1.0", "Character", "a.civitai.info")).Should().BeTrue();
        _dbChanges.Should().ContainSingle()
            .Which.Should().Be((model, In("SDXL 1.0", "Character", "a.safetensors")));
    }

    [Fact]
    public async Task CopyLeavesSourceIntactAndDoesNotTouchDb()
    {
        var model = Write(@"flat\a.safetensors");
        var move = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors");

        var result = await Executor().ExecuteAsync(Plan(isMove: false, move));

        result.Copied.Should().Be(1);
        File.Exists(model).Should().BeTrue();
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        _dbChanges.Should().BeEmpty();
    }

    [Fact]
    public async Task RenamedTargetRenamesSidecarsToo()
    {
        Write(@"flat\V1.safetensors");
        Write(@"flat\V1.preview.png", "img");
        var move = Move(@"flat\V1.safetensors", @"SDXL 1.0\Character\V1_42.safetensors",
            @"flat\V1.preview.png");

        await Executor().ExecuteAsync(Plan(isMove: true, move));

        File.Exists(In("SDXL 1.0", "Character", "V1_42.preview.png")).Should().BeTrue();
    }

    [Fact]
    public async Task FailedFileIsSkippedAndRunContinues()
    {
        Write(@"flat\a.safetensors");
        var ghost = Move(@"flat\ghost.safetensors", @"SDXL 1.0\Character\ghost.safetensors");
        // ghost source never written → FileOperations.MoveFile fallback throws FileNotFoundException(IOException)
        var ok = Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors");

        var result = await Executor().ExecuteAsync(Plan(isMove: true, ghost, ok));

        result.Failed.Should().Be(1);
        result.Moved.Should().Be(1);
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
    }

    [Fact]
    public async Task CancellationStopsBetweenFilesButKeepsCompletedWork()
    {
        Write(@"flat\a.safetensors");
        Write(@"flat\b.safetensors");
        using var cts = new CancellationTokenSource();
        var progress = new Progress<(double, string)>();
        var executor = Executor();
        var plan = Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors"),
            Move(@"flat\b.safetensors", @"SDXL 1.0\Character\b.safetensors"));

        // Cancel after the first file via a progress callback.
        var syncProgress = new SynchronousProgress(cts);
        var result = await executor.ExecuteAsync(plan, syncProgress, cts.Token);

        result.Cancelled.Should().BeTrue();
        result.Moved.Should().Be(1);
        File.Exists(In("SDXL 1.0", "Character", "a.safetensors")).Should().BeTrue();
        File.Exists(In("flat", "b.safetensors")).Should().BeTrue();
        _dbChanges.Should().HaveCount(1); // pending batch flushed on cancel
    }

    private sealed class SynchronousProgress(CancellationTokenSource cts)
        : IProgress<(double Fraction, string Status)>
    {
        public void Report((double Fraction, string Status) value) => cts.Cancel();
    }

    [Fact]
    public async Task CancellationDuringDbBatchFlushStillReturnsGracefullyAndRetriesFlush()
    {
        // 20 transfers trigger the mid-loop DbBatchSize flush; the updater cancels
        // that first call. The executor must not throw: it reports Cancelled and
        // delivers the kept batch via the CancellationToken.None final flush.
        var moves = new List<PlannedMove>();
        for (var i = 0; i < 21; i++)
        {
            Write($@"flat\m{i}.safetensors");
            moves.Add(Move($@"flat\m{i}.safetensors", $@"SDXL 1.0\Character\m{i}.safetensors"));
        }
        var updater = new CancelThenRecordPathUpdater(_dbChanges);
        var executor = new LoraSortExecutor(new FileOperations(), updater,
            new SortHistoryWriter(In("history")), logger: null);

        var result = await executor.ExecuteAsync(Plan(isMove: true, moves.ToArray()));

        result.Cancelled.Should().BeTrue();
        result.Moved.Should().Be(20);
        File.Exists(In("flat", "m20.safetensors")).Should().BeTrue();
        _dbChanges.Should().HaveCount(20); // delivered by the final CancellationToken.None retry
    }

    private sealed class CancelThenRecordPathUpdater(List<(string, string)> sink) : ILocalPathUpdater
    {
        private bool _first = true;

        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
            CancellationToken ct = default)
        {
            if (_first) { _first = false; throw new OperationCanceledException(); }
            sink.AddRange(changes.Select(c => (c.OldPath, c.NewPath)));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task DbFailureNeverCrashesTheRun()
    {
        Write(@"flat\a.safetensors");
        var executor = new LoraSortExecutor(new FileOperations(), new ThrowingPathUpdater(),
            new SortHistoryWriter(In("history")), logger: null);

        var result = await executor.ExecuteAsync(Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors")));

        result.Moved.Should().Be(1); // file physically moved; DB staleness is logged, not fatal
    }

    private sealed class ThrowingPathUpdater : ILocalPathUpdater
    {
        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
            CancellationToken ct = default) => throw new InvalidOperationException("db down");
    }

    [Fact]
    public async Task FailedDbBatchIsDeferredAndRetriedExactlyOnceWithoutStallingProgress()
    {
        // Review 2.2: the failure handler `continue`d without clearing the batch, so the
        // list stayed at DbBatchSize and every following transfer re-tripped the flush and
        // re-threw — while the triggering file never got its MarkCompleted, done++ or
        // progress report. 21 files, updater throws on its first call.
        var moves = new List<PlannedMove>();
        for (var i = 0; i < 21; i++)
        {
            Write($@"flat\m{i}.safetensors");
            moves.Add(Move($@"flat\m{i}.safetensors", $@"SDXL 1.0\Character\m{i}.safetensors"));
        }
        var updater = new FailFirstCallPathUpdater(_dbChanges);
        var executor = new LoraSortExecutor(new FileOperations(), updater,
            new SortHistoryWriter(In("history")), logger: null);
        var reports = new List<(double Fraction, string Status)>();

        var result = await executor.ExecuteAsync(Plan(isMove: true, moves.ToArray()),
            new ActionProgress(reports.Add));

        result.Moved.Should().Be(21);
        result.Cancelled.Should().BeFalse();
        result.Failed.Should().Be(0);
        reports.Should().HaveCount(21);              // no frozen progress bar
        reports[^1].Fraction.Should().Be(1.0);
        updater.Calls.Should().Be(2);                // the failed batch, then ONE retry
        _dbChanges.Should().HaveCount(21);           // 20 deferred + the 21st, in one final call
        SortHistoryWriter.ReadCompleted(result.ManifestPath!).Should().HaveCount(21);
    }

    private sealed class FailFirstCallPathUpdater(List<(string, string)> sink) : ILocalPathUpdater
    {
        public int Calls { get; private set; }

        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes,
            CancellationToken ct = default)
        {
            if (++Calls == 1) throw new InvalidOperationException("db down");
            sink.AddRange(changes.Select(c => (c.OldPath, c.NewPath)));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ManifestIsWrittenBeforeExecutionAndEntriesGetCompleted()
    {
        Write(@"flat\a.safetensors");
        var executor = Executor();

        var result = await executor.ExecuteAsync(Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors")));

        result.ManifestPath.Should().NotBeNull();
        File.Exists(result.ManifestPath!).Should().BeTrue();
        SortHistoryWriter.ReadCompleted(result.ManifestPath!).Should().ContainSingle()
            .Which.Should().Be(In("flat", "a.safetensors"));
    }

    [Fact]
    public async Task ManifestFailureNeitherFailsNorMisreportsATransferredFile()
    {
        // Review 2.1: the manifest write sat inside the per-file try, so an I/O error
        // from it counted a file that HAD moved as failed, and a JsonException from a
        // truncated manifest unwound out of the whole run. It is a restore aid — it
        // must never contradict what is on disk.
        Write(@"flat\a.safetensors");
        Write(@"flat\b.safetensors");
        var executor = Executor();
        // Nuke the history directory once the first file is journalled: every later
        // MarkCompleted then throws DirectoryNotFoundException.
        var progress = new ActionProgress(_ =>
        {
            try { Directory.Delete(In("history"), recursive: true); } catch (IOException) { }
        });

        var result = await executor.ExecuteAsync(Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors"),
            Move(@"flat\b.safetensors", @"SDXL 1.0\Character\b.safetensors")), progress);

        result.Moved.Should().Be(2);
        result.Failed.Should().Be(0);
        File.Exists(In("SDXL 1.0", "Character", "b.safetensors")).Should().BeTrue();
    }

    [Fact]
    public async Task PendingDbRowsAreFlushedEvenWhenTheLoopBlowsUp()
    {
        // Review 2.1: the terminal flush sat after the loop, so any exception the
        // per-file filter does not cover dropped up to 19 path repoints — files at
        // their new location, DB pointing at the old one.
        Write(@"flat\a.safetensors");
        Write(@"flat\b.safetensors");
        var executor = new LoraSortExecutor(new ExplodeOnSecondCreateDirectory(),
            new RecordingPathUpdater(_dbChanges), new SortHistoryWriter(In("history")), logger: null);

        var act = () => executor.ExecuteAsync(Plan(isMove: true,
            Move(@"flat\a.safetensors", @"SDXL 1.0\Character\a.safetensors"),
            Move(@"flat\b.safetensors", @"SDXL 1.0\Character\b.safetensors")));

        await act.Should().ThrowAsync<InvalidOperationException>();
        _dbChanges.Should().ContainSingle(); // file a's repoint survived the blow-up
    }

    private sealed class ActionProgress(Action<(double Fraction, string Status)> onReport)
        : IProgress<(double Fraction, string Status)>
    {
        public void Report((double Fraction, string Status) value) => onReport(value);
    }

    /// <summary>Stands in for any fault outside the IOException/UnauthorizedAccessException filter.</summary>
    private sealed class ExplodeOnSecondCreateDirectory : IFileOperations
    {
        private readonly FileOperations _inner = new();
        private int _calls;

        public void CreateDirectory(string path)
        {
            if (++_calls > 1) throw new InvalidOperationException("boom");
            _inner.CreateDirectory(path);
        }

        public bool FileExists(string path) => _inner.FileExists(path);
        public void CopyFile(string s, string d, bool o) => _inner.CopyFile(s, d, o);
        public void MoveFile(string s, string d, bool o) => _inner.MoveFile(s, d, o);
        public string[] GetFiles(string directoryPath) => _inner.GetFiles(directoryPath);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
    }
}
