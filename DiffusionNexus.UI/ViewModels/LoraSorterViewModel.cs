using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services.IO;
using DiffusionNexus.UI.Services.Lora.Sorting;
using DiffusionNexus.UI.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// ViewModel for the LoRA Sorter view: picks a source folder, computes a live preview of
/// where every LoRA would land (spec §7.2), gates the run on free disk space, and executes
/// the move/copy via <see cref="LoraSortExecutor"/>.
/// </summary>
public partial class LoraSorterViewModel : BusyViewModelBase
{
    private const long SafetyMarginBytes = 1L << 30; // 1 GB

    private static readonly string[] ModelExtensions = [".safetensors", ".ckpt", ".pt", ".pth"];

    private readonly IAppSettingsService? _settingsService;
    private readonly IModelSyncService? _syncService;
    private readonly IUnifiedLogger? _logger;
    private readonly ILocalPathUpdater _pathUpdater;
    private readonly SorterMetadataResolver _metadataResolver;
    private readonly IFileOperations _fileOperations;
    private readonly Func<string, long> _getAvailableSpace;
    private readonly Func<string, string> _hashFile;
    private readonly Func<string, bool> _fileExistsOnDisk;
    private readonly string _historyDirectory;

    private LoraSortPlan? _lastPlan;
    private CancellationTokenSource? _sortCts;

    /// <summary>Raised after a sort run finishes so the parent (Installed tab) can refresh.</summary>
    public event EventHandler? SortCompleted;

    #region Constructors

    /// <summary>Design-time constructor with no services — required because
    /// <see cref="LoraViewerViewModel"/>'s design-time ctor must also build a sorter VM.</summary>
    public LoraSorterViewModel()
    {
        _settingsService = null;
        _syncService = null;
        _logger = null;
        _pathUpdater = new NullLocalPathUpdater();
        _metadataResolver = new SorterMetadataResolver(null, () => Task.FromResult<string?>(null),
            SorterMetadataResolver.DefaultCacheDirectory, ComputeSha256, logger: null);
        _fileOperations = new FileOperations();
        _getAvailableSpace = DiskUtility.GetAvailableSpace;
        _hashFile = ComputeSha256;
        _fileExistsOnDisk = File.Exists;
        _historyDirectory = SortHistoryWriter.DefaultHistoryDirectory;

        SourceFolders.Add(@"C:\Demo\Loras");
        SelectedSourceFolder = SourceFolders[0];
        PreviewRoots.Add(new SortPreviewNodeViewModel { Name = "SDXL 1.0", LoraCount = 12, TotalBytes = 4_200_000_000 });
        PreviewRoots.Add(new SortPreviewNodeViewModel { Name = "Illustrious", LoraCount = 5, TotalBytes = 1_100_000_000 });
        PreviewSummary = "✓ 17 files will move   ·   0 already in place   ·   ✎ 0 auto-renamed · 0 duplicates skipped";
        TransferCount = 17;
        HasEnoughSpace = true;
    }

    /// <summary>Runtime constructor — every I/O seam is injected for testability.</summary>
    public LoraSorterViewModel(
        IAppSettingsService? settingsService,
        IModelSyncService? syncService,
        IUnifiedLogger? logger,
        ILocalPathUpdater pathUpdater,
        SorterMetadataResolver metadataResolver,
        IFileOperations fileOperations,
        Func<string, long> getAvailableSpace,
        Func<string, string> hashFile,
        Func<string, bool> fileExistsOnDisk,
        string historyDirectory)
    {
        _settingsService = settingsService;
        _syncService = syncService;
        _logger = logger;
        _pathUpdater = pathUpdater ?? throw new ArgumentNullException(nameof(pathUpdater));
        _metadataResolver = metadataResolver ?? throw new ArgumentNullException(nameof(metadataResolver));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _getAvailableSpace = getAvailableSpace ?? throw new ArgumentNullException(nameof(getAvailableSpace));
        _hashFile = hashFile ?? throw new ArgumentNullException(nameof(hashFile));
        _fileExistsOnDisk = fileExistsOnDisk ?? throw new ArgumentNullException(nameof(fileExistsOnDisk));
        _historyDirectory = historyDirectory ?? throw new ArgumentNullException(nameof(historyDirectory));
    }

    #endregion

    #region Observable state

    public ObservableCollection<string> SourceFolders { get; } = [];

    [ObservableProperty]
    private string? _selectedSourceFolder;

    /// <summary>Overrides the sort destination; null means "Same as source".</summary>
    [ObservableProperty]
    private string? _customTargetFolder;

    [ObservableProperty]
    private bool _includeCategory = true;

    [ObservableProperty]
    private bool _isMove = true;

    [ObservableProperty]
    private bool _deleteEmptySourceFolders;

    public ObservableCollection<SortPreviewNodeViewModel> PreviewRoots { get; } = [];

    [ObservableProperty]
    private string? _previewSummary;

    [ObservableProperty]
    private string? _diskSummary;

    [ObservableProperty]
    private bool _hasEnoughSpace;

    [ObservableProperty]
    private string? _blockReason;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private int _transferCount;

    public string? EffectiveTargetRoot => string.IsNullOrWhiteSpace(CustomTargetFolder) ? SelectedSourceFolder : CustomTargetFolder;

    public bool CanStart => !IsBusy && HasEnoughSpace && TransferCount > 0 && EffectiveTargetRoot is not null;

    partial void OnIncludeCategoryChanged(bool value) => _ = RecomputePreviewAsync();

    partial void OnIsMoveChanged(bool value) => _ = RecomputePreviewAsync();

    partial void OnSelectedSourceFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTargetRoot));
        StartSortingCommand.NotifyCanExecuteChanged();
        _ = RecomputePreviewAsync();
    }

    partial void OnCustomTargetFolderChanged(string? value)
    {
        OnPropertyChanged(nameof(EffectiveTargetRoot));
        StartSortingCommand.NotifyCanExecuteChanged();
        _ = RecomputePreviewAsync();
    }

    partial void OnTransferCountChanged(int value) => StartSortingCommand.NotifyCanExecuteChanged();

    partial void OnHasEnoughSpaceChanged(bool value) => StartSortingCommand.NotifyCanExecuteChanged();

    #endregion

    #region Commands

    /// <summary>Loads the enabled LoRA sources (favorite preselected, else first) and computes the initial preview.
    /// Called from the view's <c>OnAttachedToVisualTree</c>.</summary>
    public async Task InitializeAsync()
    {
        if (_settingsService is not null)
        {
            var sources = await _settingsService.GetEnabledLoraSourcesAsync();
            SourceFolders.Clear();
            foreach (var source in sources)
                SourceFolders.Add(source);

            var favorite = await _settingsService.GetFavoriteLoraSourceAsync();
            SelectedSourceFolder = favorite is not null && SourceFolders.Contains(favorite, StringComparer.OrdinalIgnoreCase)
                ? favorite
                : SourceFolders.FirstOrDefault();
        }

        await RecomputePreviewAsync();
    }

    [RelayCommand]
    private async Task BrowseSourceAsync()
    {
        if (DialogService is null) return;

        var folder = await DialogService.ShowOpenFolderDialogAsync("Select folder to sort");
        if (folder is null) return;

        if (!SourceFolders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            SourceFolders.Add(folder);

        SelectedSourceFolder = folder;
    }

    [RelayCommand]
    private async Task BrowseTargetAsync()
    {
        if (DialogService is null) return;

        var folder = await DialogService.ShowOpenFolderDialogAsync("Select target folder");
        if (folder is null) return;

        CustomTargetFolder = folder;
    }

    [RelayCommand]
    private void ClearTargetOverride() => CustomTargetFolder = null;

    [RelayCommand]
    private async Task RecomputePreviewAsync()
    {
        if (SelectedSourceFolder is null)
        {
            PreviewRoots.Clear();
            PreviewSummary = null;
            DiskSummary = null;
            HasEnoughSpace = false;
            BlockReason = null;
            StatusMessage = null;
            TransferCount = 0;
            return;
        }

        await RunBusyAsync(RecomputePreviewCoreAsync, "Computing preview…");
    }

    private async Task RecomputePreviewCoreAsync()
    {
        var sourceFolder = SelectedSourceFolder!;
        var targetRoot = EffectiveTargetRoot!;
        var ct = CancellationToken.None;

        var cached = _syncService is null
            ? Array.Empty<InstalledModelFile>()
            : await _syncService.LoadCachedFilesAsync(ct);

        var candidates = new List<SortCandidate>();
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in cached)
        {
            var path = f.File.LocalPath;
            if (string.IsNullOrEmpty(path)) continue;
            if (!path.StartsWith(sourceFolder, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_fileExistsOnDisk(path)) continue;

            knownPaths.Add(path);
            var category = SorterCategoryResolver.ToFolderName(SorterCategoryResolver.ResolveForModel(f.Model));
            var sizeBytes = f.File.FileSizeBytes ?? new FileInfo(path).Length;
            candidates.Add(new SortCandidate(path, f.Version.BaseModelRaw, category,
                f.Version.CivitaiId, f.File.HashSHA256, sizeBytes, SidecarLocator.FindSidecars(path)));
        }

        if (Directory.Exists(sourceFolder))
        {
            var unknownFiles = Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories)
                .Where(p => ModelExtensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
                .Where(p => !knownPaths.Contains(p))
                .ToList();

            for (var i = 0; i < unknownFiles.Count; i++)
            {
                var path = unknownFiles[i];
                BusyMessage = $"Resolving metadata {i + 1}/{unknownFiles.Count}…";
                var metadata = await _metadataResolver.ResolveAsync(path, ct);
                var sizeBytes = new FileInfo(path).Length;
                candidates.Add(new SortCandidate(path, metadata.BaseModelRaw,
                    SorterCategoryResolver.ToFolderName(CivitaiCategory.Unknown),
                    metadata.CivitaiVersionId, metadata.Sha256, sizeBytes, SidecarLocator.FindSidecars(path)));
            }
        }

        var plan = new LoraSortPlanner(_hashFile, _fileExistsOnDisk).BuildPlan(candidates, BuildOptions());
        _lastPlan = plan;

        BuildPreviewTree(plan, targetRoot);

        TransferCount = plan.TransferCount;
        PreviewSummary = $"✓ {plan.TransferCount} files will {(IsMove ? "move" : "copy")}   ·   {plan.AlreadyInPlaceCount} already in place   ·   ✎ {plan.RenamedCount} auto-renamed · {plan.SkippedDuplicateCount} duplicates skipped";

        var free = _getAvailableSpace(targetRoot);
        HasEnoughSpace = free >= plan.RequiredBytes + SafetyMarginBytes;
        DiskSummary = $"{SortPreviewNodeViewModel.FormatBytes(plan.RequiredBytes)} required · {SortPreviewNodeViewModel.FormatBytes(free)} free";
        BlockReason = HasEnoughSpace ? null : "Not enough free space on the target drive.";
        StatusMessage = null;

        if (!IsMove && string.Equals(targetRoot, sourceFolder, StringComparison.OrdinalIgnoreCase))
        {
            HasEnoughSpace = false;
            BlockReason = "Copy into the source folder would duplicate every file — pick a different target.";
        }
        else if (SourceFolders.Any(s => !string.Equals(s, sourceFolder, StringComparison.OrdinalIgnoreCase) && IsWithin(targetRoot, s)))
        {
            StatusMessage = "⚠ Target is another LoRA source — colliding sources can lead to unpredictable outcomes (duplicate imports on the next scan).";
        }

        _logger?.Info(LogCategory.FileSystem, "LoraSorter",
            $"Preview: {plan.TransferCount} transfers, {plan.RenamedCount} renames, {plan.SkippedDuplicateCount} duplicates");
    }

    [RelayCommand]
    private void CancelSort() => _sortCts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartSortingAsync()
    {
        var plan = _lastPlan;
        if (plan is null) return;

        var totalTransferBytes = plan.Moves.Where(m => m.Action == PlannedAction.Transfer).Sum(m => m.Candidate.FileSizeBytes);

        var confirmed = DialogService is null
            ? false
            : await DialogService.ShowConfirmAsync("Start sorting?",
                $"{plan.TransferCount} files will be {(IsMove ? "moved" : "copied")} into {EffectiveTargetRoot}.\n" +
                $"{plan.RenamedCount} will be renamed, {plan.SkippedDuplicateCount} duplicates skipped.\n" +
                $"Total {SortPreviewNodeViewModel.FormatBytes(totalTransferBytes)}.");

        if (!confirmed) return;

        _sortCts = new CancellationTokenSource();
        try
        {
            await RunBusyAsync(() => ExecuteSortAsync(plan), "Sorting LoRAs…");
        }
        finally
        {
            _sortCts?.Dispose();
            _sortCts = null;
        }

        await RecomputePreviewAsync();
        SortCompleted?.Invoke(this, EventArgs.Empty);
    }

    private async Task ExecuteSortAsync(LoraSortPlan plan)
    {
        var taskTracker = App.Services?.GetService<ITaskTracker>();
        using var taskHandle = taskTracker?.BeginTask("Sorting LoRAs", LogCategory.FileSystem);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _sortCts!.Token, taskHandle?.CancellationToken ?? CancellationToken.None);

        var executor = new LoraSortExecutor(_fileOperations, _pathUpdater, new SortHistoryWriter(_historyDirectory), _logger);
        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            BusyMessage = $"{p.Status} ({(int)(p.Fraction * 100)}%)";
            taskHandle?.ReportProgress(p.Fraction, p.Status);
        });

        LoraSortResult result;
        try
        {
            result = await executor.ExecuteAsync(plan, progress, linkedCts.Token);
            taskHandle?.Complete($"{result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed.");
        }
        catch (Exception ex)
        {
            taskHandle?.Fail(ex, "Sort failed.");
            throw;
        }

        if (IsMove && DeleteEmptySourceFolders && !result.Cancelled)
        {
            await new DiskUtility().DeleteEmptyDirectoriesAsync(SelectedSourceFolder!, CancellationToken.None);
        }

        StatusMessage = result.Cancelled
            ? $"Cancelled — {result.Moved + result.Copied} done, rest untouched."
            : $"Done: {result.Moved + result.Copied} sorted, {result.Skipped} duplicates skipped, {result.Failed} failed.";
    }

    #endregion

    #region Helpers

    private LoraSortOptions BuildOptions() =>
        new(SelectedSourceFolder!, EffectiveTargetRoot!, IncludeCategory, IsMove, DeleteEmptySourceFolders);

    /// <summary>Walks each non-skipped move's TargetDirectory, strips the target-root prefix, and
    /// materializes nested folder nodes with rolled-up counts/sizes. Root nodes (first-level
    /// folders) are sorted by TotalBytes descending.</summary>
    private void BuildPreviewTree(LoraSortPlan plan, string targetRoot)
    {
        PreviewRoots.Clear();

        foreach (var move in plan.Moves)
        {
            if (move.Action == PlannedAction.SkippedDuplicate) continue;

            var relative = Path.GetRelativePath(targetRoot, move.TargetDirectory);
            var segments = relative == "."
                ? Array.Empty<string>()
                : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

            var siblings = PreviewRoots;
            var chain = new List<SortPreviewNodeViewModel>();
            foreach (var segment in segments)
            {
                var node = GetOrCreateFolder(siblings, segment);
                chain.Add(node);
                siblings = node.Children;
            }

            var fileNode = new SortPreviewNodeViewModel
            {
                Name = Path.GetFileName(move.TargetFilePath),
                IsFile = true,
                TotalBytes = move.Candidate.FileSizeBytes,
                LoraCount = 1,
                IsAlreadyInPlace = move.Action == PlannedAction.AlreadyInPlace,
                IsRenamed = move.WasRenamed
            };
            siblings.Add(fileNode);

            foreach (var ancestor in chain)
            {
                ancestor.LoraCount += 1;
                ancestor.TotalBytes += move.Candidate.FileSizeBytes;
            }
        }

        var sortedRoots = PreviewRoots.OrderByDescending(n => n.TotalBytes).ToList();
        PreviewRoots.Clear();
        foreach (var root in sortedRoots)
            PreviewRoots.Add(root);
    }

    private static SortPreviewNodeViewModel GetOrCreateFolder(ObservableCollection<SortPreviewNodeViewModel> siblings, string name)
    {
        var existing = siblings.FirstOrDefault(n => !n.IsFile && string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var node = new SortPreviewNodeViewModel { Name = name, IsFile = false };
        siblings.Add(node);
        return node;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="root"/> itself or nested beneath it.</summary>
    private static bool IsWithin(string path, string root)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && (normalizedPath.Length == normalizedRoot.Length || normalizedPath[normalizedRoot.Length] == Path.DirectorySeparatorChar);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>No-op path updater for the design-time constructor, which never touches the DB.</summary>
    private sealed class NullLocalPathUpdater : ILocalPathUpdater
    {
        public Task UpdateLocalPathsAsync(IReadOnlyList<(string OldPath, string NewPath)> changes, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    #endregion
}
