using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Represents a single line of console output from a managed process.
/// </summary>
/// <param name="LineNumber">1-based line number.</param>
/// <param name="Text">The text content of the line.</param>
/// <param name="IsError">True if this came from stderr.</param>
/// <param name="Timestamp">When the line was received.</param>
public sealed record ConsoleOutputLine(int LineNumber, string Text, bool IsError, DateTime Timestamp);

/// <summary>
/// Manages the lifecycle of child processes for installer packages.
/// Provides launch, stop, restart, stdout/stderr capture, and URL detection.
/// </summary>
public sealed class PackageProcessManager : IDisposable
{
    private readonly ConcurrentDictionary<int, ManagedProcess> _processes = new();
    private readonly ChildProcessJobObject? _jobObject;

    public PackageProcessManager()
    {
        // Create a Windows Job Object so the OS kernel kills all child processes
        // when this process exits — even on crash or Task Manager kill.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                _jobObject = new ChildProcessJobObject();
                Serilog.Log.Information("PackageProcessManager: Job Object created for child process tracking");
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "PackageProcessManager: Failed to create Job Object; orphan protection disabled");
            }
        }
        // TODO: Linux Implementation - use prctl(PR_SET_PDEATHSIG) for equivalent behavior
    }

    /// <summary>
    /// Raised on the thread pool when a new console line is received.
    /// The int parameter is the package ID.
    /// </summary>
    public event Action<int, ConsoleOutputLine>? OutputReceived;

    /// <summary>
    /// Raised when a process starts or exits.
    /// The int parameter is the package ID, the bool is true if running.
    /// </summary>
    public event Action<int, bool>? RunningStateChanged;

    /// <summary>
    /// Raised when a URL is detected in the process output (e.g. "Running on http://127.0.0.1:7860").
    /// </summary>
    public event Action<int, string>? WebUrlDetected;

    /// <summary>
    /// Checks whether a process is currently running for the given package.
    /// </summary>
    public bool IsRunning(int packageId)
    {
        return _processes.TryGetValue(packageId, out var mp) && mp.IsRunning;
    }

    /// <summary>
    /// Gets all captured console lines for a package, or empty if none.
    /// </summary>
    public IReadOnlyList<ConsoleOutputLine> GetOutput(int packageId)
    {
        if (_processes.TryGetValue(packageId, out var mp))
            return mp.OutputLines;
        return [];
    }

    /// <summary>
    /// Gets the detected web URL for a package, or null.
    /// </summary>
    public string? GetDetectedUrl(int packageId)
    {
        return _processes.TryGetValue(packageId, out var mp) ? mp.DetectedUrl : null;
    }

    /// <summary>
    /// Launches a process for the given package. If already running, does nothing.
    /// </summary>
    /// <param name="packageId">The database ID of the package.</param>
    /// <param name="executablePath">Full path to the executable/script.</param>
    /// <param name="workingDirectory">Working directory for the process.</param>
    /// <param name="arguments">Command-line arguments.</param>
    public void Launch(int packageId, string executablePath, string workingDirectory, string arguments)
    {
        if (_processes.TryGetValue(packageId, out var existing) && existing.IsRunning)
            return;

        var mp = new ManagedProcess(packageId, executablePath, workingDirectory, arguments, _jobObject);
        mp.OutputReceived += (line) => OutputReceived?.Invoke(packageId, line);
        mp.RunningStateChanged += (running) => RunningStateChanged?.Invoke(packageId, running);
        mp.WebUrlDetected += (url) => WebUrlDetected?.Invoke(packageId, url);

        _processes[packageId] = mp;
        mp.Start();
    }

    /// <summary>
    /// Stops the process for the given package.
    /// </summary>
    public async Task StopAsync(int packageId)
    {
        if (_processes.TryGetValue(packageId, out var mp))
            await mp.StopAsync();
    }

    /// <summary>
    /// Stops and relaunches the process for the given package.
    /// </summary>
    public async Task RestartAsync(int packageId)
    {
        if (_processes.TryGetValue(packageId, out var mp))
        {
            var executablePath = mp.ExecutablePath;
            var workingDirectory = mp.WorkingDirectory;
            var arguments = mp.Arguments;

            await mp.StopAsync();

            // Brief delay so the port is released
            await Task.Delay(500);

            Launch(packageId, executablePath, workingDirectory, arguments);
        }
    }

    public void Dispose()
    {
        foreach (var mp in _processes.Values)
        {
            mp.Dispose();
        }
        _processes.Clear();

        // Disposing the job handle will kill any remaining child processes
        if (OperatingSystem.IsWindows())
            _jobObject?.Dispose();
    }

    /// <summary>
    /// Internal wrapper around a single child process.
    /// </summary>
    private sealed class ManagedProcess : IDisposable
    {
        // Matches URLs like http://127.0.0.1:7860 or http://0.0.0.0:8188
        private static readonly Regex UrlRegex = new(
            @"https?://[\d\.]+:\d+",
            RegexOptions.Compiled);

        private readonly int _packageId;
        private readonly ChildProcessJobObject? _jobObject;
        private readonly List<ConsoleOutputLine> _outputLines = [];
        private readonly object _lock = new();
        private Process? _process;
        private int _lineCounter;

        public string ExecutablePath { get; }
        public string WorkingDirectory { get; }
        public string Arguments { get; }
        public string? DetectedUrl { get; private set; }
        public bool IsRunning => _process is { HasExited: false };
        public IReadOnlyList<ConsoleOutputLine> OutputLines
        {
            get { lock (_lock) return [.. _outputLines]; }
        }

        public event Action<ConsoleOutputLine>? OutputReceived;
        public event Action<bool>? RunningStateChanged;
        public event Action<string>? WebUrlDetected;

        public ManagedProcess(int packageId, string executablePath, string workingDirectory, string arguments, ChildProcessJobObject? jobObject)
        {
            _packageId = packageId;
            _jobObject = jobObject;
            ExecutablePath = executablePath;
            WorkingDirectory = workingDirectory;
            Arguments = arguments;
        }

        public void Start()
        {
            lock (_lock)
            {
                _outputLines.Clear();
                _lineCounter = 0;
                DetectedUrl = null;
            }

            // TODO: Linux Implementation - handle shell scripts differently on Linux
            var psi = new ProcessStartInfo
            {
                FileName = ExecutablePath,
                WorkingDirectory = WorkingDirectory,
                Arguments = Arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.Exited += OnProcessExited;

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                // Assign to Job Object so the OS kills this process if our app crashes
                if (_jobObject is not null && OperatingSystem.IsWindows())
                {
                    if (_jobObject.AssignProcess(_process))
                    {
                        Serilog.Log.Debug("PackageProcessManager: Process {Pid} assigned to Job Object", _process.Id);
                    }
                }

                Serilog.Log.Information("PackageProcessManager: Started process {Pid} for package {PackageId}",
                    _process.Id, _packageId);

                RunningStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "PackageProcessManager: Failed to start process for package {PackageId}", _packageId);
                AddLine($"[ERROR] Failed to start: {ex.Message}", isError: true);
                RunningStateChanged?.Invoke(false);
            }
        }

        public async Task StopAsync()
        {
            if (_process is null || _process.HasExited)
                return;

            try
            {
                Serilog.Log.Information("PackageProcessManager: Stopping process {Pid} for package {PackageId}",
                    _process.Id, _packageId);

                // Try graceful shutdown first via Ctrl+C / SIGINT
                // TODO: Linux Implementation - send SIGTERM on Linux
                try
                {
                    _process.StandardInput.Close();
                }
                catch
                {
                    // stdin may already be closed
                }

                // Wait up to 5 seconds for graceful exit
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Graceful shutdown timed out — force kill
                    Serilog.Log.Warning("PackageProcessManager: Force-killing process {Pid}", _process.Id);
                    _process.Kill(entireProcessTree: true);
                }

                AddLine("[Process stopped]", isError: false);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "PackageProcessManager: Error stopping process for package {PackageId}", _packageId);
                AddLine($"[ERROR] Stop failed: {ex.Message}", isError: true);
            }
        }

        private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
                AddLine(e.Data, isError: false);
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
                AddLine(e.Data, isError: true);
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            var exitCode = _process?.ExitCode ?? -1;
            AddLine($"[Process exited with code {exitCode}]", isError: exitCode != 0);
            RunningStateChanged?.Invoke(false);

            Serilog.Log.Information("PackageProcessManager: Process for package {PackageId} exited with code {ExitCode}",
                _packageId, exitCode);
        }

        private void AddLine(string text, bool isError)
        {
            ConsoleOutputLine line;
            lock (_lock)
            {
                _lineCounter++;
                line = new ConsoleOutputLine(_lineCounter, text, isError, DateTime.Now);
                _outputLines.Add(line);
            }

            // Detect URLs in output
            var match = UrlRegex.Match(text);
            if (match.Success && DetectedUrl is null)
            {
                DetectedUrl = match.Value;
                WebUrlDetected?.Invoke(DetectedUrl);
            }

            OutputReceived?.Invoke(line);
        }

        public void Dispose()
        {
            if (_process is not null)
            {
                try
                {
                    if (!_process.HasExited)
                        _process.Kill(entireProcessTree: true);
                }
                catch { /* best effort */ }

                _process.OutputDataReceived -= OnOutputDataReceived;
                _process.ErrorDataReceived -= OnErrorDataReceived;
                _process.Exited -= OnProcessExited;
                _process.Dispose();
                _process = null;
            }
        }
    }
}
