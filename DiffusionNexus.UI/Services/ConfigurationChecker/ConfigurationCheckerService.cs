using DiffusionNexus.Installer.SDK.Models.Configuration;
using DiffusionNexus.Installer.SDK.Models.Entities;
using DiffusionNexus.Installer.SDK.Models.Enums;
using DiffusionNexus.Installer.SDK.Services;
using DiffusionNexus.Installer.SDK.Services.Installation.Utilities;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;
using Serilog;

namespace DiffusionNexus.UI.Services.ConfigurationChecker;

/// <summary>
/// Checks whether a ComfyUI instance has the custom nodes and models
/// expected by a given configuration.
/// </summary>
public sealed class ConfigurationCheckerService : IConfigurationCheckerService
{
    private static readonly ILogger Logger = Log.ForContext<ConfigurationCheckerService>();

    private const string ExtraModelPathsFileName = "extra_model_paths.yaml";

    /// <inheritdoc />
    public Task<ConfigurationCheckResult> CheckConfigurationAsync(
        InstallationConfiguration configuration,
        string comfyUIRootPath,
        ConfigurationCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(comfyUIRootPath);

        options ??= new ConfigurationCheckOptions();

        var installationType = DetectInstallationType(comfyUIRootPath);
        var repositoryPath = GetRepositoryPath(comfyUIRootPath, installationType);

        Logger.Debug("Checking configuration '{Name}' against {Path} ({Type})",
            configuration.Name, comfyUIRootPath, installationType);

        var nodeResults = CheckCustomNodes(configuration, repositoryPath);

        var modelSearchPaths = ResolveModelSearchPaths(
            repositoryPath, installationType, options);

        var modelResults = CheckModels(
            configuration, repositoryPath, modelSearchPaths, options);

        var nodesStatus = ComputeStatus(nodeResults, r => r.IsInstalled);
        var modelsStatus = ComputeStatus(modelResults, r => r.IsInstalled);
        var overallStatus = ComputeOverallStatus(nodesStatus, modelsStatus, nodeResults.Count, modelResults.Count);

        var result = new ConfigurationCheckResult
        {
            OverallStatus = overallStatus,
            CustomNodesStatus = nodesStatus,
            ModelsStatus = modelsStatus,
            InstallationType = installationType,
            CustomNodeResults = nodeResults,
            ModelResults = modelResults
        };

        Logger.Information("Configuration check complete: {Summary} => {Status}",
            result.Summary, result.OverallStatus);

        return Task.FromResult(result);
    }

    #region Installation Type Detection

    /// <summary>
    /// Detects whether the root path is a portable or manual ComfyUI install.
    /// Portable: root contains <c>ComfyUI/main.py</c>.
    /// Manual: root itself contains <c>main.py</c>.
    /// </summary>
    internal static ComfyUIInstallationType DetectInstallationType(string rootPath)
    {
        var portableMainPy = Path.Combine(rootPath, "ComfyUI", "main.py");
        if (File.Exists(portableMainPy))
        {
            return ComfyUIInstallationType.Portable;
        }

        return ComfyUIInstallationType.Manual;
    }

    /// <summary>
    /// Returns the actual ComfyUI repository root (where <c>models/</c> and <c>custom_nodes/</c> live).
    /// </summary>
    internal static string GetRepositoryPath(string rootPath, ComfyUIInstallationType installationType)
    {
        return installationType == ComfyUIInstallationType.Portable
            ? Path.Combine(rootPath, "ComfyUI")
            : rootPath;
    }

    #endregion

    #region Custom Node Checks

    private static List<CustomNodeCheckResult> CheckCustomNodes(
        InstallationConfiguration configuration,
        string repositoryPath)
    {
        var results = new List<CustomNodeCheckResult>();
        var gitRepositories = configuration.GitRepositories ?? [];

        if (gitRepositories.Count == 0)
        {
            return results;
        }

        var customNodesFolder = Path.Combine(repositoryPath, "custom_nodes");

        foreach (var repo in gitRepositories)
        {
            var repoFolderName = PathNormalizer.GetRepositoryName(repo.Url);
            var expectedPath = Path.Combine(customNodesFolder, repoFolderName);

            var isInstalled = Directory.Exists(expectedPath)
                && Directory.EnumerateFileSystemEntries(expectedPath).Any();

            results.Add(new CustomNodeCheckResult
            {
                Id = repo.Id,
                Name = string.IsNullOrWhiteSpace(repo.Name) ? repoFolderName : repo.Name,
                Url = repo.Url,
                IsInstalled = isInstalled,
                ExpectedPath = expectedPath
            });
        }

        return results;
    }

    #endregion

    #region Model Search Path Resolution

    /// <summary>
    /// Builds the list of directories where model files could exist.
    /// Considers: repository models folder, user model base folder,
    /// and any extra_model_paths.yaml entries.
    /// </summary>
    private static List<string> ResolveModelSearchPaths(
        string repositoryPath,
        ComfyUIInstallationType installationType,
        ConfigurationCheckOptions options)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var repoModelsDir = Path.Combine(repositoryPath, "models");
        if (Directory.Exists(repoModelsDir))
        {
            paths.Add(repoModelsDir);
        }

        if (!string.IsNullOrWhiteSpace(options.ModelBaseFolder) && Directory.Exists(options.ModelBaseFolder))
        {
            paths.Add(options.ModelBaseFolder);
        }

        var extraPathsFromYaml = ParseExtraModelPathsYaml(repositoryPath);
        foreach (var extraPath in extraPathsFromYaml)
        {
            if (Directory.Exists(extraPath))
            {
                paths.Add(extraPath);
            }
        }

        // For portable installs, also check the root-level models folder
        if (installationType == ComfyUIInstallationType.Portable)
        {
            var rootParent = Path.GetDirectoryName(repositoryPath);
            if (!string.IsNullOrWhiteSpace(rootParent))
            {
                var portableRootModels = Path.Combine(rootParent, "models");
                if (Directory.Exists(portableRootModels))
                {
                    paths.Add(portableRootModels);
                }
            }
        }

        return [.. paths];
    }

    /// <summary>
    /// Reads the <c>extra_model_paths.yaml</c> from the repository root and extracts
    /// all <c>base_path</c> values plus paths listed under model type keys.
    /// Uses simple line-by-line parsing to avoid adding a YAML library dependency.
    /// </summary>
    internal static List<string> ParseExtraModelPathsYaml(string repositoryPath)
    {
        var results = new List<string>();

        var yamlPath = Path.Combine(repositoryPath, ExtraModelPathsFileName);
        if (!File.Exists(yamlPath))
        {
            return results;
        }

        try
        {
            var lines = File.ReadAllLines(yamlPath);
            string? currentBasePath = null;

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                {
                    continue;
                }

                var colonIndex = line.IndexOf(':');
                if (colonIndex <= 0)
                {
                    continue;
                }

                var key = line[..colonIndex].Trim();
                var value = line[(colonIndex + 1)..].Trim();

                if (key.Equals("base_path", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(value))
                {
                    currentBasePath = NormalizeYamlPath(value);
                    if (Path.IsPathRooted(currentBasePath) && Directory.Exists(currentBasePath))
                    {
                        results.Add(currentBasePath);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(value) && !key.Contains('#'))
                {
                    var resolvedPath = NormalizeYamlPath(value);

                    if (Path.IsPathRooted(resolvedPath))
                    {
                        if (Directory.Exists(resolvedPath))
                        {
                            results.Add(resolvedPath);
                        }
                    }
                    else if (currentBasePath is not null)
                    {
                        var fullPath = Path.Combine(currentBasePath, resolvedPath);
                        if (Directory.Exists(fullPath))
                        {
                            results.Add(fullPath);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to parse {File}", yamlPath);
        }

        return results;
    }

    private static string NormalizeYamlPath(string value)
    {
        if ((value.StartsWith('"') && value.EndsWith('"')) ||
            (value.StartsWith('\'') && value.EndsWith('\'')))
        {
            value = value[1..^1];
        }

        return value.Replace('/', Path.DirectorySeparatorChar).TrimEnd(Path.DirectorySeparatorChar);
    }

    #endregion

    #region Model Checks

    private static List<ModelCheckResult> CheckModels(
        InstallationConfiguration configuration,
        string repositoryPath,
        List<string> modelSearchPaths,
        ConfigurationCheckOptions options)
    {
        var results = new List<ModelCheckResult>();
        var modelDownloads = configuration.ModelDownloads ?? [];

        foreach (var model in modelDownloads)
        {
            if (!model.Enabled)
            {
                continue;
            }

            var result = CheckSingleModel(configuration, model, repositoryPath, modelSearchPaths, options);
            results.Add(result);
        }

        return results;
    }

    private static ModelCheckResult CheckSingleModel(
        InstallationConfiguration configuration,
        ModelDownload model,
        string repositoryPath,
        List<string> modelSearchPaths,
        ConfigurationCheckOptions options)
    {
        var (fileNames, isVramScoped, scopedProfile) = ResolveExpectedFileNames(model, options.SelectedVramGb);

        var searchedPaths = new List<string>();
        var foundPath = string.Empty;

        foreach (var fileName in fileNames)
        {
            if (!string.IsNullOrEmpty(foundPath))
            {
                break;
            }

            // 1. Check model's explicit destination
            if (!string.IsNullOrWhiteSpace(model.Destination))
            {
                var destinationDir = ModelDestinationResolver.Resolve(
                    configuration, model, repositoryPath, options.ModelBaseFolder, options.FolderPathOverrides);
                var explicitPath = Path.Combine(destinationDir, fileName);
                searchedPaths.Add(explicitPath);

                if (File.Exists(explicitPath))
                {
                    foundPath = explicitPath;
                    break;
                }
            }

            // 2. Search all model search paths
            foreach (var basePath in modelSearchPaths)
            {
                var directPath = Path.Combine(basePath, fileName);
                searchedPaths.Add(directPath);
                if (File.Exists(directPath))
                {
                    foundPath = directPath;
                    break;
                }

                if (!Directory.Exists(basePath))
                {
                    continue;
                }

                try
                {
                    foreach (var subDir in Directory.EnumerateDirectories(basePath))
                    {
                        var subPath = Path.Combine(subDir, fileName);
                        searchedPaths.Add(subPath);
                        if (File.Exists(subPath))
                        {
                            foundPath = subPath;
                            break;
                        }
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Skip inaccessible directories
                }

                if (!string.IsNullOrEmpty(foundPath))
                {
                    break;
                }
            }
        }

        return new ModelCheckResult
        {
            Id = model.Id,
            Name = model.Name,
            IsInstalled = model.IsPlaceholder || !string.IsNullOrEmpty(foundPath),
            IsPlaceholder = model.IsPlaceholder,
            IsVramProfileScoped = isVramScoped,
            ScopedVramProfile = scopedProfile,
            SearchedPaths = searchedPaths,
            FoundAtPath = foundPath
        };
    }

    /// <summary>
    /// Determines which file names to look for on disk.
    /// When VRAM profiles are in use, only the best-matching quant variant is expected.
    /// </summary>
    private static (List<string> FileNames, bool IsVramScoped, VramProfile? ScopedProfile)
        ResolveExpectedFileNames(ModelDownload model, int selectedVramGb)
    {
        var enabledLinks = model.DownloadLinks?.Where(l => l.Enabled).ToList() ?? [];

        if (enabledLinks.Count > 0 && enabledLinks.Any(l => l.VramProfile.HasValue) && selectedVramGb > 0)
        {
            var bestLinks = VramProfileHelper.SelectBestMatchingLinks(
                enabledLinks, selectedVramGb, logProgress: null, model.Name);

            if (bestLinks.Count > 0)
            {
                var fileNames = bestLinks
                    .Where(l => !string.IsNullOrWhiteSpace(l.Url))
                    .Select(l => GetFileNameFromUrl(l.Url))
                    .Where(f => !string.IsNullOrEmpty(f))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var scopedProfile = bestLinks
                    .FirstOrDefault(l => l.VramProfile.HasValue)?.VramProfile;

                if (fileNames.Count > 0)
                {
                    return (fileNames, true, scopedProfile);
                }
            }
        }

        var allFileNames = new List<string>();

        if (enabledLinks.Count > 0)
        {
            allFileNames.AddRange(
                enabledLinks
                    .Where(l => !string.IsNullOrWhiteSpace(l.Url))
                    .Select(l => GetFileNameFromUrl(l.Url))
                    .Where(f => !string.IsNullOrEmpty(f)));
        }

        if (!string.IsNullOrWhiteSpace(model.Url))
        {
            var directFileName = GetFileNameFromUrl(model.Url);
            if (!string.IsNullOrEmpty(directFileName))
            {
                allFileNames.Add(directFileName);
            }
        }

        return (allFileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false, null);
    }

    /// <summary>
    /// Extracts the file name from a download URL.
    /// </summary>
    internal static string GetFileNameFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            var fileName = Path.GetFileName(uri.LocalPath);

            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            {
                var segments = uri.Segments;
                for (var i = segments.Length - 1; i >= 0; i--)
                {
                    var segment = segments[i].TrimEnd('/');
                    if (!string.IsNullOrWhiteSpace(segment) && segment.Contains('.'))
                    {
                        return Uri.UnescapeDataString(segment);
                    }
                }
            }

            return string.IsNullOrWhiteSpace(fileName) ? string.Empty : Uri.UnescapeDataString(fileName);
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion

    #region Status Computation

    private static ConfigurationStatus ComputeStatus<T>(List<T> items, Func<T, bool> isInstalled)
    {
        if (items.Count == 0)
        {
            return ConfigurationStatus.Full;
        }

        var installedCount = items.Count(isInstalled);

        if (installedCount == items.Count)
        {
            return ConfigurationStatus.Full;
        }

        return installedCount > 0
            ? ConfigurationStatus.Partial
            : ConfigurationStatus.None;
    }

    private static ConfigurationStatus ComputeOverallStatus(
        ConfigurationStatus nodesStatus,
        ConfigurationStatus modelsStatus,
        int nodeCount,
        int modelCount)
    {
        if (nodeCount == 0 && modelCount == 0)
        {
            return ConfigurationStatus.Full;
        }

        if (nodeCount == 0)
        {
            return modelsStatus;
        }

        if (modelCount == 0)
        {
            return nodesStatus;
        }

        if (nodesStatus == ConfigurationStatus.None && modelsStatus == ConfigurationStatus.None)
        {
            return ConfigurationStatus.None;
        }

        if (nodesStatus == ConfigurationStatus.Full && modelsStatus == ConfigurationStatus.Full)
        {
            return ConfigurationStatus.Full;
        }

        return ConfigurationStatus.Partial;
    }

    #endregion
}
