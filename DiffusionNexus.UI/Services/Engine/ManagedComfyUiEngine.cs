using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DiffusionNexus.Domain.Services.UnifiedLogging;
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
        if (_process is { HasExited: false } && _baseUrl is not null)
            return new EngineStartResult(true, _baseUrl, null);

        await _startLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_process is { HasExited: false } && _baseUrl is not null)
                return new EngineStartResult(true, _baseUrl, null);

            var mainPy = ResolveMainPy(installRoot);
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

            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Log(e.Data); };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var baseUrl = $"http://127.0.0.1:{port}";
            var ready = await WaitForReadyAsync(baseUrl, _process, ct).ConfigureAwait(false);
            if (!ready)
            {
                await StopAsync().ConfigureAwait(false);
                return new EngineStartResult(false, null,
                    "The engine started but never became ready (no answer from /system_stats). " +
                    "See the Unified Console for its output.");
            }

            _baseUrl = baseUrl;
            Log($"Engine ready at {baseUrl}.");
            return new EngineStartResult(true, baseUrl, null);
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

    /// <summary>Polls /system_stats until the engine answers, the process dies, or ~120 s elapse.</summary>
    private async Task<bool> WaitForReadyAsync(string baseUrl, Process process, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                Log($"Engine process exited during startup with code {process.ExitCode}.");
                return false;
            }

            try
            {
                using var response = await _httpClient.GetAsync($"{baseUrl}/system_stats", ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode) return true;
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

        return false;
    }

    /// <summary>Stops the engine if it is running. Safe to call repeatedly.</summary>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        _baseUrl = null;

        if (process is null) return;

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
        }
    }

    /// <summary>
    /// Allocates a free TCP port on loopback. Never 8188: that belongs to the user's own ComfyUI,
    /// and colliding with it is the one failure mode this engine must never cause.
    /// </summary>
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

    private static string? ResolveMainPy(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot)) return null;

        var direct = Path.Combine(installRoot, "main.py");
        if (File.Exists(direct)) return direct;

        var nested = Path.Combine(installRoot, "ComfyUI", "main.py");
        return File.Exists(nested) ? nested : null;
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
