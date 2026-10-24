using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Lora.Sorting;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>
/// One file line in the sorter's duplicate list and run report (#651): the file, optionally the
/// path it is paired with — the identical copy it matched, or where it went — and a detail line
/// such as a failure reason. Both paths open in Explorer from the row's context menu.
/// </summary>
public sealed partial class SortReportFileRowViewModel
{
    private const string LogSource = "LoraSorter";

    private readonly IProcessLauncher _launcher;
    private readonly IUnifiedLogger? _logger;

    /// <param name="relatedPrefix">Reads before <paramref name="relatedPath"/>: "≡ identical to", "→".</param>
    public SortReportFileRowViewModel(string path, string? relatedPath, string? relatedPrefix, string? detail,
        long sizeBytes, IProcessLauncher launcher, IUnifiedLogger? logger)
    {
        Path = path;
        RelatedPath = relatedPath;
        RelatedText = relatedPath is null ? null : $"{relatedPrefix} {relatedPath}";
        Detail = detail;
        SizeText = FileSizeFormatter.Format(sizeBytes);
        _launcher = launcher;
        _logger = logger;
    }

    public string Path { get; }
    public string? RelatedPath { get; }
    public string? RelatedText { get; }
    public bool HasRelated => RelatedPath is not null;
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public string SizeText { get; }

    /// <summary>Context-menu label for <see cref="OpenRelatedInFolderCommand"/>.</summary>
    public string RelatedMenuHeader { get; init; } = "Open new location in folder";

    [RelayCommand]
    private void OpenInFolder() => Open(Path);

    [RelayCommand]
    private void OpenRelatedInFolder()
    {
        if (RelatedPath is not null) Open(RelatedPath);
    }

    /// <summary>
    /// Selects the file when it is still there; otherwise opens the folder it was in — after a
    /// move the source path is gone, but its folder usually is not — and does nothing when that is
    /// gone too. A broken shell association is logged, never thrown into the dialog.
    /// </summary>
    private void Open(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                _launcher.OpenFolderAndSelectFile(path);
                return;
            }

            var folder = System.IO.Path.GetDirectoryName(path);
            if (folder is not null && Directory.Exists(folder))
                _launcher.OpenFolder(folder);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not open '{path}' in the file manager: {ex.Message}");
        }
    }

    /// <summary>The plan's skipped duplicates, each paired with the copy it matched.</summary>
    /// <param name="detailFor">Optional note for a row, keyed by the copy it matched.</param>
    internal static IReadOnlyList<SortReportFileRowViewModel> Duplicates(
        LoraSortPlan plan, IProcessLauncher launcher, IUnifiedLogger? logger, Func<string, string?>? detailFor = null)
        => plan.Moves
            .Where(m => m.Action == PlannedAction.SkippedDuplicate)
            .Select(m => new SortReportFileRowViewModel(m.Candidate.FilePath, m.TargetFilePath, "≡ identical to",
                detailFor?.Invoke(m.TargetFilePath), m.Candidate.FileSizeBytes, launcher, logger)
            {
                RelatedMenuHeader = "Open identical copy in folder"
            })
            .ToList();

    /// <summary>
    /// Shared by the preview's duplicate dialog and the report's duplicate section, so the two
    /// cannot explain the same list differently.
    /// </summary>
    internal static string DuplicatesNote(bool isMove)
        => "These files are identical (same SHA-256 hash) to a file in the destination, so they are not "
           + (isMove ? "moved. They stay where they are." : "copied.");
}

/// <summary>
/// The list behind the preview's "N duplicates skipped" link (#651): which files the sort will
/// skip and which identical copy each one matched.
/// </summary>
public sealed class SortDuplicatesDialogViewModel
{
    public SortDuplicatesDialogViewModel(LoraSortPlan plan, IProcessLauncher launcher, IUnifiedLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        Rows = SortReportFileRowViewModel.Duplicates(plan, launcher, logger);
        Title = $"Duplicates that will be skipped ({Rows.Count})";
        Note = SortReportFileRowViewModel.DuplicatesNote(plan.IsMove);
    }

    public IReadOnlyList<SortReportFileRowViewModel> Rows { get; }
    public string Title { get; }
    public string Note { get; }
}

/// <summary>
/// Read-only projection of a finished (or cancelled) sort run for the post-run report dialog
/// (#651). Built from the plan that ran plus the executor's result, never from live view state:
/// the options may already have changed by the time the dialog opens.
/// </summary>
public sealed partial class SortReportDialogViewModel
{
    private const string LogSource = "LoraSorter";

    private readonly string _targetRoot;
    private readonly IProcessLauncher _launcher;
    private readonly IUnifiedLogger? _logger;

    public SortReportDialogViewModel(LoraSortPlan plan, LoraSortResult result, IProcessLauncher launcher,
        IUnifiedLogger? logger)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(result);
        _targetRoot = plan.TargetRoot;
        _launcher = launcher;
        _logger = logger;

        var operation = plan.IsMove ? "move" : "copy";
        Title = $"Sort report: {operation}, {plan.SourceRoot} → {plan.TargetRoot}";

        // A failed file was reached too, so it counts toward "after X"; the rest were never touched.
        var reached = result.Transferred.Count + result.Failures.Count;
        IsCancelled = result.Cancelled;
        var remaining = Math.Max(0, plan.TransferCount - reached);
        // A cancel that lands in the DB flush after the last transfer still flags the run cancelled.
        CancelledText = remaining == 0
            ? "Cancelled after every file had been reached. None were left untouched."
            : $"Cancelled after {reached} of {Files(plan.TransferCount)}. "
              + (remaining == 1 ? "The remaining file was not touched." : $"The remaining {remaining} were not touched.");

        FailedRows = result.Failures
            .Select(f => new SortReportFileRowViewModel(f.Move.Candidate.FilePath, null, null, f.Reason,
                f.Move.Candidate.FileSizeBytes, launcher, logger))
            .ToList();
        // A duplicate can match another file of this plan rather than one already in the
        // destination; when that file never arrived, the copy the row names does not exist.
        var transferredTargets = result.Transferred.Select(m => m.TargetFilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingTargets = plan.Moves
            .Where(m => m.Action == PlannedAction.Transfer && !transferredTargets.Contains(m.TargetFilePath))
            .Select(m => m.TargetFilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DuplicateRows = SortReportFileRowViewModel.Duplicates(plan, launcher, logger,
            target => missingTargets.Contains(target)
                ? "Its identical copy was not created: that file's transfer failed or did not run."
                : null);
        DuplicatesNote = SortReportFileRowViewModel.DuplicatesNote(plan.IsMove);
        RenamedRows = result.Transferred
            .Where(m => m.WasRenamed)
            .Select(m => Row(m, "→ renamed to"))
            .ToList();
        TransferredRows = result.Transferred.Select(m => Row(m, "→")).ToList();

        TransferredCount = TransferredRows.Count;
        TransferredLabel = plan.IsMove ? "moved" : "copied";
        RenamedCount = RenamedRows.Count;
        DuplicateCount = DuplicateRows.Count;
        AlreadyInPlaceCount = plan.AlreadyInPlaceCount;
        FailedCount = FailedRows.Count;
        DurationText = SyncCopy.FormatElapsed(result.Elapsed);

        FailedHeader = $"Failed ({FailedCount})";
        DuplicatesHeader = $"Duplicates skipped ({DuplicateCount})";
        RenamedHeader = $"Renamed ({RenamedCount})";
        TransferredHeader = $"{(plan.IsMove ? "Moved" : "Copied")} ({TransferredCount})";

        SortReportFileRowViewModel Row(PlannedMove m, string prefix)
            => new(m.Candidate.FilePath, m.TargetFilePath, prefix, null, m.Candidate.FileSizeBytes, launcher, logger);
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    public string Title { get; }
    public bool IsCancelled { get; }
    public string CancelledText { get; }

    public int TransferredCount { get; }
    public string TransferredLabel { get; }
    public int RenamedCount { get; }
    public int DuplicateCount { get; }
    public int AlreadyInPlaceCount { get; }
    public int FailedCount { get; }
    public string DurationText { get; }

    public IReadOnlyList<SortReportFileRowViewModel> FailedRows { get; }
    public IReadOnlyList<SortReportFileRowViewModel> DuplicateRows { get; }
    public string DuplicatesNote { get; }
    public IReadOnlyList<SortReportFileRowViewModel> RenamedRows { get; }
    public IReadOnlyList<SortReportFileRowViewModel> TransferredRows { get; }

    public string FailedHeader { get; }
    public string DuplicatesHeader { get; }
    public string RenamedHeader { get; }
    public string TransferredHeader { get; }

    public bool HasFailures => FailedRows.Count > 0;
    public bool HasDuplicates => DuplicateRows.Count > 0;
    public bool HasRenamed => RenamedRows.Count > 0;
    public bool HasTransferred => TransferredRows.Count > 0;

    [RelayCommand]
    private void OpenTargetFolder()
    {
        try
        {
            if (Directory.Exists(_targetRoot))
                _launcher.OpenFolder(_targetRoot);
        }
        catch (Exception ex)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource,
                $"Could not open '{_targetRoot}' in the file manager: {ex.Message}");
        }
    }
}
