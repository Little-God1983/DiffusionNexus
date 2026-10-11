using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Installer.SDK.Catalog;
using DiffusionNexus.Installer.SDK.Catalog.Packaging;
using DiffusionNexus.Installer.SDK.Catalog.Updates;

namespace DiffusionNexus.UI.Services.Catalog;

/// <summary>Idle → Checking → Checked → Applying → Applied. A failed or cancelled apply returns to Checked.</summary>
public enum CatalogUpdatePhase { Idle, Checking, Checked, Applying, Applied }

/// <summary>A switch the user has been warned about and not answered yet.</summary>
/// <param name="Preview">The check of the target channel, filtered for this app; Switch applies exactly this.</param>
public sealed record CatalogChannelSwitch(CatalogUpdateCheck Preview, ChannelSwitchWarning Warning)
{
    public CatalogChannel Target => Preview.Channel;
}

/// <summary>
/// The catalog-update state Settings → Catalog renders (#618). One process-wide instance; the view model
/// subscribes to <see cref="Changed"/> and marshals to the UI thread itself.
/// </summary>
public interface ICatalogUpdateCoordinator
{
    /// <summary>The channel this app follows: environment → its own saved setting → Stable.</summary>
    CatalogChannel Channel { get; }
    CatalogChannelSource ChannelSource { get; }
    /// <summary>The SDK's local override folder, for the OverrideActive message. Null when none is configured.</summary>
    string? OverridePath { get; }
    CatalogUpdatePhase Phase { get; }
    /// <summary>Always filtered for Diffusion Nexus (its Audience is set). After a switch: see SwitchChannelAsync.</summary>
    CatalogUpdateCheck? LastCheck { get; }
    DateTimeOffset? LastCheckedAt { get; }
    /// <summary>catalog-state.json of the folder the app reads, as of the last check, apply or switch.</summary>
    LocalCatalogState? Installed { get; }
    /// <summary>Non-null only while <see cref="Phase"/> is Applying and bytes flow.</summary>
    CatalogDownloadProgress? Progress { get; }
    CatalogApplyResult? LastApply { get; }
    /// <summary>The apply in <see cref="LastApply"/> failed or landed only part of the release.</summary>
    bool LastApplyFailed { get; }
    /// <summary>The apply in <see cref="LastApply"/> (or running) was started by the startup check.</summary>
    bool LastApplyAutomatic { get; }
    /// <summary>"Apply catalog updates automatically". False until the preferences are read, and while they cannot be.</summary>
    bool AutoApply { get; }
    /// <summary>The release is announced and offered (<see cref="CatalogUpdateOffer.Offered"/>).</summary>
    bool UpdateAvailable { get; }
    bool CanApply { get; }
    /// <summary>Why Apply is withheld although an update is available (an install is running). Null otherwise.</summary>
    string? ApplyBlockedReason { get; }
    /// <summary>
    /// Why the channel cannot be changed right now (an install is running: a switch changes the folder the
    /// catalog is read from at once). Null otherwise.
    /// </summary>
    string? ChannelChangeBlockedReason { get; }
    /// <summary>A switch waiting for Switch or Keep. While set, checks, applies and other switches are refused.</summary>
    CatalogChannelSwitch? PendingSwitch { get; }
    /// <summary>The channel being previewed for a switch, while the preview runs; null otherwise. The page shows its dot.</summary>
    CatalogChannel? SwitchingTo { get; }

    /// <summary>Raised after every state change, on any thread. Handlers that throw are logged and skipped.</summary>
    event Action? Changed;

    /// <summary>Resolves <see cref="Channel"/> without a network check and writes it into CatalogOptions.Channel. Never throws.</summary>
    Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default);
    /// <summary>Never throws. A running check is joined; while an apply runs it is a no-op that returns at once.</summary>
    Task CheckAsync(CancellationToken ct = default);
    /// <summary>Never throws. A no-op unless <see cref="CanApply"/>. Returns once the download has started.</summary>
    Task ApplyAsync(CancellationToken ct = default);
    /// <summary>The startup check (spec §6). Never throws.</summary>
    Task CheckAtStartupAsync(CancellationToken ct = default);
    /// <summary>Saves the toggle. False, with nothing saved, while a check, an apply or a switch runs. Save errors propagate.</summary>
    Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default);
    /// <summary>
    /// Previews <paramref name="target"/>. A diff that removes or changes Diffusion Nexus workloads waits in
    /// <see cref="PendingSwitch"/>; otherwise the switch goes ahead as <see cref="ConfirmSwitchAsync"/> would.
    /// Returns once the choice is saved; a download runs on. Save errors propagate (then nothing changed).
    /// </summary>
    Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default);
    /// <summary>"Switch": saves the channel, then lets the SDK fill the target folder. A no-op without a pending switch.</summary>
    Task ConfirmSwitchAsync(CancellationToken ct = default);
    /// <summary>"Keep": drops the pending switch. Saves nothing.</summary>
    void KeepChannel();
}

/// <summary>
/// Owns the check / apply / switch lifecycle around the SDK's <see cref="ICatalogUpdateService"/> and is the
/// one writer of <see cref="CatalogOptions.Channel"/>. Every check is filtered for Diffusion Nexus
/// (<see cref="CatalogAudience.DiffusionNexus"/>); releases are always applied whole. Every step logs to the
/// Unified Console (source "Catalog"). Mirrors the installer's coordinator (Into-The-Latent/DiffusionNexus.Installer#54).
/// </summary>
public sealed class CatalogUpdateCoordinator : ICatalogUpdateCoordinator, IDisposable
{
    public const string LogSource = "Catalog";

    private readonly ICatalogUpdateService _updates;
    private readonly ICatalog _catalog;
    private readonly CatalogOptions _options;
    private readonly ICatalogPreferenceStore _preferences;
    private readonly IInstallActivity _installs;
    private readonly Func<string?> _readEnvironment;
    private readonly IUnifiedLogger? _logger;
    private readonly CatalogAudience _audience = CatalogAudience.DiffusionNexus;

    private readonly Lock _gate = new();
    private Task? _inFlight;
    private bool _channelResolved;
    private bool _autoApplyResolved;
    private bool _switching;
    private bool _savingAutoApply;
    private bool _applyAutomatic;

    public CatalogUpdateCoordinator(
        ICatalogUpdateService updates,
        ICatalog catalog,
        CatalogOptions options,
        ICatalogPreferenceStore preferences,
        IInstallActivity installs,
        Func<string?> readEnvironment,
        IUnifiedLogger? logger = null)
    {
        _updates = updates;
        _catalog = catalog;
        _options = options;
        _preferences = preferences;
        _installs = installs;
        _readEnvironment = readEnvironment;
        _logger = logger;
        _installs.Changed += OnInstallsChanged;
    }

    public CatalogChannel Channel { get; private set; } = CatalogChannel.Stable;
    public CatalogChannelSource ChannelSource { get; private set; } = CatalogChannelSource.Default;
    public string? OverridePath => _options.LocalOverridePath;
    public CatalogUpdatePhase Phase { get; private set; } = CatalogUpdatePhase.Idle;
    public CatalogUpdateCheck? LastCheck { get; private set; }
    public DateTimeOffset? LastCheckedAt { get; private set; }
    public LocalCatalogState? Installed { get; private set; }
    public CatalogDownloadProgress? Progress { get; private set; }
    public CatalogApplyResult? LastApply { get; private set; }
    public bool AutoApply { get; private set; }
    public CatalogChannelSwitch? PendingSwitch { get; private set; }
    public CatalogChannel? SwitchingTo { get; private set; }

    public bool LastApplyFailed { get { lock (_gate) return LastApply is { } r && (r.Error is not null || r.Failed != CatalogSections.None); } }

    public bool LastApplyAutomatic { get { lock (_gate) return _applyAutomatic && (LastApply is not null || Phase is CatalogUpdatePhase.Applying or CatalogUpdatePhase.Applied); } }

    public bool UpdateAvailable => CatalogUpdateOffer.Offered(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed, ContentFromAnotherChannel);

    private bool ReleaseAvailable => CatalogUpdateOffer.ReleaseAvailable(LastCheck, Phase, PendingSwitch is not null, LastApplyFailed);

    /// <summary>The installed content records a channel other than the one followed (an embedded seed after a switch).</summary>
    private bool ContentFromAnotherChannel => Installed is { } s && (s.Workloads?.Channel ?? s.Workflows?.Channel) is { } recorded && recorded != Channel;

    public bool CanApply => UpdateAvailable && Phase == CatalogUpdatePhase.Checked && _installs.Running is null;

    public string? ApplyBlockedReason => UpdateAvailable && _installs.Running is { } name ? $"It can be applied once {name} has finished." : null;

    public string? ChannelChangeBlockedReason => _installs.Running is { } name ? $"The channel can be changed once {name} has finished." : null;

    /// <summary>Why the last read of the preferences failed; null once they were read.</summary>
    private string? _preferencesError;

    public event Action? Changed;

    // ----- channel -----

    public async Task<CatalogChannel> ResolveChannelAsync(CancellationToken ct = default)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            // The folder now read: the page describes it before the first check (PR #619 round 4).
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            lock (_gate) KeepInstalled(installed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"The catalog channel could not be resolved; following {Channel}.", ex.Message);
        }
        Raise();
        lock (_gate) return Channel;
    }

    /// <summary>Reads the channel and the toggle once; a failed read latches nothing and is retried next time.</summary>
    private async Task EnsureChannelAsync(CancellationToken ct)
    {
        lock (_gate) { if (_channelResolved && _autoApplyResolved) return; }

        CatalogPreferences? stored = null;
        try
        {
            stored = await _preferences.LoadAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, "The catalog setting could not be read; read again on the next check. Nothing is applied automatically until it can be.", ex.Message);
            lock (_gate) _preferencesError = $"the setting could not be read ({ex.Message})";
        }

        lock (_gate)
        {
            // Read without a channel (the installer's file could not be read on the first start): Stable for this
            // run, not stored, so the next start tries the copy again. Checks work: refusing them would never end
            // while that file stays unreadable, and each channel has its own folder (PR #619 round 4).
            if (stored is not null) _preferencesError = null;
            if (!_autoApplyResolved)
            {
                AutoApply = stored?.ApplyAutomatically ?? false;
                if (stored is not null) _autoApplyResolved = true;
            }
            // A switch finished while the read above was awaited: its answer is newer.
            if (_channelResolved) return;
            ApplyResolution(_readEnvironment(), stored?.Channel);
            if (stored is not null) _channelResolved = true;
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ApplyResolution(string? environmentValue, string? savedValue)
    {
        var (channel, source) = CatalogChannelResolver.Resolve(environmentValue, savedValue);
        Channel = channel;
        ChannelSource = source;
        _options.Channel = channel;
        _logger?.Info(LogCategory.General, LogSource, $"Following the {channel} catalog channel ({source})");
    }

    // ----- check -----

    public Task CheckAsync(CancellationToken ct = default)
    {
        Task inFlight;
        lock (_gate)
        {
            if (_switching || PendingSwitch is not null)
            {
                _logger?.Info(LogCategory.General, LogSource, "Catalog check refused: a channel switch is in progress");
                return Task.CompletedTask;
            }
            // By the phase, not the task: a task flips the phase under this lock, then still logs and raises
            // before it completes. Only a running check is joined (PR #619 round 1).
            if (Phase == CatalogUpdatePhase.Checking && _inFlight is { IsCompleted: false }) return _inFlight;
            if (Phase == CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, "Catalog check refused: an apply is in progress");
                return Task.CompletedTask;
            }
            Phase = CatalogUpdatePhase.Checking;
            Progress = null;
            LastApply = null;
            _applyAutomatic = false;
            inFlight = _inFlight = Task.Run(() => CheckCoreAsync(ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task CheckCoreAsync(CancellationToken ct)
    {
        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            string? unreadable;
            lock (_gate) unreadable = !_channelResolved && ChannelSource != CatalogChannelSource.Environment ? _preferencesError : null;
            if (unreadable is not null)
            {
                // The channel is a guess (Stable): checking it would offer Stable's release to a user who saved
                // Preview (PR #619 round 1). Nothing is checked until the setting can be read.
                _logger?.Warn(LogCategory.General, LogSource, $"Catalog update check skipped: the catalog channel is not known yet ({unreadable})");
                var known = LocalCatalogState.Load(_options.InstalledCatalogPath);
                lock (_gate)
                {
                    KeepInstalled(known);
                    LastCheck = FailedCheck(Channel, Installed, $"The catalog channel is not known yet: {unreadable}. Try again in a moment.");
                    LastCheckedAt = DateTimeOffset.Now;
                    Phase = CatalogUpdatePhase.Checked;
                }
                Raise();
                return;
            }
            _logger?.Info(LogCategory.General, LogSource, $"Catalog update check started on {Channel}");
            var check = (await _updates.CheckAsync(ct).ConfigureAwait(false)).For(_audience);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            lock (_gate)
            {
                LastCheck = check;
                LastCheckedAt = DateTimeOffset.Now;
                // An empty state (a channel folder that does not exist yet) describes nothing; the last known one stays.
                KeepInstalled(installed);
                Phase = CatalogUpdatePhase.Checked;
            }
            // A failed check or one that needs a newer app is a warning: the console is filtered by level (PR #619 round 4).
            var level = check.Outcome is CatalogUpdateOutcome.Failed or CatalogUpdateOutcome.RequiresNewerSoftware ? LogLevel.Warning : LogLevel.Info;
            _logger?.Log(level, LogCategory.General, LogSource,
                $"Catalog update check: {check.Outcome} (remote v{check.Remote?.CatalogVersion}, installed v{installed.HighestCatalogVersion}, " +
                $"{check.Workloads.Count} workload / {check.Workflows.Count} workflow changes for Diffusion Nexus, " +
                $"{check.Unfiltered!.Workloads.Count + check.Unfiltered.Workflows.Count} in the release)",
                check.Error);
        }
        catch (OperationCanceledException)
        {
            // A cancel (app shutdown) is no failure: nothing to report (PR #619 round 3).
            _logger?.Info(LogCategory.General, LogSource, "Catalog update check cancelled");
            lock (_gate) Phase = LastCheck is null ? CatalogUpdatePhase.Idle : CatalogUpdatePhase.Checked;
        }
        catch (Exception ex)
        {
            // The SDK's CheckAsync never throws; this is the last line of defence for a task nobody awaits.
            _logger?.Error(LogCategory.General, LogSource, "Catalog update check failed unexpectedly", ex);
            lock (_gate)
            {
                LastCheck = FailedCheck(Channel, Installed, ex.Message);
                LastCheckedAt = DateTimeOffset.Now;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>
    /// Takes <paramref name="state"/> as the installed state unless it records nothing (a channel folder that does not
    /// exist yet) and a state is known already: an empty folder is no description of what the app reads. Caller holds
    /// <see cref="_gate"/>. One rule for every writer (PR #619 round 3).
    /// </summary>
    private void KeepInstalled(LocalCatalogState state)
    {
        if (state.RecordsAnything() || Installed is null) Installed = state;
    }

    /// <summary>A check that never ran, filtered like every other so <see cref="LastCheck"/> always carries the audience.</summary>
    private CatalogUpdateCheck FailedCheck(CatalogChannel channel, LocalCatalogState? installed, string error) =>
        new CatalogUpdateCheck(CatalogUpdateOutcome.Failed, channel, null, installed ?? new LocalCatalogState(), [], [], error).For(_audience);

    // ----- apply -----

    public Task ApplyAsync(CancellationToken ct = default) => StartApply(automatic: false, ct);

    public async Task CheckAtStartupAsync(CancellationToken ct = default)
    {
        await CheckAsync(ct).ConfigureAwait(false);

        bool offered, release, auto;
        lock (_gate) (offered, release, auto) = (UpdateAvailable, ReleaseAvailable, AutoApply);

        if (auto && release)
        {
            // Not awaited and not under the caller's token: returns once the download has started.
            _ = StartApply(automatic: true, CancellationToken.None);
            bool started;
            lock (_gate) started = Phase is CatalogUpdatePhase.Applying or CatalogUpdatePhase.Applied || LastApply is not null;
            if (started) return;
            // Refused (an install runs): the update waits on the page, and the console says where (review M6).
        }
        if (offered) _logger?.Info(LogCategory.General, LogSource, "Catalog update available – see Settings → Catalog");
        else if (release)
        {
            int? version;
            lock (_gate) version = LastCheck?.Remote?.CatalogVersion;
            _logger?.Info(LogCategory.General, LogSource, $"Catalog v{version} changes nothing Diffusion Nexus shows; nothing to apply.");
        }
    }

    private Task StartApply(bool automatic, CancellationToken ct)
    {
        CatalogUpdateCheck check;
        Task inFlight;
        lock (_gate)
        {
            if (Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Catalog apply refused: an apply or check is already running (automatic {automatic})");
                return Task.CompletedTask;
            }
            // A click applies what the page offered. The startup apply takes the release whether or not it
            // changes anything Diffusion Nexus shows: full snapshots, and the folder is shared with the installer.
            var canStart = automatic
                ? ReleaseAvailable && Phase == CatalogUpdatePhase.Checked && _installs.Running is null
                : CanApply;
            if (_switching || !canStart)
            {
                _logger?.Info(LogCategory.General, LogSource,
                    $"Catalog apply refused: phase {Phase}, offered {UpdateAvailable}, install running {_installs.Running ?? "none"}, switching {_switching}, automatic {automatic}");
                return Task.CompletedTask;
            }
            if (automatic) _logger?.Info(LogCategory.General, LogSource, "Applying the catalog update automatically (setting on)");
            check = LastCheck!;
            Phase = CatalogUpdatePhase.Applying;
            LastApply = null;
            _applyAutomatic = automatic;
            Progress = null;
            inFlight = _inFlight = Task.Run(() => ApplyCoreAsync(check, ct), CancellationToken.None);
        }
        Raise();
        return inFlight;
    }

    private async Task ApplyCoreAsync(CatalogUpdateCheck check, CancellationToken ct)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Catalog apply started: v{check.Remote?.CatalogVersion} from {check.Channel}");
        try
        {
            var result = await _updates.ApplyAsync(check, CatalogSections.All, new ProgressRelay(this), ct).ConfigureAwait(false);
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var succeeded = result.Failed == CatalogSections.None && result.Error is null;
            var landedSomething = result.Applied != CatalogSections.None;
            var fresh = await AfterSwitchAsync(check, result.Applied, succeeded, installed).ConfigureAwait(false);

            lock (_gate)
            {
                LastApply = result;
                KeepInstalled(installed);
                Progress = null;
                if (fresh is not null)
                {
                    LastCheck = fresh;
                    LastCheckedAt = DateTimeOffset.Now;
                }
                // A switch whose target folder was already current applies nothing: nothing was "updated".
                Phase = succeeded && landedSomething ? CatalogUpdatePhase.Applied : CatalogUpdatePhase.Checked;
            }
            if (landedSomething) LogChanges(check, result.Applied);
            if (succeeded)
                _logger?.Info(LogCategory.General, LogSource, landedSomething
                    ? $"Catalog updated to v{installed.HighestCatalogVersion} ({check.Channel})."
                    : $"The {check.Channel} catalog folder was already current.");
            else
                _logger?.Warn(LogCategory.General, LogSource, $"Catalog update incomplete: {result.Failed} not applied (applied: {result.Applied}).", result.Error);
        }
        catch (OperationCanceledException)
        {
            _logger?.Info(LogCategory.General, LogSource, "Catalog apply cancelled; nothing was changed");
            lock (_gate)
            {
                LastApply = null;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        catch (Exception ex)
        {
            // The SDK catches only I/O errors per section; anything else escapes, possibly after a section
            // landed, and then its catalog cache was not invalidated either. A fresh check says what is left.
            _logger?.Error(LogCategory.General, LogSource, "Catalog apply failed unexpectedly", ex);
            TryInvalidate();
            var installed = LocalCatalogState.Load(_options.InstalledCatalogPath);
            var fresh = await CheckFollowedAsync().ConfigureAwait(false);
            lock (_gate)
            {
                LastApply = new CatalogApplyResult(CatalogSections.None, CatalogSections.All, ex.Message);
                KeepInstalled(installed);
                LastCheck = fresh;
                LastCheckedAt = DateTimeOffset.Now;
                Progress = null;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    /// <summary>
    /// What <see cref="LastCheck"/> becomes after the apply of a switch preview, or null to keep it. The preview
    /// compared the target's release with the folder being left; the SDK's apply checked the target folder
    /// itself. Landed everything: the preview stands. Landed nothing without an error: the SDK's verdict that the
    /// folder is current, recorded as up to date. Failed with nothing landed: the preview stands -- applying it
    /// again is the retry. Landed part: a check of the folder now followed tells what is left. (Task 6 uses it.)
    /// </summary>
    private async Task<CatalogUpdateCheck?> AfterSwitchAsync(CatalogUpdateCheck check, CatalogSections applied, bool succeeded, LocalCatalogState installed)
    {
        if (applied == CatalogSections.All) return null;
        // Landed nothing without an error: the SDK found the folder current (a switch's target folder, or the
        // installer applied the same release into the shared folder meanwhile, PR #619 round 2).
        if (succeeded && applied == CatalogSections.None)
            return new CatalogUpdateCheck(CatalogUpdateOutcome.UpToDate, check.Channel, check.Remote, installed, [], [], null).For(_audience);
        if (!check.IsSwitchPreview || applied == CatalogSections.None) return null;
        return await CheckFollowedAsync().ConfigureAwait(false);
    }

    /// <summary>A check of the folder now followed. Never throws; a check that throws is a Failed check.</summary>
    private async Task<CatalogUpdateCheck> CheckFollowedAsync()
    {
        try
        {
            var fresh = (await _updates.CheckAsync(CancellationToken.None).ConfigureAwait(false)).For(_audience);
            _logger?.Info(LogCategory.General, LogSource, $"Catalog check of the followed folder: {fresh.Outcome} (remote v{fresh.Remote?.CatalogVersion})", fresh.Error);
            return fresh;
        }
        catch (Exception ex)
        {
            _logger?.Error(LogCategory.General, LogSource, "Catalog check of the followed folder failed unexpectedly", ex);
            CatalogChannel channel;
            LocalCatalogState? installed;
            lock (_gate) (channel, installed) = (Channel, Installed);
            return FailedCheck(channel, installed, ex.Message);
        }
    }

    /// <summary>The change lines of the sections that landed only (PR #619 round 4): a workload row is not "applied" when Workloads failed.</summary>
    private void LogChanges(CatalogUpdateCheck check, CatalogSections applied)
    {
        foreach (var row in CatalogChangeRows.Build(check))
        {
            var section = row.Kind == "Workflow" ? CatalogSections.Workflows : CatalogSections.Workloads;
            if ((applied & section) == 0) continue;
            _logger?.Info(LogCategory.General, LogSource, $"{row.Change}: {row.Name} ({row.VersionText})");
        }
    }

    private void TryInvalidate()
    {
        try { _catalog.Invalidate(); }
        catch (Exception ex) { _logger?.Warn(LogCategory.General, LogSource, "The catalog cache could not be refreshed after a failed apply.", ex.Message); }
    }

    // ----- the setting -----

    public async Task<bool> SetAutoApplyAsync(bool on, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Apply catalog updates automatically: change to {(on ? "on" : "off")} refused while {Phase}");
                return false;
            }
            _savingAutoApply = true;
        }
        try
        {
            await _preferences.SaveApplyAutomaticallyAsync(on, ct).ConfigureAwait(false);
            lock (_gate)
            {
                AutoApply = on;
                _autoApplyResolved = true;
            }
            _logger?.Info(LogCategory.General, LogSource, $"Apply catalog updates automatically: saved {(on ? "on" : "off")}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"Apply catalog updates automatically: {(on ? "on" : "off")} could not be saved; nothing was changed", ex.Message);
            throw;
        }
        finally
        {
            lock (_gate) _savingAutoApply = false;
            Raise();
        }
    }

    // ----- switch -----

    public async Task SwitchChannelAsync(CatalogChannel target, CancellationToken ct = default)
    {
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (_switching || _savingAutoApply || PendingSwitch is not null || Phase is CatalogUpdatePhase.Checking or CatalogUpdatePhase.Applying)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {target} refused while {Phase}{(PendingSwitch is null ? "" : ", a switch waits for an answer")}");
                return;
            }
            // A switch changes the folder the catalog is read from at once: never under a running install (PR #619 round 1).
            if (_installs.Running is { } running)
            {
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {target} refused: {running} is installing");
                return;
            }
            // Claimed with the guard so a check starting during the awaits below backs off.
            _switching = true;
            phaseBefore = Phase;
        }

        try
        {
            await EnsureChannelAsync(ct).ConfigureAwait(false);
            CatalogChannel current;
            CatalogChannelSource source;
            lock (_gate) (current, source) = (Channel, ChannelSource);

            if (source == CatalogChannelSource.Environment)
            {
                // The environment decides what this run follows; the preference is saved for runs without it.
                await _preferences.SaveChannelAsync(target, ct).ConfigureAwait(false);
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel {target} saved; DIFFUSIONNEXUS_CATALOG_CHANNEL keeps {current} for this run");
                return;
            }
            if (current == target)
            {
                bool unknown;
                lock (_gate) unknown = !_channelResolved;
                if (!unknown) return;
                // The shown channel is a guess (the preference could not be read): picking it decides it (PR #619 round 3).
                await _preferences.SaveChannelAsync(target, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    ApplyResolution(_readEnvironment(), target.ToString());
                    _channelResolved = true;
                    _preferencesError = null;
                }
                return;
            }

            lock (_gate)
            {
                Phase = CatalogUpdatePhase.Checking;
                Progress = null;
                SwitchingTo = target;   // the page keeps the clicked dot while the preview runs (PR #619 round 3)
            }
            Raise();

            var preview = await PreviewAsync(current, target, ct).ConfigureAwait(false);
            lock (_gate) Phase = phaseBefore;

            if (preview.Outcome == CatalogUpdateOutcome.RequiresNewerSoftware)
            {
                // That channel's release needs a newer Diffusion Nexus: following it would leave the app on a channel
                // it can never read (PR #619 round 5). Nothing is saved; the page says why.
                lock (_gate)
                {
                    LastCheck = preview;
                    LastCheckedAt = DateTimeOffset.Now;
                }
                _logger?.Warn(LogCategory.General, LogSource, $"Catalog channel switch to {target} refused: its catalog v{preview.Remote?.CatalogVersion} needs a newer Diffusion Nexus");
                return;
            }

            if (ChannelSwitchWarning.For(preview) is { } warning)
            {
                lock (_gate) PendingSwitch = new CatalogChannelSwitch(preview, warning);
                _logger?.Info(LogCategory.General, LogSource,
                    $"Catalog channel switch to {target} waits for an answer: v{warning.Version} removes {warning.Removed.Count} and {warning.ChangesText ?? "changes nothing"}");
                return;
            }

            await CommitSwitchAsync(preview, phaseBefore, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                SwitchingTo = null;
                _switching = false;
                // The preview's Checking ends here; a held switch's background check keeps its own.
                if (Phase == CatalogUpdatePhase.Checking && _inFlight is not { IsCompleted: false }) Phase = phaseBefore;
            }
            Raise();
        }
    }

    public async Task ConfirmSwitchAsync(CancellationToken ct = default)
    {
        CatalogChannelSwitch pending;
        CatalogUpdatePhase phaseBefore;
        lock (_gate)
        {
            if (PendingSwitch is null || _switching)
            {
                _logger?.Info(LogCategory.General, LogSource, "Catalog channel switch confirmed with no switch waiting; nothing to do");
                return;
            }
            if (_installs.Running is { } running)
            {
                // The answer waits: an install started while the warning was shown.
                _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {PendingSwitch.Target} waits: {running} is installing");
                return;
            }
            pending = PendingSwitch;
            PendingSwitch = null;
            _switching = true;
            phaseBefore = Phase;
        }

        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {pending.Target} confirmed");
        try
        {
            await CommitSwitchAsync(pending.Preview, phaseBefore, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Nothing was changed: the warning stays, so the user can answer again (PR #619 round 5).
            lock (_gate) PendingSwitch ??= pending;
            throw;
        }
        finally
        {
            lock (_gate) _switching = false;
            Raise();
        }
    }

    public void KeepChannel()
    {
        lock (_gate)
        {
            if (PendingSwitch is null) return;
            _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch to {PendingSwitch.Target} declined; nothing was saved");
            PendingSwitch = null;
        }
        Raise();
    }

    /// <summary>The switch as the SDK diffs it, filtered for Diffusion Nexus. Changes nothing. Never throws.</summary>
    private async Task<CatalogUpdateCheck> PreviewAsync(CatalogChannel from, CatalogChannel target, CancellationToken ct)
    {
        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switch: previewing {target}");
        try
        {
            var check = (await _updates.PreviewSwitchAsync(from, target, ct).ConfigureAwait(false)).For(_audience);
            _logger?.Info(LogCategory.General, LogSource,
                $"Catalog channel preview: {check.Outcome} (remote v{check.Remote?.CatalogVersion}, {check.Workloads.Count} workload / {check.Workflows.Count} workflow changes for Diffusion Nexus)",
                check.Error);
            return check;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Error(LogCategory.General, LogSource, "Catalog channel preview failed unexpectedly", ex);
            return FailedCheck(target, Installed, ex.Message);
        }
    }

    /// <summary>
    /// Saves the channel, follows it, and starts what the folder now followed needs; returns once the choice is
    /// saved. The caller holds <see cref="_switching"/>. Only a failed save throws, and then nothing changed.
    /// </summary>
    private async Task CommitSwitchAsync(CatalogUpdateCheck preview, CatalogUpdatePhase phaseBefore, CancellationToken ct)
    {
        var target = preview.Channel;
        try
        {
            await _preferences.SaveChannelAsync(target, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, $"Catalog channel {target} could not be saved; nothing was changed", ex.Message);
            lock (_gate) Phase = phaseBefore;
            throw;
        }

        // With shared folders an up-to-date preview is handed to the SDK as well: it compared against the folder
        // being left, and the SDK's apply checks the target folder itself (fills it when missing or behind,
        // applies nothing when current). With one folder the preview compared against that very folder.
        var appliable = preview.Outcome == CatalogUpdateOutcome.UpdatesAvailable
            || (preview.Outcome == CatalogUpdateOutcome.UpToDate && _options.UsesSharedFolders);
        // The target folder's state file, outside the lock (the shared-folder read enumerates format folders).
        var known = LocalCatalogState.Load(_options.ReadFolderFor(target));
        var applying = false;
        var held = false;
        lock (_gate)
        {
            ApplyResolution(_readEnvironment(), target.ToString());
            _channelResolved = true;
            KeepInstalled(known);
            LastApply = null;
            _applyAutomatic = false;
            Progress = null;
            if (Channel != target)
            {
                // The environment variable appeared since the preview: it decides.
                LastCheck = null;
                Phase = CatalogUpdatePhase.Idle;
            }
            else
            {
                LastCheck = preview;
                LastCheckedAt = DateTimeOffset.Now;
                if (appliable && _installs.Running is null)
                {
                    // Not under the caller's token: the switch has returned by the time the download runs.
                    Phase = CatalogUpdatePhase.Applying;
                    _inFlight = Task.Run(() => ApplyCoreAsync(preview, CancellationToken.None), CancellationToken.None);
                    applying = true;
                }
                else
                {
                    held = appliable;
                    Phase = held ? CatalogUpdatePhase.Checking : CatalogUpdatePhase.Checked;
                }
            }
            // Held (only an install that started after the guard above gets here): the folder check is the
            // in-flight task. Not held: just the state read, which a Check or Apply must not wait for (PR #619 round 1).
            if (held) _inFlight = Task.Run(() => AfterSwitchNotAppliedAsync(held), CancellationToken.None);
            else if (!applying) _ = Task.Run(() => AfterSwitchNotAppliedAsync(held), CancellationToken.None);
        }
        _logger?.Info(LogCategory.General, LogSource, $"Catalog channel switched to {target}: " +
            (applying ? (preview.Outcome == CatalogUpdateOutcome.UpToDate ? "checking its folder" : $"applying v{preview.Remote?.CatalogVersion}")
            : held ? "the apply waits for the running install" : preview.Outcome.ToString()));
    }

    /// <summary>
    /// After a switch that started no apply, in the background: the state of the catalog the app reads now
    /// (reading it seeds a channel folder that does not exist yet); held by a running install, a check of the
    /// folder now followed, so the Apply after the install lands what that folder needs. Never throws.
    /// </summary>
    private async Task AfterSwitchNotAppliedAsync(bool held)
    {
        LocalCatalogState? state = null;
        try
        {
            // ICatalog.State blocks on the load and seeds a missing channel folder; off the caller's thread.
            state = await Task.Run(() => _catalog.State).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger?.Warn(LogCategory.General, LogSource, "The catalog could not be read after the switch; reading its state file instead.", ex.Message);
        }
        state ??= LocalCatalogState.Load(_options.InstalledCatalogPath);
        lock (_gate)
        {
            KeepInstalled(state);
        }

        if (held)
        {
            var fresh = await CheckFollowedAsync().ConfigureAwait(false);
            lock (_gate)
            {
                LastCheck = fresh;
                LastCheckedAt = DateTimeOffset.Now;
                Phase = CatalogUpdatePhase.Checked;
            }
        }
        Raise();
    }

    // ----- plumbing -----

    // Every change, not only none↔some: the hold text names the install that runs now (PR #619 round 5).
    // Begin and end are rare, so the page re-renders a handful of times per install.
    private void OnInstallsChanged() => Raise();

    /// <summary>Each subscriber on its own: a throwing handler (a view gone away) must not fault a background task.</summary>
    private void Raise()
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action)handler)(); }
            catch (Exception ex) { _logger?.Warn(LogCategory.General, LogSource, "A catalog page handler threw.", ex.Message); }
        }
    }

    /// <summary>Raises only when the shown percent moves: the SDK reports every ~80 KB.</summary>
    private sealed class ProgressRelay(CatalogUpdateCoordinator owner) : IProgress<CatalogDownloadProgress>
    {
        private long? _lastShown;

        public void Report(CatalogDownloadProgress value)
        {
            var shown = value.TotalBytes is > 0 ? (long)Math.Round(100.0 * value.BytesReceived / value.TotalBytes.Value) : (long)Math.Round(value.BytesReceived / 104_857.6);
            bool moved;
            lock (owner._gate)
            {
                owner.Progress = value;
                moved = shown != _lastShown;
                _lastShown = shown;
            }
            if (moved) owner.Raise();
        }
    }

    public void Dispose() => _installs.Changed -= OnInstallsChanged;
}

public static class LocalCatalogStateExtensions
{
    /// <summary>A section was recorded. False for the empty state a folder that does not exist yet reads as.</summary>
    public static bool RecordsAnything(this LocalCatalogState state) => state.Workloads is not null || state.Workflows is not null;
}
