using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Update service for AI-Toolkit installations. Mirrors the AI-Toolkit
/// update.bat: pull the repo, then reinstall the Python packages whose
/// version constraints commonly drift (diffusers / huggingface-hub /
/// transformers) and finally re-run requirements.txt. Prefers <c>uv</c>
/// from the venv when available and falls back to <c>pip</c> otherwise.
/// </summary>
public sealed class AIToolkitUpdateService : IInstallerUpdateService
{
    private static readonly ILogger Logger = Log.ForContext<AIToolkitUpdateService>();

    // Collapses concurrent CheckForUpdatesAsync calls for the same path to a single
    // git fetch. UnifiedConsoleViewModel and InstallerManagerViewModel both fan out
    // update checks at startup against the same installations — without this they
    // race on ref updates and one fetch fails with "incorrect old value provided".
    private readonly ConcurrentDictionary<string, Lazy<Task<UpdateCheckResult>>> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public IReadOnlySet<InstallerType> SupportedTypes { get; } =
        new HashSet<InstallerType> { InstallerType.AIToolkit };

    /// <inheritdoc />
    public Task<UpdateCheckResult> CheckForUpdatesAsync(
        string installationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);

        var key = Path.GetFullPath(installationPath);
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<UpdateCheckResult>>(
            () => RunAndRemoveAsync(k, progress, ct)));
        return lazy.Value;
    }

    private async Task<UpdateCheckResult> RunAndRemoveAsync(
        string installationPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        try
        {
            return await CheckForUpdatesCoreAsync(installationPath, progress, ct);
        }
        finally
        {
            _inflight.TryRemove(installationPath, out _);
        }
    }

    private async Task<UpdateCheckResult> CheckForUpdatesCoreAsync(
        string installationPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var repoDir = ResolveRepoDir(installationPath);
        if (repoDir is null)
        {
            Logger.Warning("No git repository found at {Path} or its subfolders", installationPath);
            return new UpdateCheckResult(false, null, null, "No git repository found");
        }

        Logger.Information("Checking AI-Toolkit for updates in {Dir} (installation: {Path})", repoDir, installationPath);
        progress?.Report("Fetching latest changes...");

        var fetchResult = await RunGitAsync(repoDir, "fetch --all", progress, ct);
        if (!fetchResult.Success)
        {
            Logger.Warning("git fetch failed in {Dir}: {Output}", repoDir, fetchResult.Output);
            return new UpdateCheckResult(false, null, null, $"Fetch failed: {fetchResult.Output}");
        }

        var localHash = await RunGitAsync(repoDir, "rev-parse --short HEAD", progress: null, ct);
        var (branch, _) = await ResolveBranchAsync(repoDir, ct);

        var remoteHash = await RunGitAsync(repoDir, $"rev-parse --short origin/{branch}", progress: null, ct);
        if (!remoteHash.Success)
        {
            Logger.Warning("Remote branch origin/{Branch} not found in {Dir}", branch, repoDir);
            return new UpdateCheckResult(false,
                localHash.Success ? localHash.Output.Trim() : null,
                null,
                $"Remote branch origin/{branch} not found");
        }

        var behindResult = await RunGitAsync(repoDir, $"rev-list --count HEAD..origin/{branch}", progress: null, ct);
        var behindCount = behindResult.Success && int.TryParse(behindResult.Output.Trim(), out var n) ? n : 0;
        var isUpdateAvailable = behindCount > 0;

        var summary = isUpdateAvailable
            ? $"{behindCount} commit{(behindCount == 1 ? "" : "s")} behind origin/{branch}"
            : "Up to date";

        Logger.Information("AI-Toolkit update check for {Dir}: {Summary}", repoDir, summary);
        progress?.Report(summary);

        return new UpdateCheckResult(
            IsUpdateAvailable: isUpdateAvailable,
            CurrentHash: localHash.Success ? localHash.Output.Trim() : null,
            RemoteHash: remoteHash.Output.Trim(),
            Summary: summary);
    }

    /// <inheritdoc />
    public async Task<UpdateResult> UpdateAsync(
        string installationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);

        var repoDir = ResolveRepoDir(installationPath);
        if (repoDir is null)
            return new UpdateResult(false, null, "No git repository found");

        // ── Step 1: Pull ──
        progress?.Report("Pulling latest changes from git...");
        Logger.Information("Updating AI-Toolkit at {Path}", repoDir);

        var preHash = await RunGitAsync(repoDir, "rev-parse --short HEAD", progress: null, ct);
        var pullResult = await PullDirectoryAsync(repoDir, progress, ct);
        if (!pullResult.Success)
            return new UpdateResult(false, null, $"Pull failed: {pullResult.Output}");

        var postHash = await RunGitAsync(repoDir, "rev-parse --short HEAD", progress: null, ct);
        var hash = postHash.Success ? postHash.Output.Trim() : null;

        var hasChanges = preHash.Success && postHash.Success &&
            !string.Equals(preHash.Output.Trim(), postHash.Output.Trim(), StringComparison.Ordinal);

        if (!hasChanges)
        {
            progress?.Report("Already up to date — skipping reinstall.");
            return new UpdateResult(true, hash, "Already up to date");
        }

        // ── Step 2: Reinstall Python packages ──
        var venvDir = Path.Combine(repoDir, "venv");
        if (!Directory.Exists(venvDir))
        {
            progress?.Report($"venv not found at {venvDir} — skipping package reinstall.");
            Logger.Warning("AI-Toolkit venv not found at {Path}", venvDir);
            return new UpdateResult(true, hash, "Update pulled, but venv missing — packages not reinstalled");
        }

        var pythonExe = ResolveVenvPythonExecutable(venvDir);
        if (pythonExe is null)
        {
            progress?.Report("venv Python not found — skipping package reinstall.");
            return new UpdateResult(true, hash, "Update pulled, but venv Python missing");
        }

        // Prefer the venv-local uv (matches update.bat); fall back to pip when uv is absent.
        var uvExe = ResolveVenvUvExecutable(venvDir);

        // 2a — uninstall diffusers
        progress?.Report("Uninstalling diffusers...");
        var uninstallSuccess = false;
        if (uvExe is not null)
        {
            var uvUninstall = await RunIsolatedAsync(uvExe, "pip uninstall diffusers", repoDir, progress, "uv", ct, venvDir);
            uninstallSuccess = uvUninstall.Success;
            if (!uninstallSuccess)
                progress?.Report("uv pip uninstall failed, falling back to pip...");
        }
        if (!uninstallSuccess)
        {
            // -y suppresses the interactive confirmation prompt that pip uninstall asks for.
            await RunIsolatedAsync(pythonExe, "-u -m pip uninstall diffusers -y", repoDir, progress, "pip", ct, venvDir);
        }

        // 2b — pin huggingface-hub (transformers requires <1.0)
        progress?.Report("Fixing huggingface-hub version...");
        var hubResult = await InstallPackageAsync(
            uvExe, pythonExe, repoDir, venvDir, progress, ct,
            uvArgs: "pip install \"huggingface-hub>=0.34.0,<1.0\"",
            pipArgs: "-u -m pip install --progress-bar off \"huggingface-hub>=0.34.0,<1.0\"");
        if (!hubResult.Success)
            return new UpdateResult(false, hash, $"huggingface-hub install failed: {hubResult.Output}");

        // 2c — upgrade transformers
        progress?.Report("Upgrading transformers...");
        var trfResult = await InstallPackageAsync(
            uvExe, pythonExe, repoDir, venvDir, progress, ct,
            uvArgs: "pip install transformers -U",
            pipArgs: "-u -m pip install --progress-bar off transformers -U");
        if (!trfResult.Success)
            return new UpdateResult(false, hash, $"transformers install failed: {trfResult.Output}");

        // 2d — requirements.txt
        var requirementsPath = Path.Combine(repoDir, "requirements.txt");
        if (File.Exists(requirementsPath))
        {
            progress?.Report("Installing requirements...");
            var reqResult = await InstallPackageAsync(
                uvExe, pythonExe, repoDir, venvDir, progress, ct,
                uvArgs: $"pip install -r \"{requirementsPath}\"",
                pipArgs: $"-u -m pip install --progress-bar off -r \"{requirementsPath}\"");
            if (!reqResult.Success)
                return new UpdateResult(false, hash, $"requirements install failed: {reqResult.Output}");
        }
        else
        {
            progress?.Report("No requirements.txt found, skipping.");
        }

        progress?.Report($"Update complete. Version: {hash ?? "unknown"}");

        return new UpdateResult(true, hash, "Update completed successfully");
    }

    private static Task<ProcessResult> InstallPackageAsync(
        string? uvExe, string pythonExe, string workingDir, string venvDir,
        IProgress<string>? progress, CancellationToken ct,
        string uvArgs, string pipArgs)
        => uvExe is not null
            ? RunIsolatedAsync(uvExe, uvArgs, workingDir, progress, "uv", ct, venvDir)
            : RunIsolatedAsync(pythonExe, pipArgs, workingDir, progress, "pip", ct, venvDir);

    /// <summary>
    /// Resolves the AI-Toolkit git repo directory. The update.bat <c>cd</c>s into
    /// <c>./AI-Toolkit</c> first, so check that subfolder before the root.
    /// </summary>
    private static string? ResolveRepoDir(string installationPath)
    {
        foreach (var candidate in new[]
        {
            Path.Combine(installationPath, "AI-Toolkit"),
            Path.Combine(installationPath, "ai-toolkit"),
            installationPath
        })
        {
            if (Directory.Exists(Path.Combine(candidate, ".git")))
            {
                Logger.Debug("Found .git at {Path}", candidate);
                return candidate;
            }
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(installationPath))
            {
                if (Directory.Exists(Path.Combine(dir, ".git")))
                {
                    Logger.Debug("Found .git in subfolder: {Path}", dir);
                    return dir;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Logger.Debug("No .git found at {Path} or any immediate subfolder", installationPath);
        return null;
    }

    private static string? ResolveVenvPythonExecutable(string venvDir)
    {
        var win = Path.Combine(venvDir, "Scripts", "python.exe");
        if (File.Exists(win)) return win;

        // TODO: Linux Implementation - venv/bin/python on Linux
        var lin = Path.Combine(venvDir, "bin", "python");
        return File.Exists(lin) ? lin : null;
    }

    private static string? ResolveVenvUvExecutable(string venvDir)
    {
        var win = Path.Combine(venvDir, "Scripts", "uv.exe");
        if (File.Exists(win)) return win;

        // TODO: Linux Implementation - venv/bin/uv on Linux
        var lin = Path.Combine(venvDir, "bin", "uv");
        return File.Exists(lin) ? lin : null;
    }

    /// <summary>
    /// Detects the current branch for a git directory. If in detached HEAD state,
    /// resolves the default remote branch (origin/HEAD → origin/main → origin/master).
    /// </summary>
    private static async Task<(string Branch, bool IsDetached)> ResolveBranchAsync(
        string dir, CancellationToken ct)
    {
        var branchResult = await RunGitAsync(dir, "rev-parse --abbrev-ref HEAD", progress: null, ct);
        var branch = branchResult.Success ? branchResult.Output.Trim() : null;

        if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
            return (branch, IsDetached: false);

        var symbolicRef = await RunGitAsync(dir, "symbolic-ref refs/remotes/origin/HEAD --short", progress: null, ct);
        if (symbolicRef.Success)
        {
            branch = symbolicRef.Output.Trim().Replace("origin/", "");
        }
        else
        {
            branch = null;
            foreach (var candidate in new[] { "main", "master" })
            {
                var check = await RunGitAsync(dir, $"rev-parse --verify origin/{candidate}", progress: null, ct);
                if (check.Success) { branch = candidate; break; }
            }
        }

        branch ??= "main";
        return (branch, IsDetached: true);
    }

    /// <summary>
    /// Pulls updates for a git directory, handling detached HEAD by checking out
    /// the resolved branch first.
    /// </summary>
    private static async Task<ProcessResult> PullDirectoryAsync(
        string dir, IProgress<string>? progress, CancellationToken ct)
    {
        var fetchResult = await RunGitAsync(dir, "fetch --all", progress, ct);
        if (!fetchResult.Success)
        {
            Logger.Warning("git fetch failed in {Dir}: {Output}", dir, fetchResult.Output);
            return fetchResult;
        }

        var (branch, isDetached) = await ResolveBranchAsync(dir, ct);

        if (isDetached)
        {
            Logger.Information("AI-Toolkit is in detached HEAD state, checking out branch {Branch}", branch);
            progress?.Report($"Detached HEAD detected, checking out {branch}...");

            var checkoutResult = await RunGitAsync(dir, $"checkout {branch}", progress, ct);
            if (!checkoutResult.Success)
            {
                Logger.Error("Checkout of {Branch} failed: {Output}", branch, checkoutResult.Output);
                return checkoutResult;
            }
        }

        var pullResult = await RunGitAsync(dir, $"pull --ff-only origin {branch}", progress, ct);
        if (!pullResult.Success)
            Logger.Error("AI-Toolkit pull failed: {Output}", pullResult.Output);

        return pullResult;
    }

    private static Task<ProcessResult> RunGitAsync(
        string workingDir, string arguments, IProgress<string>? progress, CancellationToken ct)
        => RunIsolatedAsync("git", arguments, workingDir, progress, "git", ct, venvDir: null);

    /// <summary>
    /// Runs an external process with the AI-Toolkit isolation profile applied:
    /// clears ambient Python configuration (PYTHONPATH, CONDA_PREFIX, venv hints,
    /// etc.) so the venv-local interpreter / uv don't get redirected to a global
    /// install. Mirrors the SET-empty lines at the top of update.bat. When
    /// <paramref name="venvDir"/> is supplied, the venv is "activated" by setting
    /// VIRTUAL_ENV and prepending its Scripts/bin directory to PATH — required
    /// for <c>uv pip</c>, which otherwise refuses to install without a venv.
    /// Streams stdout/stderr live to <paramref name="progress"/> and kills the
    /// whole process tree on cancellation.
    /// </summary>
    private static async Task<ProcessResult> RunIsolatedAsync(
        string fileName,
        string arguments,
        string workingDir,
        IProgress<string>? progress,
        string? logPrefix,
        CancellationToken ct,
        string? venvDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Live streaming for pip / uv (otherwise output arrives in one blob at exit).
        psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
        psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
        psi.EnvironmentVariables["GIT_PROGRESS_DELAY"] = "0";
        psi.EnvironmentVariables["GIT_LFS_SKIP_SMUDGE"] = "1";

        // Remove ambient Python config so the venv-local interpreter wins.
        foreach (var key in IsolatedEnvironmentKeys)
            psi.EnvironmentVariables.Remove(key);

        if (!string.IsNullOrEmpty(venvDir))
        {
            // Equivalent to "CALL venv\Scripts\activate.bat": set VIRTUAL_ENV so
            // uv targets this venv, and prepend the binary dir to PATH so any
            // bare "pip"/"python" lookups resolve there too.
            var binDir = OperatingSystem.IsWindows()
                ? Path.Combine(venvDir, "Scripts")
                : Path.Combine(venvDir, "bin");

            psi.EnvironmentVariables["VIRTUAL_ENV"] = venvDir;
            var existingPath = psi.EnvironmentVariables["PATH"];
            psi.EnvironmentVariables["PATH"] = string.IsNullOrEmpty(existingPath)
                ? binDir
                : binDir + Path.PathSeparator + existingPath;
        }

        var prefix = string.IsNullOrEmpty(logPrefix) ? string.Empty : $"[{logPrefix}] ";

        Process? process = null;
        try
        {
            process = Process.Start(psi);
            if (process is null)
                return new ProcessResult(false, $"Failed to start {fileName}");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stdout.AppendLine(e.Data);
                progress?.Report(prefix + e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                stderr.AppendLine(e.Data);
                progress?.Report(prefix + e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process, fileName, arguments, workingDir);
                progress?.Report($"{prefix}Aborted by user.");
                return new ProcessResult(false, "Cancelled by user");
            }

            var exitCode = process.ExitCode;
            var output = stdout.ToString().TrimEnd();
            var error = stderr.ToString().TrimEnd();

            Logger.Debug("{Exe} {Args} in {Dir} → exit {Code}, stdout: {Out}, stderr: {Err}",
                fileName, arguments, workingDir, exitCode, output, error);

            return exitCode == 0
                ? new ProcessResult(true, output)
                : new ProcessResult(false, string.IsNullOrEmpty(error) ? output : error);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to run {Exe} {Args} in {Dir}", fileName, arguments, workingDir);
            return new ProcessResult(false, ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static readonly string[] IsolatedEnvironmentKeys =
    {
        "PYTHONPATH", "PYTHONHOME", "PYTHON", "PYTHONSTARTUP", "PYTHONUSERBASE",
        "PIP_CONFIG_FILE", "PIP_REQUIRE_VIRTUALENV", "VIRTUAL_ENV",
        "CONDA_PREFIX", "CONDA_DEFAULT_ENV", "PYENV_ROOT", "PYENV_VERSION"
    };

    private static void TryKillProcessTree(Process process, string fileName, string arguments, string workingDir)
    {
        try
        {
            if (!process.HasExited)
            {
                Logger.Warning("Killing process tree for {Exe} {Args} in {Dir} due to cancellation",
                    fileName, arguments, workingDir);
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception killEx)
        {
            Logger.Error(killEx, "Failed to kill {Exe} on cancellation", fileName);
        }
    }

    private sealed record ProcessResult(bool Success, string Output);
}
