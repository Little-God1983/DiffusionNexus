using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Sorter;

public sealed class LoraSorterViewModelTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-sortervm-");
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IModelSyncService> _sync = new();

    public void Dispose()
    {
        // UnauthorizedAccessException also shows up here: recursive delete of a tree containing a
        // directory junction hits the reparse point itself.
        try { _root.Delete(recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private string SourceRoot => Path.Combine(_root.FullName, "Loras");

    private string WriteLora(string relative)
    {
        var path = Path.Combine(SourceRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "weights");
        return path;
    }

    private static InstalledModelFile Installed(string path, string baseModel, string tag)
    {
        var model = new Model { Tags = { new ModelTag { Tag = new Tag { Name = tag } } } };
        var version = new ModelVersion { BaseModelRaw = baseModel };
        var file = new ModelFile { LocalPath = path };
        return new InstalledModelFile(model, version, file, Path.GetDirectoryName(path)!);
    }

    private LoraSorterViewModel CreateVm(long freeSpace = long.MaxValue,
        IReadOnlyList<InstalledModelFile>? cached = null,
        Func<string, long>? getAvailableSpace = null,
        Func<string, bool>? fileExistsOnDisk = null,
        Func<string, string>? resolverHash = null,
        Func<string, CancellationToken, Task>? deleteEmptyDirectories = null,
        Func<CancellationToken, Task<IReadOnlyList<InstalledModelFile>>>? loadCachedFiles = null,
        IUnifiedLogger? logger = null)
    {
        _settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([SourceRoot]);
        _settings.Setup(s => s.GetFavoriteLoraSourceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached ?? []);

        return new LoraSorterViewModel(
            _settings.Object, _sync.Object, logger,
            pathUpdater: Mock.Of<ILocalPathUpdater>(),
            metadataResolver: new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
                Path.Combine(_root.FullName, "cache"), resolverHash ?? (_ => "hash"), logger: null),
            fileOperations: new FileOperations(),
            getAvailableSpace: getAvailableSpace ?? (_ => freeSpace),
            hashFile: _ => "hash",
            fileExistsOnDisk: fileExistsOnDisk ?? File.Exists,
            historyDirectory: Path.Combine(_root.FullName, "history"),
            deleteEmptyDirectories: deleteEmptyDirectories,
            loadCachedFiles: loadCachedFiles);
    }

    [Fact]
    public async Task PreviewGroupsCachedFilesByBaseModelAndCategory()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var b = WriteLora(@"flat\b.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "Illustrious", "style"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(2);
        var rootNames = vm.PreviewRoots.Select(n => n.Name);
        rootNames.Should().Contain(["SDXL 1.0", "Illustrious"]);
        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Select(c => c.Name).Should().Contain("Character");
    }

    [Fact]
    public async Task BaseModelOnlyModeFlattensCategoryLevel()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.PreviewRoots.First(n => n.Name == "SDXL 1.0")
            .Children.Where(c => !c.IsFile).Should().BeEmpty();
    }

    [Fact]
    public async Task InsufficientDiskSpaceBlocksStartWithReason()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(freeSpace: 0, cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.IsMove = false; // copy → RequiredBytes > 0, and 0 free < margin
        vm.CustomTargetFolder = Path.Combine(_root.FullName, "Elsewhere");

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().NotBeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task CopyIntoSourceRootIsBlocked()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        vm.IsMove = false; // target still "same as source"
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
        vm.BlockReason.Should().Contain("source");
    }

    [Fact]
    public async Task UnknownFileInBrowsedFolderIsResolvedIntoUnknownBuckets()
    {
        WriteLora(@"flat\mystery.safetensors"); // no DB row, no sidecar, no client → Unknown
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.PreviewRoots.Single().Name.Should().Be("Unknown");
    }

    [Fact]
    public async Task UnreadableDbKnownFileIsSkippedNotFatalAndIsReported()
    {
        // FileSizeBytes is null on this row, so the size comes from new FileInfo(path).Length —
        // which throws FileNotFoundException for a path that is gone. One bad file out of 3000
        // used to abort the whole preview with zero candidates.
        var good = WriteLora(@"flat\good.safetensors");
        var vanished = Path.Combine(SourceRoot, "flat", "gone.safetensors");
        var vm = CreateVm(
            cached: [Installed(good, "SDXL 1.0", "character"), Installed(vanished, "SDXL 1.0", "character")],
            // The row survives the existence check; the file itself does not. Scoped to that one
            // path so the planner's real collision probing is untouched.
            fileExistsOnDisk: p => string.Equals(p, vanished, StringComparison.OrdinalIgnoreCase) || File.Exists(p));

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
        FlattenNames(vm.PreviewRoots).Should().NotContain("gone.safetensors");
    }

    [Fact]
    public async Task UnreadableBrowsedFileIsSkippedNotFatalAndIsReported()
    {
        // The resolver hashes the file first; deleting it there is a deterministic stand-in for the
        // enumerate-then-vanish race, and makes the following new FileInfo(path).Length throw.
        var doomed = WriteLora(@"flat\doomed.safetensors");
        WriteLora(@"flat\fine.safetensors");
        var vm = CreateVm(cached: [], resolverHash: p =>
        {
            if (string.Equals(p, doomed, StringComparison.OrdinalIgnoreCase)) File.Delete(p);
            return "hash";
        });

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
        FlattenNames(vm.PreviewRoots).Should().NotContain("doomed.safetensors");
    }

    [Fact]
    public async Task WhitespaceOnlyLocalPathIsSkippedNotFatalAndIsReported()
    {
        // Path.GetFullPath (called inside the IsWithin boundary check) throws ArgumentException
        // for a whitespace-only string. A blank/malformed ModelFile.LocalPath row used to abort
        // the whole pass with zero candidates because the boundary check ran before the per-file
        // try/catch could absorb it.
        var good = WriteLora(@"flat\good.safetensors");
        var vm = CreateVm(cached:
        [
            Installed(good, "SDXL 1.0", "character"),
            Installed("   ", "SDXL 1.0", "character"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
    }

    [Fact]
    public async Task TheSkippedFileNoteSurvivesAnOptionToggle()
    {
        var good = WriteLora(@"flat\good.safetensors");
        var vanished = Path.Combine(SourceRoot, "flat", "gone.safetensors");
        var vm = CreateVm(
            cached: [Installed(good, "SDXL 1.0", "character"), Installed(vanished, "SDXL 1.0", "character")],
            fileExistsOnDisk: p => string.Equals(p, vanished, StringComparison.OrdinalIgnoreCase) || File.Exists(p));
        await vm.InitializeAsync();

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        // The toggle re-plans from the cache without re-resolving, so the note must come with it.
        vm.StatusMessage.Should().Contain("1 file(s) skipped");
    }

    [Fact]
    public async Task BrowsedFolderFileWithSidecarTagsLandsInItsCategoryFolder()
    {
        // LoadCachedFilesAsync hard-filters to enabled LoRA source roots, so a browsed folder gets
        // no DB metadata at all — this is the NORMAL path for the "Browse any folder" feature. The
        // category used to be hardcoded to Unknown, dumping a fully resolved library into
        // <Target>\SDXL 1.0\Unknown\ instead of \Character\.
        var lora = WriteLora(@"flat\hero.safetensors");
        File.WriteAllText(Path.ChangeExtension(lora, null) + ".civitai.info",
            """{"baseModel":"SDXL 1.0","id":4242,"model":{"tags":["character"]}}""");
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        var root = vm.PreviewRoots.Should().ContainSingle().Subject;
        root.Name.Should().Be("SDXL 1.0");
        root.Children.Should().ContainSingle(c => !c.IsFile).Which.Name.Should().Be("Character");
    }

    [Fact]
    public async Task SiblingFolderSharingPrefixIsNotSwept()
    {
        // Source "...\Loras" must not sweep "...\Loras_backup" — a bare StartsWith would match
        // the shared name prefix even though the sibling folder is a different location.
        var a = WriteLora(@"flat\a.safetensors");
        var siblingDir = Path.Combine(_root.FullName, "Loras_backup");
        Directory.CreateDirectory(siblingDir);
        var b = Path.Combine(siblingDir, "b.safetensors");
        File.WriteAllText(b, "weights");

        var vm = CreateVm(cached:
        [
            Installed(a, "SDXL 1.0", "character"),
            Installed(b, "SDXL 1.0", "character"),
        ]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        FlattenNames(vm.PreviewRoots).Should().NotContain("b.safetensors");
    }

    [Fact]
    public async Task RunResultMessageSurvivesPostRunRecompute()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.DialogService = ConfirmingDialogService();

        await vm.InitializeAsync();

        var sortCompleted = false;
        vm.SortCompleted += (_, _) => sortCompleted = true;

        await vm.StartSortingCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().Contain("Done");
        sortCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task AnUndeletableEmptyFolderDoesNotTurnASuccessfulSortIntoAFailure()
    {
        // DiskUtility.DeleteEmptyDirectories calls Directory.Delete with no guard, and it runs
        // AFTER every file has been transferred and taskHandle.Complete() has reported success. An
        // Explorer window or AV holding one now-empty folder used to unwind into StartSortingAsync's
        // catch: no post-run recompute, SortCompleted never fired, and the user saw
        // "Sorting failed: Access to the path … is denied" over a preview listing dead paths.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            deleteEmptyDirectories: (_, _) => throw new UnauthorizedAccessException("Access to the path 'flat' is denied."));
        vm.DialogService = ConfirmingDialogService();
        await vm.InitializeAsync();
        vm.DeleteEmptySourceFolders = true;

        var sortCompleted = false;
        vm.SortCompleted += (_, _) => sortCompleted = true;

        await vm.StartSortingCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().StartWith("Done:");
        vm.StatusMessage.Should().Contain("some empty folders could not be removed");
        sortCompleted.Should().BeTrue();
        // The post-run recompute ran: the tree was rebuilt from disk, where the DB's old path no
        // longer exists, so the "SDXL 1.0" bucket the pre-run preview showed is gone.
        vm.PreviewRoots.Select(n => n.Name).Should().NotContain("SDXL 1.0");
    }

    [Fact]
    public async Task ChangingAnOptionClearsTheRunResultBanner()
    {
        // After a completed run the Done-banner shows; the next user action clears it.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        vm.DialogService = ConfirmingDialogService();
        await vm.InitializeAsync();
        await vm.StartSortingCommand.ExecuteAsync(null);
        vm.StatusMessage.Should().Contain("Done");

        vm.IncludeCategory = !vm.IncludeCategory;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().BeNull();
    }

    private static IDialogService ConfirmingDialogService()
    {
        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);
        return dialog.Object;
    }

    [Fact]
    public async Task OptionToggleDoesNotReEnumerateDisk()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        var before = vm.TransferCount;
        before.Should().BeGreaterThan(0);

        // If the option toggle re-walked the disk, this deleted file would drop out of the
        // DB-known candidate set (fileExistsOnDisk check) and TransferCount would fall.
        File.Delete(a);

        vm.IncludeCategory = false;
        await vm.RecomputePreviewCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(before);
    }

    [Fact]
    public async Task RefreshRebuildsCandidatesFromDisk()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        vm.TransferCount.Should().Be(1);

        File.Delete(a);
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(0);
    }

    [Fact]
    public async Task RefreshClearsACancelledPreviewBanner()
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();

        // Simulate the post-cancel state the Cancel path leaves behind.
        vm.CancelSortCommand.Execute(null);
        vm.StatusMessage = "Cancelled — preview not updated.";

        await vm.RefreshCommand.ExecuteAsync(null);

        vm.StatusMessage.Should().BeNull();
        vm.TransferCount.Should().Be(1); // preview genuinely rebuilt
    }

    [Fact]
    public async Task PreviewFailureIsSurfacedNotSwallowed()
    {
        var vm = CreateVm(cached: []);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));

        await vm.InitializeAsync(); // must not throw

        vm.StatusMessage.Should().Contain("Preview failed");
    }

    [Fact]
    public async Task AnInjectedLoadCachedFilesDelegateIsUsedInsteadOfTheSharedSyncService()
    {
        // Production passes a delegate that opens a fresh DI scope per call, so the sorter never
        // touches the session-long DbContext the scoped LoraViewerViewModel holds. If the VM
        // silently fell back to the shared IModelSyncService, this would preview nothing.
        var a = WriteLora(@"flat\a.safetensors");
        var calls = 0;
        var vm = CreateVm(cached: [], loadCachedFiles: _ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<InstalledModelFile>>(
                [Installed(a, "SDXL 1.0", "character")]);
        });

        await vm.InitializeAsync();

        calls.Should().Be(1);
        _sync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Never);
        vm.PreviewRoots.Single().Name.Should().Be("SDXL 1.0");
    }

    [Fact]
    public async Task DesignTimeConstructorKeepsItsDemoDataAndStartsNoBackgroundWork()
    {
        // "SelectedSourceFolder = SourceFolders[0]" fired the recompute hook with _isInitializing
        // still false, so the design-time VM kicked off real disk and DriveInfo work against
        // C:\Demo\Loras; its continuation then cleared PreviewRoots and zeroed TransferCount,
        // showing an empty tree — the opposite of what the demo data is for. LoraViewerViewModel's
        // design-time ctor builds this VM too, so every "new LoraViewerViewModel()" in the suite
        // spawned filesystem I/O off the test thread with any exception unobserved.
        var vm = new LoraSorterViewModel();

        // Synchronous and deterministic: RunBusyAsync sets IsBusy before its first await, so a
        // still-false IsBusy on return from the ctor proves no pass was started at all.
        vm.IsBusy.Should().BeFalse();
        vm.TransferCount.Should().Be(17);
        vm.PreviewRoots.Should().HaveCount(2);

        await Task.Delay(250);

        vm.TransferCount.Should().Be(17);
        vm.PreviewRoots.Should().HaveCount(2);
        vm.PreviewSummary.Should().Contain("17 files will move");
    }

    [Fact]
    public async Task AFailedPassDisarmsThePlanFromTheSuccessfulOneBeforeIt()
    {
        // Preview S1 succeeds; the next pass throws. Start used to stay armed against S1's plan
        // while the confirm dialog interpolated the LIVE mode and target — "42 files will be copied
        // into S2" for a plan that moves files inside S1. ReferenceEquals(_lastPlan, plan) could not
        // catch it, because nothing had reassigned _lastPlan.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync();
        vm.TransferCount.Should().Be(1);
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();

        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("db down"));
        await vm.RefreshCommand.ExecuteAsync(null);

        vm.TransferCount.Should().Be(0);
        vm.HasEnoughSpace.Should().BeFalse();
        vm.PreviewRoots.Should().BeEmpty();
        vm.PreviewSummary.Should().BeNull();
        vm.BlockReason.Should().Contain("db down");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task ASupersededPreviewPassCommitsNothingAndTheOverlayOutlivesIt()
    {
        // Pass A (slow) is superseded by pass B, which finishes first. Two things used to break:
        // RunBusyAsync's shared IsBusy dropped the overlay when B finished — arming Start against
        // whatever A had left behind — and A, on resuming, painted its own stale result over B's.
        var a = WriteLora(@"flat\a.safetensors");
        var passAStarted = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var passes = 0;

        var vm = CreateVm(loadCachedFiles: async _ =>
        {
            if (Interlocked.Increment(ref passes) == 1)
            {
                passAStarted.SetResult();
                await releaseA.Task;
                return [Installed(a, "PassA", "character")];
            }
            return [Installed(a, "PassB", "character")];
        });

        var passA = vm.InitializeAsync();
        await passAStarted.Task;
        vm.IsBusy.Should().BeTrue();

        await vm.RefreshCommand.ExecuteAsync(null); // pass B, start to finish

        vm.IsBusy.Should().BeTrue("pass A is still in flight");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");

        releaseA.SetResult();
        await passA;

        vm.IsBusy.Should().BeFalse("the last pass has now left");
        // A must not repaint over B.
        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");
        vm.TransferCount.Should().Be(1);
    }

    [Fact]
    public async Task ASupersededPassThatRunsToSuccessStillCommitsNothing()
    {
        // The nastier half of the same bug: cancelling a pass's token does NOT stop a delegate
        // that is already running (Task.Run only refuses to *start* one), so a superseded pass
        // reaches the SUCCESS path with a complete, stale plan and used to overwrite _lastPlan,
        // the tree and the disk gate for good. Gated inside the planner's on-disk collision probe,
        // which only ever sees paths under the built target directory.
        var a = WriteLora(@"flat\a.safetensors");
        var planningA = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        var probes = 0;
        var passes = 0;

        var vm = CreateVm(
            fileExistsOnDisk: p =>
            {
                if (p.Contains("Character", StringComparison.OrdinalIgnoreCase)
                    && Interlocked.Increment(ref probes) == 1)
                {
                    planningA.SetResult();
                    releaseA.Task.GetAwaiter().GetResult();
                }
                return File.Exists(p);
            },
            loadCachedFiles: _ => Task.FromResult<IReadOnlyList<InstalledModelFile>>(
                Interlocked.Increment(ref passes) == 1
                    ? [Installed(a, "PassA", "character")]
                    : [Installed(a, "PassB", "character")]));

        var passA = vm.InitializeAsync();
        await planningA.Task;

        await vm.RefreshCommand.ExecuteAsync(null); // pass B finishes while A sits in the planner

        releaseA.SetResult();
        await passA; // A now runs to completion on the success path

        vm.PreviewRoots.Select(n => n.Name).Should().Equal("PassB");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task DeeplyNestedUnknownFilesAreEnumerated()
    {
        WriteLora(@"a\b\c\d\deep.safetensors");
        WriteLora(@"top.safetensors");
        var vm = CreateVm(cached: []);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(2);
    }

    [Fact]
    public async Task DirectoryJunctionsAreNotFollowed()
    {
        // The walk skips reparse points, which is what stops a junction pointing at itself or an
        // ancestor from growing the enumeration without bound (a probe confirmed the unguarded
        // options happily produce "real\loop\real\loop\real\..." forever). Asserted here against a
        // junction to a *sibling* folder so a regression fails the test instead of hanging it.
        var real = WriteLora(@"real\x.safetensors");
        var outside = Path.Combine(_root.FullName, "Outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "hidden.safetensors"), "weights");

        var link = Path.Combine(SourceRoot, "link");
        CreateJunction(link, outside);
        Directory.Exists(link).Should().BeTrue("mklink /J needs no elevation and CI is windows-latest");

        var vm = CreateVm(cached: []);
        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        FlattenNames(vm.PreviewRoots).Should().NotContain("hidden.safetensors");
        real.Should().NotBeNull();
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!;
        process.WaitForExit();
    }

    [Fact]
    public async Task UnknowableFreeSpaceOnAnExistingTargetFailsOpenInsteadOfBlockingTheRun()
    {
        // new DriveInfo(@"\nas\share\") throws ArgumentException and there is no free-space
        // number to give for a UNC target — but the folder is right there, so the run may proceed.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw new ArgumentException("Drive name must be a root directory."));

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeTrue();
        vm.DiskSummary.Should().Contain("unknown");
        vm.BlockReason.Should().BeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();
        vm.StatusMessage.Should().NotContain("Preview failed");
    }

    [Theory]
    // An unplugged Z:\ throws DriveNotFoundException (an IOException). Failing open on it armed
    // Start, and the executor then threw on CreateDirectory for every single file:
    // "Done: 0 sorted, 0 duplicates skipped, 412 failed." Unreachable is not unknowable.
    [InlineData(typeof(DriveNotFoundException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public async Task AnUnreachableTargetBlocksTheRunWithAStatedReason(Type exceptionType)
    {
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw (Exception)Activator.CreateInstance(exceptionType)!);

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().Contain("not reachable");
        vm.StartSortingCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task AMissingTargetFolderBlocksEvenWhenTheProbeIsUnanswerable()
    {
        // Same ArgumentException as the UNC case, but the target root does not exist — there is
        // nothing to sort into, so this must not inherit the UNC fail-open.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")],
            getAvailableSpace: _ => throw new ArgumentException("Drive name must be a root directory."));
        vm.CustomTargetFolder = Path.Combine(_root.FullName, "NoSuchTarget");

        await vm.InitializeAsync();

        vm.HasEnoughSpace.Should().BeFalse();
        vm.BlockReason.Should().Contain("not reachable");
    }

    [Fact]
    public async Task SameVolumeMoveIsNotBlockedByTheOneGigabyteMargin()
    {
        // A same-volume move is a directory-entry rename — RequiredBytes is 0, so the margin
        // must not apply. Otherwise the primary use case (reorganizing in place on the near-full
        // drive the library lives on) reports "Not enough free space".
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(freeSpace: 1_000, cached: [Installed(a, "SDXL 1.0", "character")]);

        await vm.InitializeAsync();

        vm.TransferCount.Should().Be(1);
        vm.HasEnoughSpace.Should().BeTrue();
        vm.BlockReason.Should().BeNull();
        vm.StartSortingCommand.CanExecute(null).Should().BeTrue();
    }

    [Theory]
    // A dedicated LoRA drive as the source: Path.TrimEndingDirectorySeparator deliberately does NOT
    // trim a root path, so the old boundary check read the 'L' of "E:\Loras" instead of a separator
    // and declared every file on the drive to be outside it.
    [InlineData(@"E:\Loras\a.safetensors", @"E:\", true)]
    [InlineData(@"E:\a.safetensors", @"E:\", true)]
    [InlineData(@"E:\", @"E:\", true)]
    // Prefix-sharing siblings still must not match.
    [InlineData(@"E:\Loras_backup\b.safetensors", @"E:\Loras", false)]
    [InlineData(@"E:\Loras\a.safetensors", @"E:\Loras", true)]
    // Trailing separators on either input are irrelevant.
    [InlineData(@"E:\Loras\a.safetensors", @"E:\Loras\", true)]
    [InlineData(@"E:\Loras", @"E:\Loras\", true)]
    [InlineData(@"E:\Loras\", @"E:\Loras", true)]
    [InlineData(@"E:\Other\a.safetensors", @"E:\Loras", false)]
    public void IsWithinHandlesDriveRootsAndPrefixSiblings(string path, string root, bool expected)
        => LoraSorterViewModel.IsWithin(path, root).Should().Be(expected);

    [Fact]
    public async Task PreviewAndRunReportTimedStepsToTheUnifiedConsole()
    {
        // Standing project rule: every step reports to the Unified Console WITH timings, so an
        // exported log can tell "slow" from "hung" by which step last succeeded. Before this the
        // slowest loop in the feature reported only into BusyMessage, and the branch had no
        // Stopwatch anywhere.
        var lines = new List<string>();
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(LogCategory.FileSystem, "LoraSorter", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string message, string? _) => lines.Add(message));

        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")], logger: logger.Object);
        vm.DialogService = ConfirmingDialogService();

        await vm.InitializeAsync();
        await vm.StartSortingCommand.ExecuteAsync(null);

        lines.Should().Contain(l => l.StartsWith("Resolving candidates under"));
        lines.Should().Contain(l => l.StartsWith("Candidate resolution finished:") && l.Contains(" ms"));
        lines.Should().Contain(l => l.StartsWith("Plan built for") && l.Contains(" ms:"));
        lines.Should().Contain(l => l.StartsWith("Sort started:"));
        lines.Should().Contain(l => l.StartsWith("Sort finished:") && l.Contains(" ms"));
    }

    private static IEnumerable<string> FlattenNames(IEnumerable<SortPreviewNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node.Name;
            foreach (var childName in FlattenNames(node.Children))
                yield return childName;
        }
    }
}
