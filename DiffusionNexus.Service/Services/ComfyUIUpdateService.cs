using System.Diagnostics;
using System.Text;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.Service.Services;

/// <summary>
/// Update service for ComfyUI installations.
/// ComfyUI has a backend (main repo) and may have a separate frontend repo (web/).
/// The backend MUST be updated before the frontend.
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
        var branchResult = await RunGitAsync(backendDir, "rev-parse --abbrev-ref HEAD", ct);
        var branch = branchResult.Success ? branchResult.Output.Trim() : null;

        // "HEAD" means detached HEAD — try to find the default remote branch
        if (string.IsNullOrEmpty(branch) || branch == "HEAD")
        {
            Logger.Debug("Detached HEAD in {Dir}, trying to find default remote branch", backendDir);

            // Try origin/HEAD symbolic ref first
            var symbolicRef = await RunGitAsync(backendDir, "symbolic-ref refs/remotes/origin/HEAD --short", ct);
            if (symbolicRef.Success)
            {
                // Returns "origin/main" or "origin/master" — strip "origin/"
                branch = symbolicRef.Output.Trim().Replace("origin/", "");
            }
            else
            {
                // Fallback: try common branch names
                foreach (var candidate in new[] { "main", "master" })
                {
                    var check = await RunGitAsync(backendDir, $"rev-parse --verify origin/{candidate}", ct);
                    if (check.Success) { branch = candidate; break; }
                }
            }

            branch ??= "main";
            Logger.Debug("Resolved remote branch to {Branch} for {Dir}", branch, backendDir);
        }

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

        var backendResult = await RunGitAsync(backendDir, "pull --ff-only", ct);
        if (!backendResult.Success)
        {
            Logger.Error("Backend update failed: {Output}", backendResult.Output);
            return new UpdateResult(false, null, $"Backend update failed: {backendResult.Output}");
        }

        progress?.Report("Backend updated successfully.");

        // ── Step 2: Update frontend (web/ subfolder, if it has its own repo) ──
        var frontendDir = ResolveFrontendDir(backendDir);
        if (frontendDir is not null)
        {
            progress?.Report("Updating ComfyUI frontend...");
            Logger.Information("Updating ComfyUI frontend at {Path}", frontendDir);

            var frontendResult = await RunGitAsync(frontendDir, "pull --ff-only", ct);
            if (!frontendResult.Success)
            {
                Logger.Warning("Frontend update failed (non-fatal): {Output}", frontendResult.Output);
                progress?.Report($"Frontend update failed: {frontendResult.Output}");
                // Non-fatal — the backend was already updated
            }
            else
            {
                progress?.Report("Frontend updated successfully.");
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
    /// Runs a git command in the specified directory and returns the output.
    /// </summary>
    private static async Task<GitResult> RunGitAsync(string workingDir, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
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
                return new GitResult(false, "Failed to start git process");

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

            Logger.Debug("git {Args} in {Dir} → exit {Code}, stdout: {Out}, stderr: {Err}",
                arguments, workingDir, exitCode, output, error);

            return exitCode == 0
                ? new GitResult(true, output)
                : new GitResult(false, string.IsNullOrEmpty(error) ? output : error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to run git {Args} in {Dir}", arguments, workingDir);
            return new GitResult(false, ex.Message);
        }
    }

    private sealed record GitResult(bool Success, string Output);
}
