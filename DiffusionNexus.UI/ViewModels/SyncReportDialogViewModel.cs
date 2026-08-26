using DiffusionNexus.Domain.Services.Sync;

namespace DiffusionNexus.UI.ViewModels;

/// <summary>One step's outcome counts, ready to display in the report table.</summary>
public sealed class SyncReportStepRowViewModel
{
    public SyncReportStepRowViewModel(SyncStepReport step)
    {
        Label = SyncReport.Label(step.Kind);
        Planned = step.Planned;
        Processed = step.Processed;
        Succeeded = step.Succeeded;
        Skipped = step.Skipped;
        Failed = step.Failed;
        HasFailed = step.Failed > 0;
    }

    public string Label { get; }
    public int Planned { get; }
    public int Processed { get; }
    public int Succeeded { get; }
    public int Skipped { get; }
    public int Failed { get; }
    public bool HasFailed { get; }
}

/// <summary>One failed item within a <see cref="SyncReportFailureGroup"/>.</summary>
public sealed record SyncReportFailureItem(string Name, string Reason);

/// <summary>All failures for one step, with a header summarizing the count.</summary>
public sealed class SyncReportFailureGroup
{
    public SyncReportFailureGroup(SyncStepKind kind, IReadOnlyList<SyncReportFailureItem> items)
    {
        Kind = kind;
        Items = items;
        Header = $"{SyncReport.Label(kind)} — {items.Count} failed";
    }

    public SyncStepKind Kind { get; }
    public string Header { get; }
    public IReadOnlyList<SyncReportFailureItem> Items { get; }
}

/// <summary>
/// Read-only projection of a completed <see cref="SyncReport"/> for the post-run report dialog.
/// No observable state: the run is already finished by the time this is constructed.
/// </summary>
public sealed class SyncReportDialogViewModel
{
    public SyncReportDialogViewModel(SyncReport report, int newFilesDiscovered)
    {
        ArgumentNullException.ThrowIfNull(report);

        StepRows = report.Steps.Select(s => new SyncReportStepRowViewModel(s)).ToList();

        FailureGroups = report.Failures
            .GroupBy(f => f.Step)
            .OrderBy(g => IndexOf(report.Steps, g.Key))
            .Select(g => new SyncReportFailureGroup(
                g.Key,
                g.Select(f => new SyncReportFailureItem(f.Name, f.Reason)).ToList()))
            .ToList();
        HasFailures = FailureGroups.Count > 0;

        // An aborted run (#535) is partial in the same sense a cancelled one is — the completed
        // items are recorded — but the banner names the failure instead of claiming a Cancel.
        IsPartial = report.Cancelled || report.AbortReason is not null;
        PartialText = report.AbortReason is not null
            ? $"Aborted — {report.AbortReason}. Completed items are recorded and will not be redone."
            : "Cancelled — partial run. Completed items are recorded and will not be redone.";

        SummaryText = report.Summary;
        // The exact formatter, not the plan dialog's estimate one: this is a measurement, and a
        // tilde in front of it claims the run's own stopwatch was guessing.
        ElapsedText = SyncCopy.FormatElapsed(report.Elapsed);

        HasDiscoveredFiles = newFilesDiscovered > 0;
        DiscoveredText = SyncCopy.DescribeDiscovered(newFilesDiscovered);

        HasUnexpected = report.UnexpectedFailures > 0;
        UnexpectedText = report.UnexpectedFailures switch
        {
            <= 0 => "",
            1 => "1 item failed unexpectedly — see the log.",
            _ => $"{report.UnexpectedFailures} items failed unexpectedly — see the log.",
        };
    }

    public IReadOnlyList<SyncReportStepRowViewModel> StepRows { get; }
    public IReadOnlyList<SyncReportFailureGroup> FailureGroups { get; }
    public bool HasFailures { get; }
    public bool IsPartial { get; }
    public string PartialText { get; }
    public string SummaryText { get; }
    public string ElapsedText { get; }
    public string DiscoveredText { get; }
    public bool HasDiscoveredFiles { get; }
    public string UnexpectedText { get; }
    public bool HasUnexpected { get; }

    /// <summary>Report order for a step kind, so failure groups follow the same order as the step table.</summary>
    private static int IndexOf(IReadOnlyList<SyncStepReport> steps, SyncStepKind kind)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Kind == kind) return i;
        }

        return steps.Count;
    }
}
