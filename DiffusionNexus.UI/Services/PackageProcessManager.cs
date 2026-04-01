using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using DiffusionNexus.Domain.Enums;

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
    /// <param name="installerType">The installer type, used for type-specific launch logic.</param>
    public void Launch(int packageId, string executablePath, string workingDirectory, string arguments, InstallerType installerType = InstallerType.Unknown)
    {
        if (_processes.TryGetValue(packageId, out var existing) && existing.IsRunning)
            return;

        var mp = new ManagedProcess(packageId, executablePath, workingDirectory, arguments, _jobObject, installerType);
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
            var installerType = mp.Type;

            await mp.StopAsync();

            // Brief delay so the port is released
            await Task.Delay(500);

            Launch(packageId, executablePath, workingDirectory, arguments, installerType);
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
    /// For AIToolkit, orchestrates a multi-stage Node.js launch sequence
    /// instead of running the .bat file (which opens a browser prematurely).
    /// </summary>
    private sealed class ManagedProcess : IDisposable
    {
        // Matches URLs like http://127.0.0.1:7860 or http://0.0.0.0:8188
        private static readonly Regex UrlRegex = new(
            @"https?://[\d\.]+:\d+",
            RegexOptions.Compiled);

        // Shared HttpClient for URL readiness probes (connection-refused = not ready)
        private static readonly HttpClient s_probeClient = new()
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        private readonly int _packageId;
        private readonly ChildProcessJobObject? _jobObject;
        private readonly List<ConsoleOutputLine> _outputLines = [];
        private readonly object _lock = new();
        private Process? _process;
        private CancellationTokenSource? _urlProbeCts;
        private int _lineCounter;

        // 3-way barrier: stdout EOF + stderr EOF + Exited event.
        // Process.Exited can fire before all async output callbacks complete,
        // so we only signal RunningStateChanged(false) when all three arrive.
        private int _exitBarrier;

        public string ExecutablePath { get; }
        public string WorkingDirectory { get; }
        public string Arguments { get; }
        public InstallerType Type { get; }
        public string? DetectedUrl { get; private set; }
        public bool IsRunning => _process is { HasExited: false };
        public IReadOnlyList<ConsoleOutputLine> OutputLines
        {
            get { lock (_lock) return [.. _outputLines]; }
        }

        public event Action<ConsoleOutputLine>? OutputReceived;
        public event Action<bool>? RunningStateChanged;
        public event Action<string>? WebUrlDetected;

        public ManagedProcess(int packageId, string executablePath, string workingDirectory, string arguments, ChildProcessJobObject? jobObject, InstallerType installerType)
        {
            _packageId = packageId;
            _jobObject = jobObject;
            ExecutablePath = executablePath;
            WorkingDirectory = workingDirectory;
            Arguments = arguments;
            Type = installerType;
        }

        public void Start()
        {
            // Cancel any pending URL readiness probe from a previous run
            _urlProbeCts?.Cancel();
            _urlProbeCts?.Dispose();
            _urlProbeCts = null;

            lock (_lock)
            {
                _outputLines.Clear();
                _lineCounter = 0;
                DetectedUrl = null;
            }

            Interlocked.Exchange(ref _exitBarrier, 0);

            if (Type == InstallerType.AIToolkit)
            {
                // AI Toolkit: run the same npm chain as the batch file,
                // but without the final browser-opening step.
                StartAIToolkit();
                return;
            }

            StartGenericProcess();
        }

        /// <summary>
        /// Generic process start for all installer types except AIToolkit.
        /// </summary>
        private void StartGenericProcess()
        {
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

            // Suppress auto-browser-opening in child processes.
            // DiffusionNexus handles browser timing via the URL readiness probe
            // so the "Web UI" button only appears once the server is actually listening.
            // BROWSER=none  — Node.js / npm / Next.js / CRA / Vite ecosystem
            psi.Environment["BROWSER"] = "none";

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

        /// <summary>
        /// AI Toolkit launch that replaces the Start-AI-Toolkit.bat file.
        /// Runs <c>npm run build_and_start</c> through <c>cmd.exe</c> in the ui/ directory — exactly what the
        /// .bat does after <c>cd ./AI-Toolkit &amp;&amp; call venv\Scripts\activate.bat &amp;&amp; cd ui</c>.
        /// That single npm script chains: npm install → prisma generate → prisma db push
        /// → tsc + next build → concurrently (worker + next start --port 8675).
        /// All output streams to the unified logger. No browser is opened.
        /// </summary>
        private void StartAIToolkit()
        {
            // The InstallationPath (WorkingDirectory) may be the parent folder
            // (e.g. E:\AI\Toolkit) while the actual ai-toolkit code lives in a
            // subfolder (e.g. E:\AI\Toolkit\AI-Toolkit) that contains toolkit/ + run.py.
            // Mirror the same detection the .bat file does: cd ./AI-Toolkit → cd ui.
            var aiToolkitRoot = ResolveAIToolkitRoot(WorkingDirectory);
            if (aiToolkitRoot is null)
            {
                AddLine("[ERROR] Cannot locate AI Toolkit root (expected a folder with 'toolkit/' and 'run.py').", isError: true);
                AddLine("        Searched: " + WorkingDirectory, isError: true);
                RunningStateChanged?.Invoke(false);
                return;
            }

            var uiDir = Path.Combine(aiToolkitRoot, "ui");
            if (!Directory.Exists(uiDir))
            {
                AddLine("[ERROR] AI Toolkit 'ui' folder not found. Expected at: " + uiDir, isError: true);
                RunningStateChanged?.Invoke(false);
                return;
            }

            var packageJsonPath = Path.Combine(uiDir, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                AddLine("[ERROR] AI Toolkit package.json not found. Expected at: " + packageJsonPath, isError: true);
                RunningStateChanged?.Invoke(false);
                return;
            }

            // Locate the Python virtual environment (same as: call venv\Scripts\activate.bat)
            var venvDir = Path.Combine(aiToolkitRoot, "venv");
            // TODO: Linux Implementation - use venv/bin instead of venv\Scripts on Linux
            var venvScriptsDir = Path.Combine(venvDir, "Scripts");
            var hasVenv = Directory.Exists(venvScriptsDir);
            if (!hasVenv)
            {
                AddLine("[WARNING] Python venv not found at: " + venvDir, isError: true);
                AddLine("         The worker process may fail without the virtual environment.", isError: true);
            }

            var commandInterpreter = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrWhiteSpace(commandInterpreter))
                commandInterpreter = "cmd.exe";

            AddLine("═══════════════════════════════════════════════", isError: false);
            AddLine("  AI Toolkit — DiffusionNexus Direct Launch", isError: false);
            AddLine("═══════════════════════════════════════════════", isError: false);
            AddLine("Running: cmd.exe /d /c npm run build_and_start", isError: false);
            AddLine("  (npm install → prisma db setup → build → server start)", isError: false);
            AddLine($"  Working directory: {uiDir}", isError: false);
            if (hasVenv)
                AddLine($"  Python venv: {venvDir}", isError: false);

            var psi = new ProcessStartInfo
            {
                FileName = commandInterpreter,
                Arguments = "/d /c npm run build_and_start",
                WorkingDirectory = uiDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true
            };

            // Isolate from system Python (same env vars the .bat file clears)
            foreach (var key in new[] { "PYTHONPATH", "PYTHONHOME", "PYTHON", "PYTHONSTARTUP",
                "PYTHONUSERBASE", "PIP_CONFIG_FILE", "PIP_REQUIRE_VIRTUALENV",
                "VIRTUAL_ENV", "CONDA_PREFIX", "CONDA_DEFAULT_ENV",
                "PYENV_ROOT", "PYENV_VERSION" })
            {
                psi.Environment[key] = "";
            }
            psi.Environment["GIT_LFS_SKIP_SMUDGE"] = "1";
            // Suppress auto-browser-opening in Node.js ecosystem
            psi.Environment["BROWSER"] = "none";

            // Activate the Python virtual environment by replicating what
            // venv\Scripts\activate.bat does: set VIRTUAL_ENV and prepend
            // the venv's Scripts directory to PATH so the venv Python is found first.
            if (hasVenv)
            {
                psi.Environment["VIRTUAL_ENV"] = venvDir;
                var currentPath = psi.Environment.TryGetValue("PATH", out var existingPath)
                    ? existingPath
                    : Environment.GetEnvironmentVariable("PATH") ?? "";
                psi.Environment["PATH"] = venvScriptsDir + Path.PathSeparator + currentPath;
            }

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;
            _process.Exited += OnProcessExited;

            try
            {
                _process.Start();
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();

                AddLine($"[Started AI Toolkit launcher PID {_process.Id}]", isError: false);

                // Assign to Job Object so the OS kills npm + node + next + worker
                // if our app crashes or is killed
                if (_jobObject is not null && OperatingSystem.IsWindows())
                {
                    _jobObject.AssignProcess(_process);
                }

                Serilog.Log.Information(
                    "PackageProcessManager: AI Toolkit 'npm run build_and_start' started (PID {Pid}) for package {PackageId}",
                    _process.Id, _packageId);

                RunningStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "PackageProcessManager: AI Toolkit launch failed for package {PackageId}", _packageId);
                AddLine($"[ERROR] AI Toolkit launch failed: {ex.Message}", isError: true);
                RunningStateChanged?.Invoke(false);
            }
        }

        /// <summary>
        /// Resolves the actual AI Toolkit root directory (containing toolkit/ + run.py).
        /// The InstallationPath may be the direct root or a parent directory.
        /// </summary>
        private static string? ResolveAIToolkitRoot(string basePath)
        {
            // Check if basePath itself is the AI Toolkit root
            if (Directory.Exists(Path.Combine(basePath, "toolkit"))
                && File.Exists(Path.Combine(basePath, "run.py")))
            {
                return basePath;
            }

            // Check immediate subfolders (same logic as AddExistingInstallationDialogViewModel)
            try
            {
                foreach (var subDir in Directory.EnumerateDirectories(basePath))
                {
                    if (Directory.Exists(Path.Combine(subDir, "toolkit"))
                        && File.Exists(Path.Combine(subDir, "run.py")))
                    {
                        return subDir;
                    }
                }
            }
            catch (IOException) { /* Directory access error */ }
            catch (UnauthorizedAccessException) { /* Permission denied */ }

            return null;
        }

        public async Task StopAsync()
        {
            _urlProbeCts?.Cancel();

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
                    // Graceful shutdown timed out — force kill the entire process tree
                    Serilog.Log.Warning("PackageProcessManager: Force-killing process tree {Pid}", _process.Id);
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
            else
                OnExitPartCompleted(); // stdout EOF sentinel
        }

        private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data is not null)
                AddLine(e.Data, isError: true);
            else
                OnExitPartCompleted(); // stderr EOF sentinel
        }

        private void OnProcessExited(object? sender, EventArgs e)
        {
            OnExitPartCompleted(); // process exited signal
        }

        /// <summary>
        /// Called when one of the three exit signals arrives (stdout EOF, stderr EOF,
        /// process exited). Only the third signal triggers the actual exit logic,
        /// ensuring all buffered output has been drained first.
        /// </summary>
        private void OnExitPartCompleted()
        {
            // Only the third signal (stdout + stderr + exited) fires the exit logic
            if (Interlocked.Increment(ref _exitBarrier) < 3)
                return;

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

            // Detect URLs in output — probe readiness before notifying
            var match = UrlRegex.Match(text);
            if (match.Success && DetectedUrl is null)
            {
                _ = ProbeUrlThenNotifyAsync(match.Value);
            }

            OutputReceived?.Invoke(line);
        }

        /// <summary>
        /// Probes the detected URL with HTTP GET until the server responds,
        /// then fires <see cref="WebUrlDetected"/>. This prevents premature
        /// browser opens for processes (e.g. AI Toolkit) whose URL appears in
        /// log output before the web server is actually listening.
        /// </summary>
        private async Task ProbeUrlThenNotifyAsync(string url)
        {
            // Prevent duplicate probes
            if (DetectedUrl is not null) return;

            _urlProbeCts = new CancellationTokenSource();
            var ct = _urlProbeCts.Token;

            // 0.0.0.0 isn't routable for a probe; use loopback instead
            var probeUrl = url.Replace("://0.0.0.0:", "://127.0.0.1:");

            Serilog.Log.Debug("PackageProcessManager: Probing URL {Url} for package {PackageId}",
                probeUrl, _packageId);

            for (int attempt = 0; attempt < 120; attempt++) // Up to ~2 minutes
            {
                if (ct.IsCancellationRequested || _process is null or { HasExited: true })
                    break;

                try
                {
                    using var response = await s_probeClient.GetAsync(
                        probeUrl, HttpCompletionOption.ResponseHeadersRead, ct);

                    // Any HTTP response means the server is listening
                    DetectedUrl = url;
                    WebUrlDetected?.Invoke(url);

                    Serilog.Log.Information(
                        "PackageProcessManager: URL {Url} ready for package {PackageId} after {Attempts} probe(s)",
                        url, _packageId, attempt + 1);
                    return;
                }
                catch (HttpRequestException)
                {
                    // Connection refused — server not ready yet
                }
                catch (TaskCanceledException) when (!ct.IsCancellationRequested)
                {
                    // HTTP timeout — server not responding yet
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                try
                {
                    await Task.Delay(1000, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            // Fallback: surface the URL anyway so the button eventually appears
            if (DetectedUrl is null)
            {
                DetectedUrl = url;
                WebUrlDetected?.Invoke(url);

                Serilog.Log.Warning(
                    "PackageProcessManager: URL probe timed out for {Url} on package {PackageId}; showing button anyway",
                    url, _packageId);
            }
        }

        public void Dispose()
        {
            _urlProbeCts?.Cancel();
            _urlProbeCts?.Dispose();

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
