using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services; // ChildProcessJobObject — same assembly, sibling namespace
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>Outcome of trying to bring the engine up.</summary>
public sealed record EngineStartResult(bool IsRunning, string? BaseUrl, string? FailureReason);

/// <summary>
/// Hosts the app-owned ComfyUI process. Bound to loopback on a dynamically allocated port so a
/// user's own ComfyUI on 8188 is never disturbed, started on demand, and killed when the app
/// exits. Health is confirmed against /system_stats before the engine is declared ready.
/// </summary>
public sealed class ManagedComfyUiEngine : IAsyncDisposable
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

    public ManagedComfyUiEngine(IUnifiedLogger? unifiedLogger)
    {
        _unifiedLogger = unifiedLogger;
    }

    /// <summary>Base URL of the running engine, or null when it is not running.</summary>
    public string? BaseUrl => _baseUrl;

    /// <summary>
    /// Starts the engine if it is not already running and waits until it answers /system_stats.
    /// Never throws for ordinary failures — the reason is returned so the Canvas can show it.
    /// </summary>
    public async Task<EngineStartResult> EnsureRunningAsync(string installRoot, CancellationToken ct)
    {
        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Captured into a local and checked only inside the lock/try: a concurrent
            // StopAsync disposes _process, and reading .HasExited on a disposed Process throws
            // InvalidOperationException — this used to run as an unguarded fast path before the
            // lock was even taken, so that exception could escape this method uncaught.
            var current = _process;
            if (current is { HasExited: false } && _baseUrl is not null)
                return new EngineStartResult(true, _baseUrl, null);

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
                _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
                _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                var baseUrl = $"http://127.0.0.1:{port}";
                var outcome = await WaitForReadyAsync(baseUrl, _process, ct).ConfigureAwait(false);
                switch (outcome.Kind)
                {
                    case ReadinessKind.Ready:
                        _baseUrl = baseUrl;
                        Log($"Engine ready at {baseUrl}.");
                        return new EngineStartResult(true, baseUrl, null);

                    case ReadinessKind.ProcessExited:
                        await StopCoreAsync().ConfigureAwait(false);
                        return new EngineStartResult(false, null,
                            $"The engine process exited on its own during startup (exit code {outcome.ExitCode}). " +
                            "See the Unified Console for its output.");

                    case ReadinessKind.StopRequested:
                        await StopCoreAsync().ConfigureAwait(false);
                        return new EngineStartResult(false, null, "The engine was stopped while it was starting.");

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
            _startLock.Release();
        }
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
