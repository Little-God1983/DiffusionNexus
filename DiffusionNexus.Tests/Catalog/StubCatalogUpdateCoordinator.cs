using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;
using DiffusionNexus.UI.Services.Catalog;

namespace DiffusionNexus.Tests.Catalog;

/// <summary>Settable coordinator for view-model tests; the offer rule is the real one (CatalogUpdateOffer).</summary>
internal sealed class StubCatalogUpdateCoordinator : ICatalogUpdateCoordinator
{
    public CatalogChannel Channel { get; set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; set; } = CatalogChannelSource.Setting;
    public string? OverridePath { get; set; }
    public CatalogUpdatePhase Phase { get; set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public LocalCatalogState? Installed { get; set; }
    public CatalogDownloadProgress? Progress { get; set; }
    public CatalogApplyResult? LastApply { get; set; }
    public bool LastApplyAutomatic { get; set; }
    public bool AutoApply { get; set; }
    public string? InstallRunning { get; set; }
    public CatalogChannelSwitch? PendingSwitch { get; set; }

    public bool LastApplyFailed => LastApply is { } r && (r.Error is not null || r.Failed != CatalogSections.None);
    public bool UpdateAvailable => CatalogUpdateOffer.Offered(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed,
        Installed is { } s && (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } c && c != Channel);
    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && InstallRunning is null;
    public string? ApplyBlockedReason => UpdateAvailable && InstallRunning is { } n ? $"It can be applied once {n} has finished." : null;
    public string? ChannelChangeBlockedReason => InstallRunning is { } n ? $"The channel can be changed once {n} has finished." : null;

    public event Action? Changed;
    public void RaiseChanged() => Changed?.Invoke();

    public int Checks { get; private set; }
    public int Applies { get; private set; }
    public List<CatalogChannel> Switches { get; } = [];
    public int Confirms { get; private set; }
    public int Keeps { get; private set; }
    public List<bool> AutoApplySet { get; } = [];
    public bool RefuseAutoApply { get; set; }
    public Exception? SwitchFailure { get; set; }
    public Exception? AutoApplyFailure { get; set; }
    public Exception? ConfirmFailure { get; set; }
    public CatalogChannelSwitch? PendingOnSwitch { get; set; }

    public Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default) => Task.FromResult(Channel);
    public Task CheckAsync(CancellationToken ct = default) { Checks++; return Task.CompletedTask; }
    public Task ApplyAsync(CancellationToken ct = default) { Applies++; return Task.CompletedTask; }
    public Task CheckAtStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default)
    {
        AutoApplySet.Add(on);
        if (AutoApplyFailure is not null) { Changed?.Invoke(); return Task.FromException<bool>(AutoApplyFailure); }
        if (RefuseAutoApply) return Task.FromResult(false);
        AutoApply = on;
        return Task.FromResult(true);
    }

    public Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        Switches.Add(target);
        if (SwitchFailure is not null) { Changed?.Invoke(); return Task.FromException(SwitchFailure); }   // the real one raises in its finally
        if (PendingOnSwitch is not null) PendingSwitch = PendingOnSwitch;
        else Channel = target;
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        Confirms++;
        if (ConfirmFailure is not null) { PendingSwitch = null; Changed?.Invoke(); return Task.FromException(ConfirmFailure); }
        if (PendingSwitch is { } p) { Channel = p.Target; PendingSwitch = null; }
        Changed?.Invoke();
        return Task.CompletedTask;
    }

    public void KeepChannel() { Keeps++; PendingSwitch = null; Changed?.Invoke(); }
}
