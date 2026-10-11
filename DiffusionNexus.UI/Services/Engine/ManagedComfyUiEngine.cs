using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services; // ChildProcessJobObject — same assembly, sibling namespace
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Outcome of trying to bring the engine up.</summary>
public sealed record EngineStartResult(bool IsRunning, string? BaseUrl, string? FailureReason, bool StoppedByRequest = false);

/// <summary>
/// Hosts the app-owned ComfyUI process. Bound to loopback on a dynamically allocated port so a
/// user's own ComfyUI on 8188 is never disturbed, started on demand, and killed when the app
/// exits. Health is confirmed against /system_stats before the engine is declared ready.
/// </summary>
public sealed class ManagedComfyUiEngine : IManagedEngineControl, IAsyncDisposable
{
    private static readonly ILogger Logger = Serilog.Log.ForContext<ManagedComfyUiEngine>();

    private readonly IUnifiedLogger? _unifiedLogger;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    private Process? _process;
    private string? _baseUrl;

    // Assigns the spawned process to a Windows Job Object configured with
    // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, exactly like PackageProcessManager does for the
    // installers it launches. Without this, the only thing keeping the engine from outliving
    // the app is App.axaml.cs's ShutdownRequested handler calling StopAsync() — any exit that
    // skips it (crash, Environment.FailFast, Task Manager kill on the parent) leaves a Python
    // process resident with multi-GB model weights in VRAM, invisible to the user. Held here
    // (not disposed alongside _process) because a *new* job object is created per engine start —
    // see StopCoreAsync, which terminates and disposes it together with the process.
    private ChildProcessJobObject? _jobObject;

    // Set for the duration of an in-flight EnsureRunningAsync call (under _startLock) so a
    // concurrent StopAsync can ask the readiness poll to wind down within a couple of seconds
    // instead of blocking on the lock for up to the full ~120 s poll window.
    private volatile bool _stopRequested;

    // Set by RequestRestart (e.g. after node packs were installed) and used up by the next
    // EnsureRunningAsync, under _startLock: a running engine is stopped there and started fresh,
    // never at request time, so a job running on the engine is not killed by the install. Cleared
    // only just before a fresh process is spawned, so a request made during a start survives it.
    private volatile bool _restartRequested;

    // Jobs running on the engine (BeginJob tokens not yet disposed). A pending restart waits for 0.
    private int _activeJobs;

    // Under _startLock: the current postponement was already logged, so a frequent caller (the
    // Canvas availability check) does not repeat it. Reset when the request is used up.
    private bool _postponementLogged;

    // 1 while the engine is reported running (ready), 0 otherwise. Changed only through
    // ReportRunning, so StateChanged fires once per change whoever starts or stops it.
    private int _reportedRunning;

    // 1 while a start runs (under _startLock: spawn and readiness wait), through ReportStarting.
    private int _reportedStarting;

    // Generations getting under way (BeginPreparing tokens not yet disposed). Counted for the
    // console's Stop (HasActiveJobs), never for a pending restart (ShouldRestartNow reads _activeJobs).
    private int _preparing;

    // Maintenance holds (TryBeginMaintenanceAsync: an update changing the engine's folder). No start while > 0.
    private int _maintenance;

    // Counts stops (the console's, the app's shutdown). A start that was waiting for the start lock when
    // one happened does not start the engine again: the user stopped it, a generation queued behind
    // the stop included.
    private int _stopGeneration;

    public ManagedComfyUiEngine(IUnifiedLogger? unifiedLogger)
    {
        _unifiedLogger = unifiedLogger;
    }

    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    public string? BaseUrl => _baseUrl;

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _reportedRunning) == 1;

    /// <inheritdoc />
    public bool IsStarting => Volatile.Read(ref _reportedStarting) == 1;

    /// <inheritdoc />
    public bool HasActiveJobs => Volatile.Read(ref _activeJobs) + Volatile.Read(ref _preparing) > 0;

    /// <inheritdoc />
    public event Action? StateChanged;

    /// <inheritdoc />
    public IDisposable BeginPreparing()
    {
        Interlocked.Increment(ref _preparing);
        return new CountToken(() => Interlocked.Decrement(ref _preparing));
    }

    /// <inheritdoc />
    public async Task<IDisposable?> TryBeginMaintenanceAsync()
    {
        if (IsRunning || IsStarting) return null;

        // Atomic with every start: a start checks the hold under the start lock. A booting start holds
        // that lock for minutes and is refused below; a brief holder (a call that only checks, a stop,
        // a crash's release) is waited out rather than reported as "in use".
        if (!await _startLock.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false)) return null;
        try
        {
            if (IsStarting || (_process is { } process && !HasExitedSafely(process))) return null;
            Interlocked.Increment(ref _maintenance);
            return new CountToken(() => Interlocked.Decrement(ref _maintenance));
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>A stop is under way: starts waiting for the lock now do not start the engine. Internal for tests.</summary>
    internal void NoteStopRequested() => Interlocked.Increment(ref _stopGeneration);

    /// <inheritdoc />
    public async Task<bool> StopIfIdleAsync()
    {
        await _startLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (HasActiveJobs) return false;
            NoteStopRequested();
            await StopCoreAsync().ConfigureAwait(false);
            return true;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>Records the running state and raises <see cref="StateChanged"/> when it changed. Internal for tests.</summary>
    internal void ReportRunning(bool running) => Report(ref _reportedRunning, running);

    /// <summary>Records the starting state and raises <see cref="StateChanged"/> when it changed. Internal for tests.</summary>
    internal void ReportStarting(bool starting) => Report(ref _reportedStarting, starting);

    private void Report(ref int field, bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref field, next) == next) return;
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            // A subscriber's failure must never break a start or a stop.
            Logger.Warning(ex, "A StateChanged subscriber failed.");
        }
    }

    /// <summary>Runs its action once, however often it is disposed.</summary>
    private sealed class CountToken(Action release) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) release();
        }
    }

    /// <summary>Hears the started process exit (<see cref="OnProcessExited"/>). Internal for tests.</summary>
    internal void WatchProcess(Process process)
    {
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnProcessExited(process);
    }

    /// <summary>
    /// The started process answered its readiness check. Under <see cref="_startLock"/>. Internal for tests.
    /// </summary>
    internal async Task<EngineStartResult> CompleteReadyAsync(Process process, string baseUrl)
    {
        // It answered, but may have died since: a dead process is never reported running. Checked
        // again after the report, because OnProcessExited leaves a process not yet reported running
        // to the start, so an exit between the two would otherwise go unseen.
        if (process.HasExited) return await FailExitedAfterReadyAsync(process).ConfigureAwait(false);
        _baseUrl = baseUrl;
        // Starting ends before running is reported: never both at once. From here an exit is the exit
        // handler's to report (it leaves an engine that is not running to the start), and the check
        // below catches one that came before the report.
        ReportStarting(false);
        ReportRunning(true);
        if (process.HasExited) return await FailExitedAfterReadyAsync(process).ConfigureAwait(false);

        Log($"Engine ready at {baseUrl}.");
        return new EngineStartResult(true, baseUrl, null);
    }

    private async Task<EngineStartResult> FailExitedAfterReadyAsync(Process process)
    {
        var code = TryExitCode(process);
        await StopCoreAsync().ConfigureAwait(false);
        return new EngineStartResult(false, null,
            $"The engine process exited right after it became ready (exit code {code?.ToString() ?? "unknown"}). " +
            "See the Unified Console for its output.");
    }

    private static int? TryExitCode(Process process)
    {
        try { return process.ExitCode; }
        catch (InvalidOperationException) { return null; }   // disposed meanwhile
    }

    // The process this engine started has exited. A start still in progress (not reported running
    // yet) reports the exit itself. Otherwise nobody stopped it: it exited on its own (a crash, or
    // killed from outside), so the engine is no longer running, and its handles are released.
    internal void OnProcessExited(Process exited)
    {
        if (!ReferenceEquals(_process, exited) || !IsRunning) return;
        _ = ReleaseExitedAsync(exited);
    }

    // Clears the dead process the same way a stop does (BaseUrl null, handles disposed, reported not
    // running), under the start lock so it never races a start or a stop. Logs the exit only when it
    // is the one that lets go: a start that got the lock first released and logged it already.
    private async Task ReleaseExitedAsync(Process exited)
    {
        try
        {
            await _startLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(_process, exited))
                {
                    LogExit(exited);
                    await StopCoreAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _startLock.Release();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not release the exited engine process.");
        }
    }

    /// <summary>
    /// Asks for a fresh process on the next <see cref="EnsureRunningAsync"/>. Stops nothing now: the
    /// next call stops a running engine and starts a new one; when the engine is not running, that
    /// call's ordinary cold start already loads everything and uses the request up.
    /// </summary>
    public void RequestRestart() => _restartRequested = true;

    /// <inheritdoc />
    public bool IsRestartPending => _restartRequested;

    /// <inheritdoc />
    public IDisposable BeginJob()
    {
        Interlocked.Increment(ref _activeJobs);
        return new CountToken(() => Interlocked.Decrement(ref _activeJobs));
    }

    /// <summary>
    /// Whether <see cref="EnsureRunningAsync"/> stops the running engine now: only when a restart is
    /// pending and no job is running on it. Internal for tests.
    /// </summary>
    internal static bool ShouldRestartNow(bool isRunning, bool restartPending, int activeJobs) =>
        isRunning && restartPending && activeJobs == 0;


    /// <summary>
    /// Starts the engine if it is not already running and waits until it answers /system_stats.
    /// Never throws for ordinary failures — the reason is returned so the Canvas can show it.
    /// </summary>
    public async Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct)
    {
        var stopsBeforeWaiting = Volatile.Read(ref _stopGeneration);
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Captured into a local and checked only inside the lock/try: a concurrent
            // StopAsync disposes _process, and reading .HasExited on a disposed Process throws
            // InvalidOperationException — this used to run as an unguarded fast path before the
            // lock was even taken, so that exception could escape this method uncaught.
            var current = _process;
            if (current is { HasExited: false } && _baseUrl is not null)
            {
                var restartPending = _restartRequested;
                if (!ShouldRestartNow(isRunning: true, restartPending, Volatile.Read(ref _activeJobs)))
                {
                    // A job running on the engine (another consumer's Generate) must not be killed:
                    // the restart waits, still pending, for a call made while the engine is idle.
                    if (restartPending && !_postponementLogged)
                    {
                        _postponementLogged = true;
                        Log("Engine restart postponed: a job is still running.");
                    }
                    return new EngineStartResult(true, _baseUrl, null);
                }

                Log("Restarting the engine so it loads the newly installed node packs or model folders...");
                await StopCoreAsync().ConfigureAwait(false);
            }
            else if (current is not null)
            {
                // A process that died since (a crash whose release has not run yet): released before
                // the fresh start, so it never reads as running while that start runs. Logged by
                // whichever release lets go of it, this one or the exit handler's: once.
                LogExit(current);
                await StopCoreAsync().ConfigureAwait(false);
            }

            // The user stopped the engine while this call waited (a confirmed Stop cancels a generation
            // getting under way too): it is not started again behind their back.
            if (Volatile.Read(ref _stopGeneration) != stopsBeforeWaiting)
                return new EngineStartResult(false, null, "The engine was stopped.", StoppedByRequest: true);

            // An update is changing the engine's folder: no start from it, whoever asks.
            if (Volatile.Read(ref _maintenance) > 0)
                return new EngineStartResult(false, null,
                    "The Diffusion Nexus Engine is being updated. Try again when the update has finished.");

            // Whatever starts below is a fresh process, so a pending restart request is used up. A
            // request that arrives from here on (during the up-to-2-minute start) stays set.
            _restartRequested = false;
            _postponementLogged = false;

            var mainPy = ManagedEngineLocator.ResolveMainPy(installRoot);
            if (mainPy is null)
                return new EngineStartResult(false, null,
                    $"No ComfyUI entry point (main.py) was found under '{installRoot}'. Install the engine first.");

            var python = ResolveVenvPython(Path.GetDirectoryName(mainPy)!) ?? ResolveVenvPython(installRoot);
            if (python is null)
                return new EngineStartResult(false, null,
                    $"The engine's Python environment was not found under '{installRoot}'. The install may be incomplete.");

            var port = AllocateFreePort();
            var startInfo = new ProcessStartInfo(python, BuildArguments(mainPy, port))
            {
                WorkingDirectory = Path.GetDirectoryName(mainPy)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (var (name, value) in EngineEnvironment)
                startInfo.Environment[name] = value;

            ReportStarting(true);
            Log($"Starting engine on 127.0.0.1:{port}...");
            _process = Process.Start(startInfo);
            if (_process is null)
                return new EngineStartResult(false, null, "The engine process could not be started.");

            // Assign to a Job Object with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, same as
            // PackageProcessManager does for every installer process it launches. This is what
            // actually protects against an orphaned engine — the ShutdownRequested handler in
            // App.axaml.cs is only reached on a clean exit, and this makes cleanup unconditional:
            // the OS kernel kills the process (and its tree) the moment the job handle closes,
            // even on a crash, Environment.FailFast, or a Task Manager kill of the app itself.
            // Best-effort: a failure here still leaves the process running under the normal
            // ShutdownRequested/StopAsync path, so it must never fail the start.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _jobObject = new ChildProcessJobObject(name: null);
                    if (!_jobObject.AssignProcess(_process))
                    {
                        Logger.Warning("Failed to assign the engine process (PID {Pid}) to its Job Object.", _process.Id);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to create a Job Object for the engine process; orphan protection disabled for this run.");
                }
            }

            try
            {
                WatchProcess(_process);
                _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                var baseUrl = $"http://127.0.0.1:{port}";
                var outcome = await WaitForReadyAsync(baseUrl, _process, ct).ConfigureAwait(false);
                switch (outcome.Kind)
                {
                    case ReadinessKind.Ready:
                        return await CompleteReadyAsync(_process, baseUrl).ConfigureAwait(false);

                    case ReadinessKind.ProcessExited:
                        await StopCoreAsync().ConfigureAwait(false);
                        return new EngineStartResult(false, null,
                            $"The engine process exited on its own during startup (exit code {outcome.ExitCode}). " +
                            "See the Unified Console for its output.");

                    case ReadinessKind.StopRequested:
                        await StopCoreAsync().ConfigureAwait(false);
                        return new EngineStartResult(false, null, "The engine was stopped while it was starting.", StoppedByRequest: true);

                    default: // TimedOut
                        await StopCoreAsync().ConfigureAwait(false);
                        return new EngineStartResult(false, null,
                            "The engine started but never became ready (no answer from /system_stats) within " +
                            "120 seconds. See the Unified Console for its output.");
                }
            }
            catch
            {
                // The process is already spawned at this point. Whatever went wrong — including
                // caller cancellation — must not leave it dangling in _process for the next
                // EnsureRunningAsync call to silently overwrite and orphan. Note this calls the
                // lock-free core, not the public StopAsync(): _startLock is already held here, and
                // StopAsync() also takes it (see its own doc comment), so calling it here would
                // deadlock against ourselves.
                await StopCoreAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to start the managed ComfyUI engine.");
            return new EngineStartResult(false, null, ex.Message);
        }
        finally
        {
            ReportStarting(false);
            _startLock.Release();
        }
    }

    // One line for an engine process that exited on its own (a crash, a kill from outside).
    private void LogExit(Process process)
    {
        var code = TryExitCode(process);
        Log(code is null ? "The engine process exited." : $"The engine process exited with code {code}.");
    }

    private static bool HasExitedSafely(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }   // disposed or never started
    }

    /// <summary>Why the readiness poll in <see cref="WaitForReadyAsync"/> stopped.</summary>
    private enum ReadinessKind
    {
        Ready,
        ProcessExited,
        StopRequested,
        TimedOut
    }

    /// <summary>
    /// Outcome of a readiness poll. Kept distinct from a plain bool so
    /// <see cref="EnsureRunningAsync"/> can give the caller a specific, human-readable reason
    /// (died vs. never answered vs. was stopped) instead of one generic "not ready" message.
    /// </summary>
    private readonly record struct ReadinessOutcome(ReadinessKind Kind, int ExitCode = 0)
    {
        public static readonly ReadinessOutcome Ready = new(ReadinessKind.Ready);
        public static readonly ReadinessOutcome StopRequested = new(ReadinessKind.StopRequested);
        public static readonly ReadinessOutcome TimedOut = new(ReadinessKind.TimedOut);
        public static ReadinessOutcome ProcessExited(int exitCode) => new(ReadinessKind.ProcessExited, exitCode);
    }

    /// <summary>
    /// Polls /system_stats until the engine answers, the process dies, a stop is requested, or
    /// ~120 s elapse. Logs progress every 10 attempts so a stall shows how far it got, per this
    /// project's standing rule that a hang must show its last successful step.
    /// </summary>
    private async Task<ReadinessOutcome> WaitForReadyAsync(string baseUrl, Process process, CancellationToken ct)
    {
        const int maxAttempts = 60;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Checked every iteration (at most a couple of seconds apart — see StopAsync) so a
            // concurrent stop request doesn't have to wait out the full ~120 s poll window.
            if (_stopRequested)
            {
                Log("Engine start aborted: a stop was requested while it was still becoming ready.");
                return ReadinessOutcome.StopRequested;
            }

            if (process.HasExited)
            {
                Log($"Engine process exited during startup with code {process.ExitCode}.");
                return ReadinessOutcome.ProcessExited(process.ExitCode);
            }

            if (attempt > 0 && attempt % 10 == 0)
                Log($"Still waiting for the engine to become ready (attempt {attempt}/{maxAttempts})...");

            try
            {
                using var response = await _httpClient.GetAsync($"{baseUrl}/system_stats", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return ReadinessOutcome.Ready;
            }
            catch (HttpRequestException)
            {
                // Not up yet — expected while the server binds.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // Per-request timeout, not caller cancellation.
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }

        return ReadinessOutcome.TimedOut;
    }

    /// <summary>
    /// Stops the engine if it is running. Safe to call repeatedly, and safe to call while a start
    /// is in flight: it signals <see cref="_stopRequested"/> so the readiness poll in
    /// <see cref="WaitForReadyAsync"/> winds down within its next iteration (at most a couple of
    /// seconds) rather than running to the full ~120 s timeout, then serializes on the same
    /// <see cref="_startLock"/> as <see cref="EnsureRunningAsync"/> before touching the process —
    /// without that, a stop landing mid-start could dispose the <see cref="Process"/> or the
    /// shared <see cref="HttpClient"/> out from under the in-flight poll. The reset back to false
    /// happens under the same lock acquisition that did the stop, immediately before releasing
    /// it — not after — so the flag and the lock are handed over together. Resetting it outside
    /// the lock (even in an outer <c>finally</c> a few instructions later) would leave a window
    /// where the lock is free but the flag is still true, letting a completely unrelated,
    /// freshly-invoked <see cref="EnsureRunningAsync"/> that acquires the lock in that gap read a
    /// stale "stop requested" and abort a start that has nothing to do with this stop.
    /// </summary>
    public async Task StopAsync()
    {
        NoteStopRequested();
        _stopRequested = true;
        try
        {
            await _startLock.WaitAsync().ConfigureAwait(false);
        }
        catch
        {
            // The lock was never acquired, so there's nothing to hand over — reset immediately.
            _stopRequested = false;
            throw;
        }

        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            // Reset before releasing, in the same finally, so the flag and the lock are handed
            // over together — never a free lock with a still-true flag for another start to see.
            _stopRequested = false;
            _startLock.Release();
        }
    }

    /// <summary>
    /// The actual kill-and-clear logic, without taking <see cref="_startLock"/>. Callers that
    /// already hold the lock (paths inside <see cref="EnsureRunningAsync"/> cleaning up after a
    /// failed start) must call this directly — calling the public <see cref="StopAsync"/> from
    /// there would deadlock waiting on a lock they themselves are holding.
    /// </summary>
    private async Task StopCoreAsync()
    {
        var process = _process;
        var jobObject = _jobObject;
        _process = null;
        _jobObject = null;
        _baseUrl = null;
        ReportRunning(false);

        if (process is null)
        {
            if (OperatingSystem.IsWindows()) jobObject?.Dispose();
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to stop the managed ComfyUI engine cleanly.");
        }
        finally
        {
            process.Dispose();
            // Disposing closes the job handle, which (per JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE)
            // also kills anything still assigned to it — belt-and-suspenders alongside the
            // explicit Kill above for the case where Kill itself failed.
            if (OperatingSystem.IsWindows()) jobObject?.Dispose();
        }
    }

    /// <summary>
    /// Allocates a free TCP port on loopback. Never 8188: that belongs to the user's own ComfyUI,
    /// and colliding with it is the one failure mode this engine must never cause.
    /// </summary>
    /// <remarks>
    /// Open-check-close-then-hand-to-child is an inherent TOCTOU race: another process could
    /// claim the port in the gap between <see cref="TcpListener.Stop"/> here and the child
    /// binding it. There is no race-free way to reserve a port for a process you don't yet
    /// control on Windows, and this is the same technique .NET's own <c>WebApplication</c>
    /// "dynamic port" helpers use — acceptable because a same-machine collision is rare and, if
    /// it ever happens, the engine simply fails to bind and <see cref="EnsureRunningAsync"/>
    /// reports it as a plain startup failure rather than silently misbehaving.
    /// </remarks>
    public static int AllocateFreePort()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            if (port != 8188) return port;
        }

        throw new InvalidOperationException("Could not allocate a free TCP port for the engine.");
    }

    /// <summary>
    /// Environment variables the engine process starts with. <c>HF_HUB_USER_AGENT_ORIGIN</c> works
    /// around a library bug (#607): ComfyUI's main.py disables Hugging Face telemetry, with which
    /// <c>kernels</c> 0.17 passes an empty user-agent part that huggingface_hub turns into a header
    /// ending in "; ". httpx rejects that header, so the publisher trust check of the FP8 kernel
    /// fails and the Qwen3-VL node (Outpaint Vision) cannot run. An origin is appended after the empty
    /// part, so the header no longer ends in whitespace.
    /// </summary>
    public static IReadOnlyDictionary<string, string> EngineEnvironment { get; } = new Dictionary<string, string>
    {
        ["HF_HUB_USER_AGENT_ORIGIN"] = "diffusion-nexus-engine",
    };

    /// <summary>Command line for the engine: loopback-only, private port, no browser.</summary>
    public static string BuildArguments(string mainPyPath, int port) =>
        $"\"{mainPyPath}\" --listen 127.0.0.1 --port {port} --disable-auto-launch";

    /// <summary>The engine venv's interpreter, or null when it does not exist.</summary>
    public static string? ResolveVenvPython(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;

        // TODO: Linux Implementation - venv/bin/python
        var windows = Path.Combine(installRoot, "venv", "Scripts", "python.exe");
        return File.Exists(windows) ? windows : null;
    }

    private void Log(string message)
    {
        Logger.Information("Engine: {Message}", message);
        // InstanceManagement fits best: its doc comment covers exactly this — start/stop and
        // stdout/stderr of a managed process — better than Installation, which is for setup/update.
        _unifiedLogger?.Info(LogCategory.InstanceManagement, "Diffusion Nexus Engine", message);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _httpClient.Dispose();
        _startLock.Dispose();
    }
}
