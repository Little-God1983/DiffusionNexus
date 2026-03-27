using System.Diagnostics;
using System.Text;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Update service for ComfyUI installations.
/// ComfyUI has a backend (main repo) and may have a pip-based frontend package or
/// a separate frontend git repo (web/). Update order: backend git pull → pip requirements
/// (updates the frontend package) → git-based frontend pull (legacy fallback).
/// </summary>
public sealed class ComfyUIUpdateService : IInstallerUpdateService
{
    private static readonly ILogger Logger = Log.ForContext<ComfyUIUpdateService>();

    /// <inheritdoc />
    public IReadOnlySet<InstallerType> SupportedTypes { get; } =
        new HashSet<InstallerType> { InstallerType.ComfyUI };

    /// <inheritdoc />
    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        string installationPath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);

        var backendDir = ResolveBackendDir(installationPath);
        if (backendDir is null)
        {
            Logger.Warning("No git repository found at {Path} or its subfolders", installationPath);
            return new UpdateCheckResult(false, null, null, "No git repository found");
        }

        Logger.Information("Checking for updates in {Dir} (installation: {Path})", backendDir, installationPath);
        progress?.Report("Fetching latest changes...");

        // Fetch from remote without modifying the working tree
        var fetchResult = await RunGitAsync(backendDir, "fetch --all", ct);
        if (!fetchResult.Success)
        {
            Logger.Warning("git fetch failed in {Dir}: {Output}", backendDir, fetchResult.Output);
            return new UpdateCheckResult(false, null, null, $"Fetch failed: {fetchResult.Output}");
        }

        // Get current local HEAD
        var localHash = await RunGitAsync(backendDir, "rev-parse --short HEAD", ct);

        // Detect the tracked branch — handle detached HEAD and missing tracking
        var (branch, isDetached) = await ResolveBranchAsync(backendDir, ct);

        // Verify the remote branch exists
        var remoteHash = await RunGitAsync(backendDir, $"rev-parse --short origin/{branch}", ct);
        if (!remoteHash.Success)
        {
            Logger.Warning("Remote branch origin/{Branch} not found in {Dir}", branch, backendDir);
            return new UpdateCheckResult(false,
                localHash.Success ? localHash.Output.Trim() : null,
                null,
                $"Remote branch origin/{branch} not found");
        }

        // Count how many commits behind
        var behindResult = await RunGitAsync(backendDir, $"rev-list --count HEAD..origin/{branch}", ct);
        var behindCount = behindResult.Success && int.TryParse(behindResult.Output.Trim(), out var n) ? n : 0;

        var isUpdateAvailable = behindCount > 0;

        var summary = isUpdateAvailable
            ? $"{behindCount} commit{(behindCount == 1 ? "" : "s")} behind origin/{branch}"
            : "Up to date";

        Logger.Information("Update check for {Dir}: {Summary}", backendDir, summary);
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

        var backendDir = ResolveBackendDir(installationPath);
        if (backendDir is null)
            return new UpdateResult(false, null, "No git repository found");

        // ── Step 1: Update backend (main ComfyUI repo) ──
        progress?.Report("Updating ComfyUI backend...");
        Logger.Information("Updating ComfyUI backend at {Path}", backendDir);

        var backendPullResult = await PullDirectoryAsync(backendDir, progress, "Backend", ct);
        if (!backendPullResult.Success)
            return new UpdateResult(false, null, $"Backend update failed: {backendPullResult.Output}");

        progress?.Report("Backend updated successfully.");

        // ── Step 2: Update pip requirements (includes the frontend package) ──
        await UpdatePipRequirementsAsync(installationPath, backendDir, progress, ct);

        // ── Step 3: Update git-based frontend (legacy fallback for older setups) ──
        var frontendDir = ResolveFrontendDir(backendDir);
        if (frontendDir is not null)
        {
            progress?.Report("Updating ComfyUI frontend (git)...");
            Logger.Information("Updating ComfyUI frontend at {Path}", frontendDir);

            var frontendPullResult = await PullDirectoryAsync(frontendDir, progress, "Frontend", ct);
            if (!frontendPullResult.Success)
            {
                Logger.Warning("Frontend git update failed (non-fatal): {Output}", frontendPullResult.Output);
                progress?.Report($"Frontend git update failed: {frontendPullResult.Output}");
                // Non-fatal — pip may have already updated the frontend package
            }
            else
            {
                progress?.Report("Frontend (git) updated successfully.");
            }
        }

        // ── Get new version hash ──
        var newHash = await RunGitAsync(backendDir, "rev-parse --short HEAD", ct);
        var hash = newHash.Success ? newHash.Output.Trim() : null;

        progress?.Report($"Update complete. Version: {hash ?? "unknown"}");

        return new UpdateResult(
            Success: true,
            NewHash: hash,
            Message: "Update completed successfully");
    }

    /// <summary>
    /// Detects the current branch for a git directory. If in detached HEAD state,
    /// resolves the default remote branch (origin/HEAD → origin/main → origin/master).
    /// </summary>
    /// <returns>The resolved branch name and whether the repo was in detached HEAD state.</returns>
    private static async Task<(string Branch, bool IsDetached)> ResolveBranchAsync(
        string dir, CancellationToken ct)
    {
        var branchResult = await RunGitAsync(dir, "rev-parse --abbrev-ref HEAD", ct);
        var branch = branchResult.Success ? branchResult.Output.Trim() : null;

        if (!string.IsNullOrEmpty(branch) && branch != "HEAD")
            return (branch, IsDetached: false);

        Logger.Debug("Detached HEAD in {Dir}, trying to find default remote branch", dir);

        // Try origin/HEAD symbolic ref first
        var symbolicRef = await RunGitAsync(dir, "symbolic-ref refs/remotes/origin/HEAD --short", ct);
        if (symbolicRef.Success)
        {
            // Returns "origin/main" or "origin/master" — strip "origin/"
            branch = symbolicRef.Output.Trim().Replace("origin/", "");
        }
        else
        {
            // Fallback: try common branch names
            branch = null;
            foreach (var candidate in new[] { "main", "master" })
            {
                var check = await RunGitAsync(dir, $"rev-parse --verify origin/{candidate}", ct);
                if (check.Success) { branch = candidate; break; }
            }
        }

        branch ??= "main";
        Logger.Debug("Resolved remote branch to {Branch} for {Dir}", branch, dir);
        return (branch, IsDetached: true);
    }

    /// <summary>
    /// Pulls updates for a git directory, handling detached HEAD by checking out the resolved branch first.
    /// </summary>
    private static async Task<ProcessResult> PullDirectoryAsync(
        string dir, IProgress<string>? progress, string label, CancellationToken ct)
    {
        // Fetch latest refs first so we have up-to-date remote tracking
        var fetchResult = await RunGitAsync(dir, "fetch --all", ct);
        if (!fetchResult.Success)
        {
            Logger.Warning("{Label} git fetch failed in {Dir}: {Output}", label, dir, fetchResult.Output);
            return fetchResult;
        }

        var (branch, isDetached) = await ResolveBranchAsync(dir, ct);

        if (isDetached)
        {
            Logger.Information("{Label} is in detached HEAD state, checking out branch {Branch}", label, branch);
            progress?.Report($"{label}: detached HEAD detected, checking out {branch}...");

            var checkoutResult = await RunGitAsync(dir, $"checkout {branch}", ct);
            if (!checkoutResult.Success)
            {
                Logger.Error("{Label} checkout of {Branch} failed: {Output}", label, branch, checkoutResult.Output);
                return checkoutResult;
            }
        }

        var pullResult = await RunGitAsync(dir, $"pull --ff-only origin {branch}", ct);
        if (!pullResult.Success)
            Logger.Error("{Label} update failed: {Output}", label, pullResult.Output);

        return pullResult;
    }

    /// <summary>
    /// Resolves the backend git directory. ComfyUI may be at the root or in a subfolder.
    /// </summary>
    private static string? ResolveBackendDir(string installationPath)
    {
        // Check root first
        if (Directory.Exists(Path.Combine(installationPath, ".git")))
        {
            Logger.Debug("Found .git at root: {Path}", installationPath);
            return installationPath;
        }

        // Check ComfyUI subfolder (standalone distributions)
        var comfySubDir = Path.Combine(installationPath, "ComfyUI");
        if (Directory.Exists(Path.Combine(comfySubDir, ".git")))
        {
            Logger.Debug("Found .git in ComfyUI subfolder: {Path}", comfySubDir);
            return comfySubDir;
        }

        // Check immediate subfolders
        try
        {
            foreach (var subDir in Directory.EnumerateDirectories(installationPath))
            {
                if (Directory.Exists(Path.Combine(subDir, ".git")))
                {
                    Logger.Debug("Found .git in subfolder: {Path}", subDir);
                    return subDir;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        Logger.Debug("No .git found at {Path} or any immediate subfolder", installationPath);
        return null;
    }

    /// <summary>
    /// Resolves the frontend directory if it has its own git repo.
    /// In newer ComfyUI versions, the frontend lives in web/ with a separate .git.
    /// </summary>
    private static string? ResolveFrontendDir(string backendDir)
    {
        var webDir = Path.Combine(backendDir, "web");
        if (Directory.Exists(Path.Combine(webDir, ".git")))
            return webDir;

        // Some distributions use "ComfyUI-Frontend" or similar
        // TODO: Linux Implementation - path casing may differ
        var frontendDir = Path.Combine(backendDir, "ComfyUI-Frontend");
        if (Directory.Exists(Path.Combine(frontendDir, ".git")))
            return frontendDir;

        return null;
    }

    /// <summary>
    /// Known folder names for the embedded Python in ComfyUI portable installs.
    /// The portable package historically ships <c>python_embeded</c> (typo); newer
    /// builds may use <c>python_embedded</c>. We check both.
    /// </summary>
    private static readonly string[] PortablePythonFolders =
        ["python_embeded", "python_embedded", "python"];

    /// <summary>
    /// Finds the Python executable for the ComfyUI installation.
    /// <list type="bullet">
    ///   <item><b>Portable</b>: <c>{installationPath}/python_embeded/python.exe</c></item>
    ///   <item><b>Manual / venv</b>: <c>{backendDir}/venv/Scripts/python.exe</c> (Windows)
    ///     or <c>{backendDir}/venv/bin/python</c> (Linux).</item>
    /// </list>
    /// </summary>
    private static string? ResolvePythonExecutable(string installationPath, string backendDir)
    {
        // Portable: embedded Python lives next to the ComfyUI/ subfolder
        foreach (var folder in PortablePythonFolders)
        {
            var candidate = Path.Combine(installationPath, folder, "python.exe");
            if (File.Exists(candidate))
            {
                Logger.Debug("Found portable Python at {Path}", candidate);
                return candidate;
            }
        }

        // Manual / venv on Windows
        var venvScripts = Path.Combine(backendDir, "venv", "Scripts", "python.exe");
        if (File.Exists(venvScripts))
        {
            Logger.Debug("Found venv Python at {Path}", venvScripts);
            return venvScripts;
        }

        // TODO: Linux Implementation - use venv/bin/python on Linux
        var venvBin = Path.Combine(backendDir, "venv", "bin", "python");
        if (File.Exists(venvBin))
        {
            Logger.Debug("Found venv Python at {Path}", venvBin);
            return venvBin;
        }

        return null;
    }

    /// <summary>
    /// Runs <c>pip install -r requirements.txt</c> in the backend directory to update
    /// pip-managed dependencies including the frontend package (<c>comfyui-frontend-package</c>).
    /// </summary>
    private static async Task UpdatePipRequirementsAsync(
        string installationPath, string backendDir,
        IProgress<string>? progress, CancellationToken ct)
    {
        var requirementsPath = Path.Combine(backendDir, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            Logger.Debug("No requirements.txt found at {Path}, skipping pip update", requirementsPath);
            return;
        }

        var pythonExe = ResolvePythonExecutable(installationPath, backendDir);
        if (pythonExe is null)
        {
            Logger.Warning("Could not find Python executable for {Path} — pip requirements will not be updated", installationPath);
            progress?.Report("Python not found — skipping pip requirements update");
            return;
        }

        progress?.Report("Updating pip requirements (includes frontend package)...");
        Logger.Information("Running pip install -r requirements.txt using {Python}", pythonExe);

        var pipResult = await RunProcessAsync(
            pythonExe,
            $"-m pip install -r \"{requirementsPath}\"",
            backendDir,
            ct);

        if (pipResult.Success)
        {
            Logger.Information("pip requirements updated successfully");
            progress?.Report("Pip requirements updated successfully.");
        }
        else
        {
            Logger.Warning("pip install failed (non-fatal): {Output}", pipResult.Output);
            progress?.Report($"pip install failed (non-fatal): {pipResult.Output}");
        }
    }

    /// <summary>
    /// Runs a git command in the specified directory and returns the output.
    /// </summary>
    private static Task<ProcessResult> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
        => RunProcessAsync("git", arguments, workingDir, ct);

    /// <summary>
    /// Runs an external process and captures stdout/stderr.
    /// </summary>
    private static async Task<ProcessResult> RunProcessAsync(
        string fileName, string arguments, string workingDir, CancellationToken ct)
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

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return new ProcessResult(false, $"Failed to start {fileName}");

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync(ct);

            var exitCode = process.ExitCode;
            var output = stdout.ToString().TrimEnd();
            var error = stderr.ToString().TrimEnd();

            Logger.Debug("{Exe} {Args} in {Dir} → exit {Code}, stdout: {Out}, stderr: {Err}",
                fileName, arguments, workingDir, exitCode, output, error);

            return exitCode == 0
                ? new ProcessResult(true, output)
                : new ProcessResult(false, string.IsNullOrEmpty(error) ? output : error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to run {Exe} {Args} in {Dir}", fileName, arguments, workingDir);
            return new ProcessResult(false, ex.Message);
        }
    }

    private sealed record ProcessResult(bool Success, string Output);
}
