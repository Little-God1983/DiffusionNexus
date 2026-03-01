using System.Diagnostics;
using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using Serilog;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Installs missing custom nodes (shallow git clone + venv pip requirements) and
/// downloads missing models for a workload configuration, reusing SDK services
/// (<see cref="IGitService"/>, <see cref="FileDownloader"/>) to match the standalone
/// installer behaviour.
/// </summary>
public sealed class WorkloadInstallService : IWorkloadInstallService
{
    private static readonly ILogger Logger = Log.ForContext<WorkloadInstallService>();

    private readonly IGitService _gitService;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Known folder names for the embedded Python in ComfyUI portable installs.
    /// The portable package historically ships <c>python_embeded</c> (typo); newer
    /// builds may use <c>python_embedded</c>. We check both.
    /// </summary>
    private static readonly string[] PortablePythonFolders =
        ["python_embeded", "python_embedded", "python"];

    public WorkloadInstallService(IGitService gitService, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(gitService);
        ArgumentNullException.ThrowIfNull(httpClient);

        _gitService = gitService;
        _httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task<string> InstallSelectedAsync(
        InstallationConfiguration configuration,
        string comfyUIRootPath,
        IReadOnlyList<CustomNodeCheckResult> selectedNodes,
        IReadOnlyList<ModelCheckResult> selectedModels,
        int selectedVramGb,
        IProgress<WorkloadInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(comfyUIRootPath);

        var installationType = ConfigurationCheckerService.DetectInstallationType(comfyUIRootPath);
        var repositoryPath = ConfigurationCheckerService.GetRepositoryPath(comfyUIRootPath, installationType);

        var nodesCloned = 0;
        var nodesFailed = 0;
        var modelsDownloaded = 0;
        var modelsFailed = 0;

        // ── Clone custom nodes + install pip requirements ──
        if (selectedNodes.Count > 0)
        {
            var customNodesPath = Path.Combine(repositoryPath, "custom_nodes");
            Directory.CreateDirectory(customNodesPath);

            // Build a lookup so we can check InstallRequirements per repo
            var repoLookup = (configuration.GitRepositories ?? [])
                .ToDictionary(r => r.Id);

            // Resolve the Python executable once for all nodes
            var pythonExe = ResolvePythonExecutable(comfyUIRootPath, repositoryPath, installationType);
            if (pythonExe is null)
            {
                Logger.Warning("Could not find Python executable for {Root} - pip requirements will not be installed", comfyUIRootPath);
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = "Python",
                    Message = "Python not found - requirements will not be installed"
                });
            }
            else
            {
                Logger.Information("Using Python executable: {Python}", pythonExe);
            }

            foreach (var node in selectedNodes)
            {
                cancellationToken.ThrowIfCancellationRequested();

                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = node.Name,
                    Message = $"Cloning {node.Name}..."
                });

                try
                {
                    var cloneOptions = new GitCloneOptions
                    {
                        RepositoryUrl = node.Url,
                        TargetDirectory = customNodesPath,
                        ShallowClone = true
                    };

                    var result = await _gitService.CloneRepositoryAsync(
                        cloneOptions, progress: null, cancellationToken);

                    if (!result.IsSuccess)
                    {
                        nodesFailed++;
                        Logger.Warning("Failed to clone {Name}: {Message}", node.Name, result.Message);
                        progress?.Report(new WorkloadInstallProgress
                        {
                            ItemName = node.Name,
                            Message = $"Failed to clone {node.Name}: {result.Message}",
                            IsFailed = true
                        });
                        continue;
                    }

                    nodesCloned++;
                    Logger.Information("Cloned {Name} to {Path}", node.Name, result.RepositoryPath);

                    // Determine the cloned folder path
                    var clonedPath = result.RepositoryPath
                        ?? Path.Combine(customNodesPath, PathNormalizer.GetRepositoryName(node.Url));

                    // Install requirements.txt via the venv's pip if the configuration says so
                    var shouldInstallReqs = repoLookup.TryGetValue(node.Id, out var gitRepo)
                        && gitRepo.InstallRequirements;

                    if (shouldInstallReqs && pythonExe is not null)
                    {
                        await InstallRequirementsAsync(
                            pythonExe, clonedPath, node.Name, progress, cancellationToken);
                    }

                    progress?.Report(new WorkloadInstallProgress
                    {
                        ItemName = node.Name,
                        Message = $"Installed {node.Name}",
                        IsSuccess = true
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    nodesFailed++;
                    Logger.Error(ex, "Error installing {Name}", node.Name);
                    progress?.Report(new WorkloadInstallProgress
                    {
                        ItemName = node.Name,
                        Message = $"Error installing {node.Name}: {ex.Message}",
                        IsFailed = true
                    });
                }
            }
        }

        // ── Download models ──
        if (selectedModels.Count > 0)
        {
            var downloader = new FileDownloader(_httpClient);

            // Build a lookup for the model entities from the configuration
            var modelLookup = (configuration.ModelDownloads ?? [])
                .ToDictionary(m => m.Id);

            // Resolve model base folder from extra_model_paths.yaml if available
            var extraBasePath = ResolveExtraModelBasePath(repositoryPath);

            foreach (var modelResult in selectedModels)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!modelLookup.TryGetValue(modelResult.Id, out var model))
                {
                    Logger.Warning("Model {Name} (ID {Id}) not found in configuration", modelResult.Name, modelResult.Id);
                    modelsFailed++;
                    continue;
                }

                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = model.Name,
                    Message = $"Downloading {model.Name}..."
                });

                try
                {
                    var (ok, fail) = await DownloadModelAsync(
                        downloader, configuration, model, repositoryPath,
                        extraBasePath, selectedVramGb, progress, cancellationToken);

                    modelsDownloaded += ok;
                    modelsFailed += fail;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    modelsFailed++;
                    Logger.Error(ex, "Error downloading {Name}", model.Name);
                    progress?.Report(new WorkloadInstallProgress
                    {
                        ItemName = model.Name,
                        Message = $"Error downloading {model.Name}: {ex.Message}",
                        IsFailed = true
                    });
                }
            }
        }

        var summary = BuildSummary(nodesCloned, nodesFailed, modelsDownloaded, modelsFailed);
        Logger.Information("Workload install complete: {Summary}", summary);
        return summary;
    }

    #region Python / Requirements

    /// <summary>
    /// Finds the Python executable for the ComfyUI installation.
    /// <list type="bullet">
    ///   <item><b>Portable</b>: <c>{rootPath}/python_embeded/python.exe</c>
    ///     (or the corrected <c>python_embedded</c> folder).</item>
    ///   <item><b>Manual / venv</b>: <c>{repositoryPath}/venv/Scripts/python.exe</c>
    ///     (Windows) or <c>{repositoryPath}/venv/bin/python</c> (Linux).</item>
    /// </list>
    /// </summary>
    internal static string? ResolvePythonExecutable(
        string comfyUIRootPath,
        string repositoryPath,
        ComfyUIInstallationType installationType)
    {
        // Portable: embedded Python lives next to the ComfyUI/ subfolder
        if (installationType == ComfyUIInstallationType.Portable)
        {
            foreach (var folder in PortablePythonFolders)
            {
                var candidate = Path.Combine(comfyUIRootPath, folder, "python.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        // Manual / venv on Windows
        var venvScripts = Path.Combine(repositoryPath, "venv", "Scripts", "python.exe");
        if (File.Exists(venvScripts))
        {
            return venvScripts;
        }

        // TODO: Linux Implementation - use venv/bin/python on Linux
        var venvBin = Path.Combine(repositoryPath, "venv", "bin", "python");
        if (File.Exists(venvBin))
        {
            return venvBin;
        }

        return null;
    }

    /// <summary>
    /// Installs <c>requirements.txt</c> for a cloned custom node using the venv's Python,
    /// matching the SDK's <c>CloneAdditionalReposStepHandler.InstallRepositoryRequirementsAsync</c>.
    /// Runs: <c>{pythonExe} -m pip install -r requirements.txt</c>.
    /// </summary>
    private static async Task InstallRequirementsAsync(
        string pythonExe,
        string repoPath,
        string nodeName,
        IProgress<WorkloadInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var requirementsPath = Path.Combine(repoPath, "requirements.txt");
        if (!File.Exists(requirementsPath))
        {
            Logger.Debug("No requirements.txt found for {Name}, skipping pip install", nodeName);
            return;
        }

        progress?.Report(new WorkloadInstallProgress
        {
            ItemName = nodeName,
            Message = $"Installing pip requirements for {nodeName}..."
        });

        Logger.Information("pip install -r requirements.txt for {Name} using {Python}", nodeName, pythonExe);

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"-m pip install -r \"{requirementsPath}\"",
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            process.Start();

            // Read output asynchronously to avoid deadlock on large output
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdoutText = await stdoutTask;
            var stderrText = await stderrTask;

            if (process.ExitCode == 0)
            {
                Logger.Information("Requirements installed for {Name}", nodeName);
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = nodeName,
                    Message = $"Requirements installed for {nodeName}"
                });
            }
            else
            {
                Logger.Warning(
                    "pip install failed for {Name} (exit code {Code}):\n{StdErr}",
                    nodeName, process.ExitCode, stderrText);

                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = nodeName,
                    Message = $"pip install failed for {nodeName} (exit code {process.ExitCode})"
                });
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to run pip install for {Name}", nodeName);
            progress?.Report(new WorkloadInstallProgress
            {
                ItemName = nodeName,
                Message = $"Failed to install requirements for {nodeName}: {ex.Message}"
            });
        }
    }

    #endregion

    #region Model Download

    /// <summary>
    /// Downloads a single model using the same logic as the SDK's ModelDownloadStepHandler:
    /// resolves destination via <see cref="ModelDestinationResolver"/>, selects best VRAM
    /// links via <see cref="VramProfileHelper"/>.
    /// </summary>
    private static async Task<(int Success, int Fail)> DownloadModelAsync(
        FileDownloader downloader,
        InstallationConfiguration configuration,
        ModelDownload model,
        string repositoryPath,
        string? modelBaseFolder,
        int selectedVramGb,
        IProgress<WorkloadInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var enabledLinks = model.DownloadLinks?.Where(l => l.Enabled).ToList() ?? [];

        if (enabledLinks.Count == 0)
        {
            // Fall back to the model's direct URL
            if (string.IsNullOrWhiteSpace(model.Url))
            {
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = model.Name,
                    Message = $"No download URL for {model.Name}",
                    IsFailed = true
                });
                return (0, 1);
            }

            // Check VRAM profile fit
            if (selectedVramGb > 0 && !VramProfileHelper.VramProfileFitsSelection(model.VramProfile, selectedVramGb))
            {
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = model.Name,
                    Message = $"Skipped {model.Name} (VRAM profile mismatch)"
                });
                return (0, 0);
            }

            var destination = ModelDestinationResolver.Resolve(
                configuration, model, repositoryPath, modelBaseFolder);

            var ok = await downloader.DownloadSingleFileAsync(
                model.Url, destination, model.Name,
                verboseLogging: false, logProgress: null, downloadProgress: null,
                cancellationToken);

            ReportDownloadResult(progress, model.Name, ok);
            return ok ? (1, 0) : (0, 1);
        }

        // Select best links via VRAM profile helper
        var linksToDownload = VramProfileHelper.SelectBestMatchingLinks(
            enabledLinks, selectedVramGb, logProgress: null, model.Name);

        if (linksToDownload.Count == 0)
        {
            progress?.Report(new WorkloadInstallProgress
            {
                ItemName = model.Name,
                Message = $"No suitable download links for {model.Name} with {selectedVramGb} GB VRAM"
            });
            return (0, 0);
        }

        int success = 0, fail = 0;

        foreach (var link in linksToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = !string.IsNullOrWhiteSpace(link.Destination)
                ? ResolvePathFromLink(link.Destination, repositoryPath, configuration)
                : ModelDestinationResolver.Resolve(
                    configuration, model, repositoryPath, modelBaseFolder);

            var ok = await downloader.DownloadSingleFileAsync(
                link.Url, destination, model.Name,
                verboseLogging: false, logProgress: null, downloadProgress: null,
                cancellationToken);

            if (ok) success++;
            else fail++;
        }

        ReportDownloadResult(progress, model.Name, fail == 0);
        return (success, fail);
    }

    #endregion

    /// <summary>
    /// Reads the <c>extra_model_paths.yaml</c> from the repository root and returns
    /// the first <c>base_path</c> value as the model base folder, if present.
    /// </summary>
    private static string? ResolveExtraModelBasePath(string repositoryPath)
    {
        var extraPaths = ConfigurationCheckerService.ParseExtraModelPathsYaml(repositoryPath);
        return extraPaths.Count > 0 ? extraPaths[0] : null;
    }

    /// <summary>
    /// Resolves a download link destination path (mirrors SDK's ModelDownloadStepHandler.ResolvePath).
    /// </summary>
    private static string ResolvePathFromLink(
        string path, string repositoryPath, InstallationConfiguration config)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        var resolved = path
            .Replace("{RepositoryPath}", repositoryPath)
            .Replace("{Repository}", repositoryPath);

        return Path.IsPathRooted(resolved)
            ? resolved
            : Path.Combine(repositoryPath, resolved);
    }

    private static void ReportDownloadResult(
        IProgress<WorkloadInstallProgress>? progress, string name, bool success)
    {
        progress?.Report(new WorkloadInstallProgress
        {
            ItemName = name,
            Message = success ? $"Downloaded {name}" : $"Failed to download {name}",
            IsSuccess = success,
            IsFailed = !success
        });
    }

    private static string BuildSummary(int nodesCloned, int nodesFailed, int modelsDownloaded, int modelsFailed)
    {
        var parts = new List<string>();

        if (nodesCloned > 0) parts.Add($"{nodesCloned} node(s) installed");
        if (nodesFailed > 0) parts.Add($"{nodesFailed} node(s) failed");
        if (modelsDownloaded > 0) parts.Add($"{modelsDownloaded} model(s) downloaded");
        if (modelsFailed > 0) parts.Add($"{modelsFailed} model(s) failed");

        return parts.Count > 0
            ? string.Join(", ", parts)
            : "Nothing to install.";
    }
}
