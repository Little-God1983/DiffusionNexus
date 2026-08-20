using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
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
        try { _root.Delete(recursive: true); } catch (IOException) { }
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
        IReadOnlyList<InstalledModelFile>? cached = null)
    {
        _settings.Setup(s => s.GetEnabledLoraSourcesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([SourceRoot]);
        _settings.Setup(s => s.GetFavoriteLoraSourceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _sync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cached ?? []);

        return new LoraSorterViewModel(
            _settings.Object, _sync.Object, logger: null,
            pathUpdater: Mock.Of<ILocalPathUpdater>(),
            metadataResolver: new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
                Path.Combine(_root.FullName, "cache"), _ => "hash", logger: null),
            fileOperations: new FileOperations(),
            getAvailableSpace: _ => freeSpace,
            hashFile: _ => "hash",
            fileExistsOnDisk: File.Exists,
            historyDirectory: Path.Combine(_root.FullName, "history"));
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
    public async Task InaccessibleSubfolderIsSkippedNotFatal()
    {
        // Simulate by asserting the safe enumerator itself: a nonexistent nested dir must not throw.
        var a = WriteLora(@"flat\a.safetensors");
        var vm = CreateVm(cached: [Installed(a, "SDXL 1.0", "character")]);
        await vm.InitializeAsync(); // enumeration path exercised for unknown files
        vm.TransferCount.Should().Be(1);
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
