using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <summary>
/// Auto-registers an installation's model folders as Base Model Folders — the same idea
/// as the Generation Gallery link created for a package's output folder. ComfyUI installs
/// contribute every root <see cref="ComfyUiPathDiscovery.EnumerateModelSearchPaths"/> finds
/// (their own <c>models/</c>, every <c>extra_model_paths.yaml</c> root, and the portable
/// sibling <c>models/</c>); other installer types contribute their plain
/// <c>{InstallationPath}\models</c> folder when it exists.
///
/// Runs when a package is added in the Installer Manager and as an idempotent startup
/// backfill for already-registered packages. Registration never claims the default flag.
/// </summary>
public sealed class BaseModelFolderRegistrar
{
    private static readonly ILogger Logger = Log.ForContext<BaseModelFolderRegistrar>();

    private readonly IAppSettingsService _settingsService;

    public BaseModelFolderRegistrar(IAppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <summary>
    /// Registers all model roots of one package. Idempotency (by path, case-insensitive)
    /// and package re-linking are handled by
    /// <see cref="IAppSettingsService.AddBaseModelFolderAsync(string, int?, CancellationToken)"/>.
    /// </summary>
    /// <returns>How many folders were newly inserted (0 when everything already existed).</returns>
    public async Task<int> RegisterPackageFoldersAsync(InstallerPackage package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var added = 0;
        foreach (var root in ResolveModelRoots(package))
        {
            if (await _settingsService
                    .AddBaseModelFolderAsync(root, package.Id, cancellationToken)
                    .ConfigureAwait(false))
            {
                added++;
                Logger.Information("Registered base model folder {Root} for installation '{Name}'.", root, package.Name);
            }
        }

        return added;
    }

    /// <summary>
    /// Startup backfill over all packages. Never throws — a registration failure must
    /// not block app startup.
    /// </summary>
    /// <returns>
    /// How many folders were newly inserted across all packages. Callers should publish a
    /// settings-saved notification when this is non-zero so an already-loaded Settings
    /// page reloads its Base Model Folders list.
    /// </returns>
    public async Task<int> EnsureRegisteredAsync(IEnumerable<InstallerPackage> packages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var added = 0;
        foreach (var package in packages)
        {
            try
            {
                added += await RegisterPackageFoldersAsync(package, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "Failed to register base model folders for '{Name}'.", package.Name);
            }
        }

        return added;
    }

    /// <summary>
    /// Resolves the model roots an installation contributes to the registry.
    ///
    /// Uses <see cref="ComfyUiPathDiscovery.EnumerateModelRoots"/>, not
    /// <c>EnumerateModelSearchPaths</c>: the latter answers "where might a model file be?"
    /// and therefore also returns every per-category folder from
    /// <c>extra_model_paths.yaml</c>. Registering those as roots produced a Settings list of
    /// twenty rows — <c>D:\Models</c> alongside <c>D:\Models\Lora</c>, <c>D:\Models\VAE</c>
    /// and the rest — where only the base path is a root.
    /// </summary>
    internal static IReadOnlyList<string> ResolveModelRoots(InstallerPackage package)
    {
        if (string.IsNullOrWhiteSpace(package.InstallationPath) || !Directory.Exists(package.InstallationPath))
        {
            return [];
        }

        if (package.Type == InstallerType.ComfyUI)
        {
            return ComfyUiPathDiscovery.EnumerateModelRoots(package.InstallationPath);
        }

        var modelsDir = Path.Combine(package.InstallationPath, "models");
        return Directory.Exists(modelsDir) ? [modelsDir] : [];
    }

    /// <summary>
    /// Removes Base Model Folder rows this app registered that are not roots at all —
    /// per-category folders nested inside a root of the same installation, added by the
    /// pre-fix registrar (see <see cref="ResolveModelRoots"/>).
    ///
    /// Never throws: like the backfill, a failure here must not block startup.
    /// </summary>
    /// <returns>How many rows were removed (0 when there was nothing redundant).</returns>
    public async Task<int> PruneRedundantFoldersAsync(
        IEnumerable<InstallerPackage> packages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packages);

        try
        {
            var rows = await _settingsService
                .GetAllBaseModelFoldersAsync(cancellationToken)
                .ConfigureAwait(false);

            var rootsByPackage = new Dictionary<int, IReadOnlyList<string>>();
            foreach (var package in packages)
            {
                try
                {
                    rootsByPackage[package.Id] = ResolveModelRoots(package);
                }
                catch (Exception ex)
                {
                    Logger.Warning(ex, "Failed to resolve model roots for '{Name}' while pruning.", package.Name);
                }
            }

            var redundant = RedundantBaseModelFolders.Resolve(rows, rootsByPackage);
            if (redundant.Count == 0)
            {
                return 0;
            }

            foreach (var row in redundant)
            {
                Logger.Information(
                    "Removing Base Model Folder {Path}: a category folder inside a registered root, not a root itself.",
                    row.FolderPath);
            }

            return await _settingsService
                .RemoveBaseModelFoldersAsync([.. redundant.Select(r => r.Id)], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to prune redundant Base Model Folders.");
            return 0;
        }
    }
}
