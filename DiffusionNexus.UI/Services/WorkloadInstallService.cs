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
        IProgress<DownloadProgress>? downloadProgress = null,
        Func<CancellationToken>? skipDownloadTokenProvider = null,
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
                    ItemId = node.Id,
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
                            ItemId = node.Id,
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
                        // TODO: Pass gitRepo?.AdditionalPipPackages once SDK NuGet includes the property
                        await InstallRequirementsAsync(
                            pythonExe, clonedPath, node.Name,
                            additionalPipPackages: null,
                            progress, cancellationToken);
                    }

                    progress?.Report(new WorkloadInstallProgress
                    {
                        ItemId = node.Id,
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
                        ItemId = node.Id,
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
                    ItemId = model.Id,
                    ItemName = model.Name,
                    Message = $"Downloading {model.Name}..."
                });

                try
                {
                    var (ok, fail) = await DownloadModelAsync(
                        downloader, configuration, model, repositoryPath,
                        extraBasePath, selectedVramGb, progress,
                        downloadProgress, skipDownloadTokenProvider, cancellationToken);

                    modelsDownloaded += ok;
                    modelsFailed += fail;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    modelsFailed++;
                    Logger.Error(ex, "Error downloading {Name}", model.Name);
                    progress?.Report(new WorkloadInstallProgress
                    {
                        ItemId = model.Id,
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

    /// <inheritdoc />
    public async Task<string> RepairPipDependenciesAsync(
        InstallationConfiguration configuration,
        string comfyUIRootPath,
        IProgress<WorkloadInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(comfyUIRootPath);

        var installationType = ConfigurationCheckerService.DetectInstallationType(comfyUIRootPath);
        var repositoryPath = ConfigurationCheckerService.GetRepositoryPath(comfyUIRootPath, installationType);
        var customNodesPath = Path.Combine(repositoryPath, "custom_nodes");

        var pythonExe = ResolvePythonExecutable(comfyUIRootPath, repositoryPath, installationType);
        if (pythonExe is null)
        {
            const string msg = "Python executable not found — cannot repair pip dependencies.";
            Logger.Warning(msg);
            progress?.Report(new WorkloadInstallProgress { ItemName = "Python", Message = msg });
            return msg;
        }

        var gitRepositories = configuration.GitRepositories ?? [];
        if (gitRepositories.Count == 0)
        {
            return "No custom nodes configured for this workload.";
        }

        var repairedCount = 0;
        var skippedCount = 0;
        var failedCount = 0;

        foreach (var repo in gitRepositories)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var repoFolderName = PathNormalizer.GetRepositoryName(repo.Url);
            var repoPath = Path.Combine(customNodesPath, repoFolderName);
            var requirementsPath = Path.Combine(repoPath, "requirements.txt");
            var nodeName = string.IsNullOrWhiteSpace(repo.Name) ? repoFolderName : repo.Name;

            if (!Directory.Exists(repoPath) || !File.Exists(requirementsPath))
            {
                skippedCount++;
                continue;
            }

            // TODO: Pass repo.AdditionalPipPackages once SDK NuGet includes the property
            var extras = ResolveSupplementaryPackages(
                requirementsPath, additionalPipPackages: null);

            if (extras.Count == 0)
            {
                skippedCount++;
                continue;
            }

            var packageList = string.Join(" ", extras);
            Logger.Information(
                "Repairing {Count} supplementary package(s) for {Name}: {Packages}",
                extras.Count, nodeName, packageList);

            progress?.Report(new WorkloadInstallProgress
            {
                ItemId = repo.Id,
                ItemName = nodeName,
                Message = $"Installing supplementary packages: {packageList}"
            });

            var (success, _) = await RunPipInstallAsync(
                pythonExe, repoPath,
                $"-m pip install {packageList}",
                nodeName, "supplementary packages",
                progress, cancellationToken);

            if (success)
            {
                repairedCount++;
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemId = repo.Id,
                    ItemName = nodeName,
                    Message = $"Repaired {nodeName}",
                    IsSuccess = true
                });
            }
            else
            {
                failedCount++;
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemId = repo.Id,
                    ItemName = nodeName,
                    Message = $"Failed to repair {nodeName}",
                    IsFailed = true
                });
            }
        }

        var parts = new List<string>();
        if (repairedCount > 0) parts.Add($"{repairedCount} node(s) repaired");
        if (skippedCount > 0) parts.Add($"{skippedCount} node(s) up-to-date");
        if (failedCount > 0) parts.Add($"{failedCount} node(s) failed");

        var result = parts.Count > 0 ? string.Join(", ", parts) : "Nothing to repair.";
        Logger.Information("Pip dependency repair complete: {Summary}", result);
        return result;
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
    /// After the main install, resolves supplementary packages via
    /// <see cref="ResolveSupplementaryPackages"/> and installs those too.
    /// </summary>
    private static async Task InstallRequirementsAsync(
        string pythonExe,
        string repoPath,
        string nodeName,
        string? additionalPipPackages,
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

        // ── Step 1: Install the node's own requirements.txt ──
        var (mainSuccess, _) = await RunPipInstallAsync(
            pythonExe, repoPath,
            $"-m pip install -r \"{requirementsPath}\"",
            nodeName, "requirements",
            progress, cancellationToken);

        if (!mainSuccess)
            return;

        // ── Step 2: Install supplementary packages (known upstream gaps + per-repo overrides) ──
        var extras = ResolveSupplementaryPackages(requirementsPath, additionalPipPackages);
        if (extras.Count > 0)
        {
            var packageList = string.Join(" ", extras);
            Logger.Information(
                "Installing {Count} supplementary pip package(s) for {Name}: {Packages}",
                extras.Count, nodeName, packageList);

            progress?.Report(new WorkloadInstallProgress
            {
                ItemName = nodeName,
                Message = $"Installing supplementary packages for {nodeName}: {packageList}"
            });

            await RunPipInstallAsync(
                pythonExe, repoPath,
                $"-m pip install {packageList}",
                nodeName, "supplementary packages",
                progress, cancellationToken);
        }
    }

    /// <summary>
    /// Runs a single pip install command and reports progress.
    /// </summary>
    /// <returns>A tuple of (success, stderr output).</returns>
    private static async Task<(bool Success, string StdErr)> RunPipInstallAsync(
        string pythonExe,
        string workingDirectory,
        string arguments,
        string nodeName,
        string description,
        IProgress<WorkloadInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
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
                Logger.Information("{Description} installed for {Name}", description, nodeName);
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemName = nodeName,
                    Message = $"{description} installed for {nodeName}"
                });
                return (true, stderrText);
            }

            Logger.Warning(
                "pip install {Description} failed for {Name} (exit code {Code}):\n{StdErr}",
                description, nodeName, process.ExitCode, stderrText);

            progress?.Report(new WorkloadInstallProgress
            {
                ItemName = nodeName,
                Message = $"pip install {description} failed for {nodeName} (exit code {process.ExitCode})"
            });

            return (false, stderrText);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Error(ex, "Failed to run pip install {Description} for {Name}", description, nodeName);
            progress?.Report(new WorkloadInstallProgress
            {
                ItemName = nodeName,
                Message = $"Failed to install {description} for {nodeName}: {ex.Message}"
            });
            return (false, ex.Message);
        }
    }

    // TODO: Remove once SDK NuGet is updated to include SupplementaryPipPackageResolver.
    // These methods mirror SDK's SupplementaryPipPackageResolver and should be replaced
    // with direct calls to the SDK utility when the NuGet package is published.

    /// <summary>
    /// Known packages whose upstream <c>requirements.txt</c> omits required runtime deps.
    /// Mirrors the SDK's <c>SupplementaryPipPackageResolver.KnownSupplementaryPackages</c>.
    /// </summary>
    internal static readonly Dictionary<string, string[]> SupplementaryPipPackages =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["transformers"] = ["kernels"],
        };

    /// <summary>
    /// Scans a <c>requirements.txt</c> file and returns any supplementary packages that
    /// should be installed alongside the declared dependencies, merged with any
    /// per-repository additional packages from the configuration.
    /// </summary>
    internal static List<string> ResolveSupplementaryPackages(
        string requirementsPath, string? additionalPipPackages = null)
    {
        var extras = new List<string>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(requirementsPath);
        }
        catch (IOException)
        {
            return extras;
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            var packageName = ExtractPackageName(line);
            if (packageName.Length == 0)
                continue;

            if (SupplementaryPipPackages.TryGetValue(packageName, out var supplementary))
            {
                foreach (var pkg in supplementary)
                {
                    if (!extras.Contains(pkg, StringComparer.OrdinalIgnoreCase))
                        extras.Add(pkg);
                }
            }
        }

        // Merge per-repository overrides
        if (!string.IsNullOrWhiteSpace(additionalPipPackages))
        {
            var configured = additionalPipPackages
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var pkg in configured)
            {
                if (!extras.Contains(pkg, StringComparer.OrdinalIgnoreCase))
                    extras.Add(pkg);
            }
        }

        return extras;
    }

    /// <summary>
    /// Extracts the pip package name from a requirements.txt line, stripping version
    /// specifiers, extras markers, and environment markers.
    /// </summary>
    internal static string ExtractPackageName(string requirementLine)
    {
        var commentIdx = requirementLine.IndexOf('#');
        var line = commentIdx >= 0 ? requirementLine[..commentIdx].Trim() : requirementLine.Trim();

        if (line.StartsWith('-'))
            return string.Empty;

        var markerIdx = line.IndexOf(';');
        if (markerIdx >= 0)
            line = line[..markerIdx].Trim();

        var bracketIdx = line.IndexOf('[');
        if (bracketIdx >= 0)
            line = line[..bracketIdx].Trim();

        var specifierChars = new[] { '>', '<', '=', '~', '!' };
        var specIdx = line.IndexOfAny(specifierChars);
        if (specIdx >= 0)
            line = line[..specIdx].Trim();

        return line.Replace('_', '-').ToLowerInvariant();
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
        IProgress<DownloadProgress>? downloadProgress,
        Func<CancellationToken>? skipDownloadTokenProvider,
        CancellationToken cancellationToken)
    {
        var enabledLinks = model.DownloadLinks?.Where(l => l.Enabled).ToList() ?? [];
        var skipToken = skipDownloadTokenProvider?.Invoke() ?? CancellationToken.None;

        if (enabledLinks.Count == 0)
        {
            // Fall back to the model's direct URL
            if (string.IsNullOrWhiteSpace(model.Url))
            {
                progress?.Report(new WorkloadInstallProgress
                {
                    ItemId = model.Id,
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
                    ItemId = model.Id,
                    ItemName = model.Name,
                    Message = $"Skipped {model.Name} (VRAM profile mismatch)"
                });
                return (0, 0);
            }

            var destination = ModelDestinationResolver.Resolve(
                configuration, model, repositoryPath, modelBaseFolder);

            var ok = await downloader.DownloadSingleFileAsync(
                model.Url, destination, model.Name,
                verboseLogging: false, logProgress: null, downloadProgress: downloadProgress,
                cancellationToken, skipToken);

            if (ok)
            {
                TryRenameToConfiguredName(model.Url, destination, model.Name);
            }

            ReportDownloadResult(progress, model.Id, model.Name, ok);
            return ok ? (1, 0) : (0, 1);
        }

        // Select best links via VRAM profile helper
        var linksToDownload = VramProfileHelper.SelectBestMatchingLinks(
            enabledLinks, selectedVramGb, logProgress: null, model.Name);

        if (linksToDownload.Count == 0)
        {
            progress?.Report(new WorkloadInstallProgress
            {
                ItemId = model.Id,
                ItemName = model.Name,
                Message = $"No suitable download links for {model.Name} with {selectedVramGb} GB VRAM"
            });
            return (0, 0);
        }

        int success = 0, fail = 0;

        foreach (var link in linksToDownload)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Get a fresh skip token for each file so a previous skip doesn't affect later files
            skipToken = skipDownloadTokenProvider?.Invoke() ?? CancellationToken.None;

            var destination = !string.IsNullOrWhiteSpace(link.Destination)
                ? ResolvePathFromLink(link.Destination, repositoryPath, configuration)
                : ModelDestinationResolver.Resolve(
                    configuration, model, repositoryPath, modelBaseFolder);

            var ok = await downloader.DownloadSingleFileAsync(
                link.Url, destination, model.Name,
                verboseLogging: false, logProgress: null, downloadProgress: downloadProgress,
                cancellationToken, skipToken);

            if (ok)
            {
                TryRenameToConfiguredName(link.Url, destination, model.Name);
                success++;
            }
            else fail++;
        }

        ReportDownloadResult(progress, model.Id, model.Name, fail == 0);
        return (success, fail);
    }

    /// <summary>
    /// After a successful download, renames the file from the URL-derived name to the
    /// configured name (<c>{model.Name}{ext}</c>) when they differ.
    /// HuggingFace often uses generic file names like <c>diffusion_pytorch_model.safetensors</c>.
    /// </summary>
    private static void TryRenameToConfiguredName(string downloadUrl, string destinationDir, string modelName)
    {
        try
        {
            var urlFileName = ConfigurationCheckerService.GetFileNameFromUrl(downloadUrl);
            var configuredName = ConfigurationCheckerService.DeriveConfiguredFileName(modelName, urlFileName);

            if (configuredName is null) return;

            var downloadedPath = Path.Combine(destinationDir, urlFileName);
            var configuredPath = Path.Combine(destinationDir, configuredName);

            if (File.Exists(downloadedPath) && !File.Exists(configuredPath))
            {
                File.Move(downloadedPath, configuredPath);
                Logger.Information(
                    "Renamed downloaded model from {UrlName} to {ConfiguredName}",
                    urlFileName, configuredName);
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex,
                "Failed to rename downloaded model to configured name for {ModelName}",
                modelName);
        }
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
        IProgress<WorkloadInstallProgress>? progress, Guid id, string name, bool success)
    {
        progress?.Report(new WorkloadInstallProgress
        {
            ItemId = id,
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
