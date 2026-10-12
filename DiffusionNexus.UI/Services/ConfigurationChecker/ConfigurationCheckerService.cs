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
    public static ComfyUIInstallationType DetectInstallationType(string rootPath)
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
    public static string GetRepositoryPath(string rootPath, ComfyUIInstallationType installationType)
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
    /// all <c>base_path</c> values plus paths listed under model type keys — every
    /// directory a model file might sit in, which is what searching needs.
    ///
    /// This is NOT a list of model <em>roots</em>: a <c>loras: Lora/</c> entry resolves to
    /// a single category folder, not a directory that holds the ComfyUI category
    /// subfolders. Use <see cref="ParseExtraModelPathRoots"/> when you need roots.
    /// Uses simple line-by-line parsing to avoid adding a YAML library dependency.
    /// </summary>
    public static List<string> ParseExtraModelPathsYaml(string repositoryPath)
        => [.. EnumerateExtraModelPathEntries(repositoryPath).Select(entry => entry.Path)];

    /// <summary>
    /// The <c>base_path</c> values only — the shared model libraries an installation
    /// points at, each of which holds the per-category subfolders. These are model roots
    /// in the sense <see cref="Domain.Entities.BaseModelFolder"/> means it; the per-category
    /// entries that <see cref="ParseExtraModelPathsYaml"/> also returns are not.
    /// </summary>
    public static List<string> ParseExtraModelPathRoots(string repositoryPath)
        => [.. EnumerateExtraModelPathEntries(repositoryPath)
                .Where(entry => entry.IsBasePath)
                .Select(entry => entry.Path)];

    /// <summary>
    /// Every path the YAML resolves to, each flagged with whether it came from a
    /// <c>base_path</c> key (a root) or from a model-type key (a category folder).
    /// </summary>
    private static List<(bool IsBasePath, string Path)> EnumerateExtraModelPathEntries(string repositoryPath)
    {
        var results = new List<(bool IsBasePath, string Path)>();

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
                        results.Add((IsBasePath: true, Path: currentBasePath));
                    }
                }
                else if (!string.IsNullOrWhiteSpace(value) && !key.Contains('#'))
                {
                    var resolvedPath = NormalizeYamlPath(value);

                    if (Path.IsPathRooted(resolvedPath))
                    {
                        if (Directory.Exists(resolvedPath))
                        {
                            results.Add((IsBasePath: false, Path: resolvedPath));
                        }
                    }
                    else if (currentBasePath is not null)
                    {
                        var fullPath = Path.Combine(currentBasePath, resolvedPath);
                        if (Directory.Exists(fullPath))
                        {
                            results.Add((IsBasePath: false, Path: fullPath));
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
        var (fileNames, isVramScoped, scopedProfile) = ResolveExpectedFileNames(
            model, options.SelectedVramGb, onlyNamesLeftOnDisk: options.CollectAllMatches);

        var searchedPaths = new List<string>();
        var found = new List<string>();

        foreach (var fileName in fileNames)
        {
            foreach (var candidate in CandidatePaths(configuration, model, repositoryPath, modelSearchPaths, options, fileName))
            {
                searchedPaths.Add(candidate);
                if (!File.Exists(candidate))
                    continue;

                var full = Path.GetFullPath(candidate);
                if (!found.Contains(full, StringComparer.OrdinalIgnoreCase))
                    found.Add(full);

                if (!options.CollectAllMatches)
                    break;
            }

            if (found.Count > 0 && !options.CollectAllMatches)
                break;
        }

        var foundPath = found.Count > 0 ? found[0] : string.Empty;

        var hasVramProfiles = model.DownloadLinks?.Any(l => l.Enabled && l.VramProfile.HasValue) ?? false;

        return new ModelCheckResult
        {
            Id = model.Id,
            Name = model.Name,
            IsInstalled = model.IsPlaceholder || !string.IsNullOrEmpty(foundPath),
            IsPlaceholder = model.IsPlaceholder,
            IsFindableByName = fileNames.Any(Path.HasExtension),
            IsVramProfileScoped = isVramScoped,
            ScopedVramProfile = scopedProfile,
            HasVramProfiles = hasVramProfiles,
            SearchedPaths = searchedPaths,
            FoundAtPath = foundPath,
            AllFoundPaths = found
        };
    }

    /// <summary>
    /// Where a file named <paramref name="fileName"/> may sit, in search order: the model's explicit
    /// destination, then each search folder and its direct subfolders.
    /// </summary>
    private static IEnumerable<string> CandidatePaths(
        InstallationConfiguration configuration,
        ModelDownload model,
        string repositoryPath,
        List<string> modelSearchPaths,
        ConfigurationCheckOptions options,
        string fileName)
    {
        if (!string.IsNullOrWhiteSpace(model.Destination))
        {
            var destinationDir = ModelDestinationResolver.Resolve(
                configuration, model, repositoryPath, options.ModelBaseFolder, options.FolderPathOverrides);
            yield return Path.Combine(destinationDir, fileName);
        }

        foreach (var basePath in modelSearchPaths)
        {
            yield return Path.Combine(basePath, fileName);

            if (!Directory.Exists(basePath))
                continue;

            List<string> subDirs;
            try
            {
                subDirs = Directory.EnumerateDirectories(basePath).ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue; // Skip inaccessible directories
            }

            foreach (var subDir in subDirs)
                yield return Path.Combine(subDir, fileName);
        }
    }

    /// <summary>
    /// Determines which file names to look for on disk.
    /// When VRAM profiles are in use, only the best-matching quant variant is expected.
    /// With <paramref name="onlyNamesLeftOnDisk"/> (removal: every match is collected), a URL-derived
    /// name the installer renames to its configured name is replaced by that name rather than kept
    /// beside it: a generic HuggingFace name like <c>diffusion_pytorch_model.safetensors</c> found
    /// elsewhere belongs to some other model.
    /// </summary>
    private static (List<string> FileNames, bool IsVramScoped, VramProfile? ScopedProfile)
        ResolveExpectedFileNames(ModelDownload model, int selectedVramGb, bool onlyNamesLeftOnDisk = false)
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
                    AppendConfiguredFileNames(fileNames, model.Name, onlyNamesLeftOnDisk);
                    return (fileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), true, scopedProfile);
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

        AppendConfiguredFileNames(allFileNames, model.Name, onlyNamesLeftOnDisk);

        return (allFileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), false, null);
    }

    /// <summary>
    /// Appends configured file names derived from the model display name and
    /// extensions found in the existing URL-derived file names. This handles the
    /// common case where HuggingFace uses generic names like
    /// <c>diffusion_pytorch_model.safetensors</c> instead of the configured model name.
    /// With <paramref name="replaceUrlNames"/>, each URL name that has a configured name is dropped:
    /// the installer's rename (<c>WorkloadInstallService.TryRenameToConfiguredName</c>) leaves only the
    /// configured name on disk.
    /// </summary>
    private static void AppendConfiguredFileNames(List<string> fileNames, string modelName, bool replaceUrlNames = false)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return;

        foreach (var urlFileName in fileNames.ToList())
        {
            var configuredName = DeriveConfiguredFileName(modelName, urlFileName);
            if (configuredName is null)
                continue;

            if (replaceUrlNames)
                fileNames.Remove(urlFileName);

            if (!fileNames.Contains(configuredName, StringComparer.OrdinalIgnoreCase))
                fileNames.Add(configuredName);
        }
    }

    /// <summary>
    /// Derives a configured file name by combining the model's display name with the
    /// extension of the URL-derived file name. Used to find models renamed after
    /// downloading from sources like HuggingFace where generic file names
    /// (e.g. <c>diffusion_pytorch_model.safetensors</c>) are common.
    /// Returns <c>null</c> when the names already match or derivation is not possible.
    /// </summary>
    internal static string? DeriveConfiguredFileName(string modelName, string urlFileName)
    {
        if (string.IsNullOrWhiteSpace(modelName) || string.IsNullOrWhiteSpace(urlFileName))
            return null;

        var ext = Path.GetExtension(urlFileName);
        if (string.IsNullOrEmpty(ext))
            return null;

        var configured = modelName.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            ? modelName
            : modelName + ext;

        return configured.Equals(urlFileName, StringComparison.OrdinalIgnoreCase)
            ? null
            : configured;
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
