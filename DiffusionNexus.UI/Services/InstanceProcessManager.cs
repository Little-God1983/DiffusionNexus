using System.Collections.Concurrent;
using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Singleton service that decouples instance lifecycle from any specific view.
/// Wraps <see cref="PackageProcessManager"/> and pipes stdout/stderr through
/// <see cref="IUnifiedLogger"/> with <see cref="LogCategory.InstanceManagement"/>.
/// </summary>
public sealed class InstanceProcessManager : IInstanceProcessManager, IDisposable
{
    private readonly PackageProcessManager _processManager;
    private readonly IUnifiedLogger _logger;
    private readonly ITaskTracker _taskTracker;
    private readonly IServiceProvider _serviceProvider;
    private readonly ConcurrentDictionary<string, ITrackedTaskHandle> _runningTasks = new();
    private readonly RunningInstancesSubject _runningSubject = new();

    /// <summary>
    /// Caches packageId → display name so that the OnProcessOutput fallback can
    /// log with source "Instance: {name}" instead of "Instance:{id}". Without this,
    /// the UnifiedConsole search filter (SearchText = tab.Name) excludes all output.
    /// </summary>
    private readonly ConcurrentDictionary<int, string> _packageNameCache = new();

    public InstanceProcessManager(
        PackageProcessManager processManager,
        IUnifiedLogger logger,
        ITaskTracker taskTracker,
        IServiceProvider serviceProvider)
    {
        _processManager = processManager;
        _logger = logger;
        _taskTracker = taskTracker;
        _serviceProvider = serviceProvider;

        // Wire existing process manager events to unified logger
        _processManager.OutputReceived += OnProcessOutput;
        _processManager.RunningStateChanged += OnRunningStateChanged;
        _processManager.WebUrlDetected += OnWebUrlDetected;

        // Pre-populate name cache so the very first output line uses the correct source
        _ = PopulateNameCacheAsync();
    }

    /// <summary>
    /// Loads all package names into the cache. Called once at startup so that
    /// process output logged before RunningStateChanged can use the correct source.
    /// </summary>
    private async Task PopulateNameCacheAsync()
    {
        try
        {
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .CreateScope(_serviceProvider);
            var repo = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<IInstallerPackageRepository>(scope.ServiceProvider);

            var packages = await repo.GetAllAsync();
            foreach (var p in packages)
            {
                _packageNameCache[p.Id] = p.Name;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "InstanceProcessManager: Failed to pre-populate name cache");
        }
    }

    /// <inheritdoc />
    public async Task<ITrackedTaskHandle> StartInstanceAsync(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);

        var packageId = int.Parse(instanceId);

        // Resolve the repository to look up the package
        using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .CreateScope(_serviceProvider);
        var repo = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<IInstallerPackageRepository>(scope.ServiceProvider);

        var package = await repo.GetByIdAsync(packageId);
        if (package is null)
        {
            _logger.Error(LogCategory.InstanceManagement, nameof(InstanceProcessManager),
                $"Instance '{instanceId}' not found in database");
            throw new InvalidOperationException($"InstallerPackage with ID {instanceId} not found.");
        }

        if (string.IsNullOrWhiteSpace(package.ExecutablePath))
        {
            _logger.Error(LogCategory.InstanceManagement, nameof(InstanceProcessManager),
                $"No executable path configured for '{package.Name}'");
            throw new InvalidOperationException($"No executable path configured for '{package.Name}'.");
        }

        var fullPath = Path.Combine(package.InstallationPath, package.ExecutablePath);
        if (!File.Exists(fullPath))
        {
            _logger.Error(LogCategory.InstanceManagement, nameof(InstanceProcessManager),
                $"Executable not found at '{fullPath}'");
            throw new FileNotFoundException($"Executable not found: {fullPath}", fullPath);
        }

        var handle = _taskTracker.BeginTask(
            $"Instance: {package.Name}",
            LogCategory.InstanceManagement);

        _runningTasks[instanceId] = handle;
        handle.ReportIndeterminate("Starting...");

        // Ensure the name is cached for the fallback source path
        _packageNameCache[packageId] = package.Name;

        _processManager.Launch(packageId, fullPath, package.InstallationPath, package.Arguments, package.Type);

        return handle;
    }

    /// <inheritdoc />
    public async Task StopInstanceAsync(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var packageId = int.Parse(instanceId);

        _logger.Info(LogCategory.InstanceManagement, nameof(InstanceProcessManager),
            $"Stopping instance '{instanceId}'");

        await _processManager.StopAsync(packageId);
    }

    /// <inheritdoc />
    public async Task KillInstanceAsync(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var packageId = int.Parse(instanceId);

        _logger.Warn(LogCategory.InstanceManagement, nameof(InstanceProcessManager),
            $"Force-killing instance '{instanceId}'");

        // StopAsync already escalates to Kill after timeout, but for explicit kill
        // we want immediate force
        await _processManager.StopAsync(packageId);
    }

    /// <inheritdoc />
    public IObservable<IReadOnlySet<string>> RunningInstances => _runningSubject;

    /// <inheritdoc />
    public bool IsRunning(string instanceId)
    {
        return int.TryParse(instanceId, out var packageId) && _processManager.IsRunning(packageId);
    }

    /// <inheritdoc />
    public string? GetDetectedUrl(string instanceId)
    {
        return int.TryParse(instanceId, out var packageId) ? _processManager.GetDetectedUrl(packageId) : null;
    }

    public void Dispose()
    {
        _processManager.OutputReceived -= OnProcessOutput;
        _processManager.RunningStateChanged -= OnRunningStateChanged;
        _processManager.WebUrlDetected -= OnWebUrlDetected;
    }

    private void OnProcessOutput(int packageId, ConsoleOutputLine line)
    {
        var instanceId = packageId.ToString();
        var level = line.IsError ? LogLevel.Warning : LogLevel.Info;

        // Log through the task handle if we have one
        if (_runningTasks.TryGetValue(instanceId, out var handle))
        {
            handle.Log(level, line.Text);
        }
        else
        {
            // Fallback: log directly, using the display name so the UnifiedConsole
            // search filter (SearchText = tab.Name) can match the source.
            var source = _packageNameCache.TryGetValue(packageId, out var name)
                ? $"Instance: {name}"
                : $"Instance:{packageId}";
            _logger.Log(level, LogCategory.InstanceManagement, source,
                line.Text, taskId: null);
        }
    }

    private void OnRunningStateChanged(int packageId, bool running)
    {
        var instanceId = packageId.ToString();

        // Ensure the name is cached for processes launched outside StartInstanceAsync
        // (e.g. directly from UnifiedConsoleViewModel or InstallerManagerViewModel)
        if (running && !_packageNameCache.ContainsKey(packageId))
        {
            _ = CachePackageNameAsync(packageId);
        }

        if (!running && _runningTasks.TryRemove(instanceId, out var handle))
        {
            handle.Complete("Process exited");
            handle.Dispose();
        }

        EmitRunningSet();
    }

    private async Task CachePackageNameAsync(int packageId)
    {
        try
        {
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .CreateScope(_serviceProvider);
            var repo = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<IInstallerPackageRepository>(scope.ServiceProvider);

            var package = await repo.GetByIdAsync(packageId);
            if (package is not null)
            {
                _packageNameCache[packageId] = package.Name;
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "InstanceProcessManager: Failed to cache name for package {Id}", packageId);
        }
    }

    private void OnWebUrlDetected(int packageId, string url)
    {
        var instanceId = packageId.ToString();
        _logger.Info(LogCategory.InstanceManagement, $"Instance:{packageId}",
            $"Web UI detected: {url}");

        if (_runningTasks.TryGetValue(instanceId, out var handle))
        {
            handle.ReportIndeterminate($"Running — {url}");
        }
    }

    private void EmitRunningSet()
    {
        var set = new HashSet<string>(_runningTasks.Keys);
        _runningSubject.OnNext(set);
    }

    /// <summary>
    /// Minimal IObservable for running instance IDs.
    /// </summary>
    private sealed class RunningInstancesSubject : IObservable<IReadOnlySet<string>>
    {
        private readonly List<IObserver<IReadOnlySet<string>>> _observers = [];
        private readonly object _lock = new();

        public IDisposable Subscribe(IObserver<IReadOnlySet<string>> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_lock) _observers.Add(observer);
            return new Unsub(this, observer);
        }

        public void OnNext(IReadOnlySet<string> value)
        {
            IObserver<IReadOnlySet<string>>[] snapshot;
            lock (_lock) snapshot = [.. _observers];
            foreach (var o in snapshot)
            {
                try { o.OnNext(value); }
                catch { /* must not break the manager */ }
            }
        }

        private void Remove(IObserver<IReadOnlySet<string>> observer)
        {
            lock (_lock) _observers.Remove(observer);
        }

        private sealed class Unsub(RunningInstancesSubject s, IObserver<IReadOnlySet<string>> o) : IDisposable
        {
            public void Dispose() => s.Remove(o);
        }
    }
}
