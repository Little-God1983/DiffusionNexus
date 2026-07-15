using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Timer = System.Timers.Timer;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// App-level scheduler that owns the automatic-backup timer and orchestrates a backup pass across
/// the enabled payloads (dataset images and/or the core user database). See <see cref="IBackupScheduler"/>.
/// Backup work runs on a background thread with its own DI scope (the DbContext-backed services are
/// not thread-safe across scopes) and each step is narrated to the Unified Console.
/// </summary>
public sealed class BackupScheduler : IBackupScheduler, IDisposable
{
    private const string Source = "BackupScheduler";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActivityLogService _activityLog;
    private readonly IUnifiedLogger? _unifiedLogger;

    private readonly object _gate = new();
    private Timer? _timer;
    private bool _started;
    private bool _isRunning;

    public BackupScheduler(
        IServiceScopeFactory scopeFactory,
        IActivityLogService activityLog,
        IUnifiedLogger? unifiedLogger = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _activityLog = activityLog ?? throw new ArgumentNullException(nameof(activityLog));
        _unifiedLogger = unifiedLogger;
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) { return _isRunning; } }
    }

    /// <inheritdoc />
    public event EventHandler? StateChanged;

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _timer = new Timer(PollInterval.TotalMilliseconds) { AutoReset = true };
            _timer.Elapsed += OnTimerElapsed;
            _timer.Start();
        }

        // Initial due-check off the calling (UI) thread.
        _ = Task.Run(RunDueCheckAsync);
    }

    private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e) => _ = RunDueCheckAsync();

    private async Task RunDueCheckAsync()
    {
        try
        {
            if (IsRunning) return;

            var next = await GetNextBackupTimeAsync().ConfigureAwait(false);
            if (next is null || next.Value > DateTimeOffset.UtcNow) return;

            await RunPassAsync(manual: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Backup due-check failed");
        }
    }

    /// <inheritdoc />
    public Task<BackupResult> RunBackupNowAsync(CancellationToken cancellationToken = default)
        => Task.Run(() => RunPassAsync(manual: true, cancellationToken), cancellationToken);

    /// <inheritdoc />
    public async Task<DateTimeOffset?> GetNextBackupTimeAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var settings = await scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
            .GetSettingsAsync(cancellationToken).ConfigureAwait(false);

        if (!AnyPayloadConfigured(settings))
            return null;

        var lastBackup = settings.LastBackupAt ?? DateTimeOffset.MinValue;
        return lastBackup + ComputeInterval(settings);
    }

    private async Task<BackupResult> RunPassAsync(bool manual, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_isRunning)
                return BackupResult.Failed("A backup or restore operation is already in progress.");
            _isRunning = true;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);

        var successes = new List<string>();
        var failures = new List<string>();
        var totalFiles = 0;
        long totalBytes = 0;
        var progressStarted = false;

        try
        {
            AppSettings settings;
            using (var scope = _scopeFactory.CreateScope())
            {
                settings = await scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
                    .GetSettingsAsync(cancellationToken).ConfigureAwait(false);
            }

            var doDatasets = settings.BackupDatasetImagesEnabled
                             && !string.IsNullOrWhiteSpace(settings.DatasetStoragePath);
            var doDatabase = settings.BackupDatabaseEnabled;

            if (!doDatasets && !doDatabase)
            {
                _unifiedLogger?.Info(LogCategory.Backup, Source, "Backup skipped — no backup targets are enabled.");
                return BackupResult.Failed("No backup targets are enabled.");
            }

            if (string.IsNullOrWhiteSpace(settings.AutoBackupLocation))
            {
                _unifiedLogger?.Warn(LogCategory.Backup, Source, "Backup skipped — backup location is not configured.");
                return BackupResult.Failed("Backup location is not configured.");
            }

            var targets = string.Join(" + ",
                new[] { doDatasets ? "datasets" : null, doDatabase ? "database" : null }
                    .Where(x => x is not null));

            _unifiedLogger?.Info(LogCategory.Backup, Source,
                $"{(manual ? "Manual" : "Automatic")} backup started ({targets}).");
            _activityLog.StartBackupProgress($"Backing up {targets}");
            progressStarted = true;

            if (doDatasets)
            {
                _unifiedLogger?.Info(LogCategory.Backup, Source, "Zipping dataset images…");
                var r = await RunScopedAsync<IDatasetBackupService, BackupResult>(
                    (svc, p) => svc.BackupDatasetsAsync(p, cancellationToken),
                    PhaseProgress("Datasets")).ConfigureAwait(false);
                RecordResult("datasets", r, successes, failures, ref totalFiles, ref totalBytes,
                    okMessage: $"Dataset images backed up: {r.FilesBackedUp} files ({Mb(r.TotalSizeBytes)}).");
            }

            if (doDatabase)
            {
                _unifiedLogger?.Info(LogCategory.Backup, Source, "Copying database…");
                var r = await RunScopedAsync<IDatabaseBackupService, BackupResult>(
                    (svc, p) => svc.BackupDatabaseAsync(p, cancellationToken),
                    PhaseProgress("Database")).ConfigureAwait(false);
                RecordResult("database", r, successes, failures, ref totalFiles, ref totalBytes,
                    okMessage: $"Database copied ({Mb(r.TotalSizeBytes)}).");
            }

            // Only stamp the timestamp if at least one payload succeeded, so a total failure
            // leaves the next backup due immediately (retried on the next timer tick).
            if (successes.Count > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAppSettingsService>()
                    .UpdateLastBackupAtAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            }

            if (failures.Count == 0)
            {
                var summary = $"Backup complete: {string.Join(" + ", successes)} ({totalFiles} files, {Mb(totalBytes)}).";
                _unifiedLogger?.Info(LogCategory.Backup, Source, summary);
                _activityLog.CompleteBackupProgress(true, summary);
                return BackupResult.Succeeded(string.Empty, totalFiles, totalBytes);
            }

            var failMsg = $"Backup finished with errors — {string.Join("; ", failures)}.";
            _unifiedLogger?.Warn(LogCategory.Backup, Source, failMsg);
            _activityLog.CompleteBackupProgress(false, failMsg);
            return BackupResult.Failed(failMsg);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Backup pass failed");
            _unifiedLogger?.Error(LogCategory.Backup, Source, "Backup pass failed", ex);
            if (progressStarted) _activityLog.CompleteBackupProgress(false, $"Backup failed: {ex.Message}");
            return BackupResult.Failed(ex.Message);
        }
        finally
        {
            lock (_gate) { _isRunning = false; }
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RecordResult(
        string label, BackupResult r, List<string> successes, List<string> failures,
        ref int totalFiles, ref long totalBytes, string okMessage)
    {
        if (r.Success)
        {
            successes.Add(label);
            totalFiles += r.FilesBackedUp;
            totalBytes += r.TotalSizeBytes;
            _unifiedLogger?.Info(LogCategory.Backup, Source, okMessage, r.BackupPath);
        }
        else
        {
            failures.Add($"{label}: {r.ErrorMessage}");
            _unifiedLogger?.Error(LogCategory.Backup, Source, $"{label} backup failed: {r.ErrorMessage}");
        }
    }

    private async Task<TResult> RunScopedAsync<TService, TResult>(
        Func<TService, IProgress<BackupProgress>, Task<TResult>> action,
        IProgress<BackupProgress> progress)
        where TService : notnull
    {
        using var scope = _scopeFactory.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<TService>();
        return await action(svc, progress).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a progress reporter that (a) forwards the percent to the status-bar backup progress and
    /// (b) writes a Unified Console line whenever the phase text changes — so the user sees "Zipping…",
    /// "Copying database…", "Cleaning up old backups…" as they happen, without a line per percent tick.
    /// </summary>
    private IProgress<BackupProgress> PhaseProgress(string label)
    {
        string? lastPhase = null;
        return new DelegateProgress<BackupProgress>(p =>
        {
            _activityLog.ReportBackupProgress(p.ProgressPercent, $"{label}: {p.Phase}");
            if (!string.Equals(p.Phase, lastPhase, StringComparison.Ordinal))
            {
                lastPhase = p.Phase;
                _unifiedLogger?.Info(LogCategory.Backup, Source, $"{label}: {p.Phase}");
            }
        });
    }

    private static bool AnyPayloadConfigured(AppSettings s)
    {
        var anyEnabled = (s.BackupDatasetImagesEnabled && !string.IsNullOrWhiteSpace(s.DatasetStoragePath))
                         || s.BackupDatabaseEnabled;
        return anyEnabled && !string.IsNullOrWhiteSpace(s.AutoBackupLocation);
    }

    private static TimeSpan ComputeInterval(AppSettings s)
    {
        var ticks = TimeSpan.FromDays(s.AutoBackupIntervalDays).Ticks
                    + TimeSpan.FromHours(s.AutoBackupIntervalHours).Ticks;
        var interval = TimeSpan.FromTicks(ticks);
        return interval.TotalMinutes < 1 ? TimeSpan.FromHours(1) : interval; // Minimum 1 hour
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:F1} MB";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_timer is not null)
            {
                _timer.Elapsed -= OnTimerElapsed;
                _timer.Stop();
                _timer.Dispose();
                _timer = null;
            }
        }
    }

    private sealed class DelegateProgress<T>(Action<T> onReport) : IProgress<T>
    {
        public void Report(T value) => onReport(value);
    }
}
