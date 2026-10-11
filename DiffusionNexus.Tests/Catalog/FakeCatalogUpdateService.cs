using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Scriptable stand-in for the SDK's update service. HoldCheck / HoldApply keep a call in flight until the test releases it.</summary>
internal sealed class FakeCatalogUpdateService : ICatalogUpdateService
{
    public Func<CatalogUpdateCheck> NextCheck { get; set; } = () => CatalogChecks.Outcome(CatalogUpdateOutcome.UpToDate);
    public Func<CatalogChannel, CatalogUpdateCheck>? NextPreview { get; set; }
    public Func<CatalogApplyResult> NextApply { get; set; } = () => new CatalogApplyResult(CatalogSections.All, CatalogSections.None, null);
    public TaskCompletionSource? HoldCheck { get; set; }
    public TaskCompletionSource? HoldApply { get; set; }
    public Exception? ApplyFailure { get; set; }
    public Action? OnApply { get; set; }

    public int CheckCalls { get; private set; }
    public List<(CatalogChannel From, CatalogChannel To)> PreviewedSwitches { get; } = [];
    public int ApplyCalls { get; private set; }
    public CatalogUpdateCheck? AppliedCheck { get; private set; }
    public CatalogSections? AppliedSections { get; private set; }

    public async Task<CatalogUpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        CheckCalls++;
        if (HoldCheck is not null) await HoldCheck.Task;
        return NextCheck();
    }

    public Task<CatalogUpdateCheck> CheckAsync(CatalogChannel channel, CancellationToken ct = default) =>
        PreviewSwitchAsync(CatalogChannel.Stable, channel, ct);

    public async Task<CatalogUpdateCheck> PreviewSwitchAsync(CatalogChannel from, CatalogChannel to, CancellationToken ct = default)
    {
        PreviewedSwitches.Add((from, to));
        if (HoldCheck is not null) await HoldCheck.Task;
        return (NextPreview?.Invoke(to) ?? NextCheck() with { Channel = to }) with { IsSwitchPreview = true, FollowedChannel = from };
    }

    public async Task<CatalogApplyResult> ApplyAsync(CatalogUpdateCheck check, CatalogSections sections,
        IProgress<CatalogDownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ApplyCalls++;
        AppliedCheck = check;
        AppliedSections = sections;
        OnApply?.Invoke();
        if (HoldApply is not null) await HoldApply.Task.WaitAsync(ct);
        if (ApplyFailure is not null) throw ApplyFailure;
        return NextApply();
    }
}
