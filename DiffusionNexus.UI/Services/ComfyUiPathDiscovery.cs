using DiffusionNexus.UI.Services.ConfigurationChecker;
using DiffusionNexus.UI.Services.ConfigurationChecker.Models;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Static helpers that enumerate every on-disk directory ComfyUI might serve
/// models from for a given installation root. Reused by:
///  • the configuration checker (which validates per-workload model presence),
///  • the captioning model manager (which needs to find user-supplied GGUFs
///    that already live in the ComfyUI model tree).
///
/// Mirrors ComfyUI's own path resolution: the repository's <c>models/</c>
/// folder, plus every <c>base_path</c> / model-type entry in
/// <c>extra_model_paths.yaml</c>, plus a portable root fallback.
/// </summary>
public static class ComfyUiPathDiscovery
{
    /// <summary>
    /// Returns every directory that should be considered a model root for the
    /// given ComfyUI installation. Includes <c>models/</c>, deeper subfolders
    /// (e.g. <c>models/clip</c>, <c>models/text_encoders</c>) are NOT returned
    /// individually — callers walk recursively from each root.
    /// </summary>
    /// <param name="comfyUiRootPath">
    /// Path entered as the installation root. Works for both manual installs
    /// (root contains <c>main.py</c>) and portable installs (root contains a
    /// <c>ComfyUI/</c> subfolder).
    /// </param>
    public static IReadOnlyList<string> EnumerateModelSearchPaths(string comfyUiRootPath)
    {
        if (string.IsNullOrWhiteSpace(comfyUiRootPath) || !Directory.Exists(comfyUiRootPath))
        {
            return [];
        }

        var installationType = ConfigurationCheckerService.DetectInstallationType(comfyUiRootPath);
        var repositoryPath = ConfigurationCheckerService.GetRepositoryPath(comfyUiRootPath, installationType);

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var repoModelsDir = Path.Combine(repositoryPath, "models");
        if (Directory.Exists(repoModelsDir))
        {
            paths.Add(repoModelsDir);
        }

        foreach (var extraPath in ConfigurationCheckerService.ParseExtraModelPathsYaml(repositoryPath))
        {
            if (Directory.Exists(extraPath))
            {
                paths.Add(extraPath);
            }
        }

        // Portable installs sometimes keep a sibling top-level "models" folder
        // next to the inner ComfyUI/ directory. Honour that layout too.
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
}
