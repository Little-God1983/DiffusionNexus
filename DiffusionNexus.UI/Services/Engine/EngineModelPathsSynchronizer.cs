using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services.Diffusion;
using Serilog;

namespace DiffusionNexus.UI.Services.Engine;

/// <summary>What a sync attempt did, so callers can log it and tests can assert on it.</summary>
/// <param name="Written">True when the file on disk was changed.</param>
/// <param name="FilePath">The file that was written, or would have been.</param>
/// <param name="Roots">The model libraries wired into the engine, in search order.</param>
/// <param name="SkipReason">Why nothing was done, when nothing was done.</param>
public sealed record EngineModelPathsSyncResult(
    bool Written,
    string? FilePath,
    IReadOnlyList<string> Roots,
    string? SkipReason = null);

/// <summary>
/// Keeps the app-owned engine's <c>extra_model_paths.yaml</c> in step with Settings →
/// Model Storage Folders.
///
/// <para>
/// The engine deliberately downloads no models of its own; it reads the libraries the user
/// already has. Wiring that up is the engine installer's job, but the SDK option it uses
/// (<c>InstallationOptions.ModelBaseFolder</c>) holds one folder, so an install could only ever
/// declare the first — every other registered folder stayed invisible, and a user whose models
/// live anywhere else got a workload reporting nothing installed and an engine that genuinely
/// could not load them. Re-running this on every engine start also means changing the folder list
/// in Settings takes effect without reinstalling anything.
/// </para>
///
/// <para>
/// Never throws: a failure here must not stop the engine from starting or the app from booting.
/// The pre-existing single-root file the SDK wrote stays in place in that case.
/// </para>
/// </summary>
public sealed class EngineModelPathsSynchronizer
{
    private const string LogSource = "Diffusion Nexus Engine";
    private static readonly ILogger Logger = Log.ForContext<EngineModelPathsSynchronizer>();

    private readonly IUnitOfWork _unitOfWork;
    private readonly IModelFolderCatalog _modelFolderCatalog;
    private readonly IUnifiedLogger? _unifiedLogger;

    public EngineModelPathsSynchronizer(
        IUnitOfWork unitOfWork,
        IModelFolderCatalog modelFolderCatalog,
        IUnifiedLogger? unifiedLogger = null)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(modelFolderCatalog);

        _unitOfWork = unitOfWork;
        _modelFolderCatalog = modelFolderCatalog;
        _unifiedLogger = unifiedLogger;
    }

    /// <summary>
    /// Rewrites the engine's <c>extra_model_paths.yaml</c> from the current Base Model Folders.
    /// </summary>
    /// <param name="engineInstallRoot">
    /// The engine's install root. Omit to use the registered app-managed installation — pass it
    /// explicitly straight after an install, before the database row is guaranteed to be there.
    /// </param>
    public async Task<EngineModelPathsSyncResult> SyncAsync(
        string? engineInstallRoot = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var packages = await _unitOfWork.InstallerPackages.GetAllAsync(cancellationToken)
                .ConfigureAwait(false);

            var installRoot = engineInstallRoot
                ?? packages.FirstOrDefault(p => p.IsAppManaged)?.InstallationPath;

            if (string.IsNullOrWhiteSpace(installRoot))
            {
                return Skip("the engine is not installed");
            }

            var mainPy = ManagedEngineLocator.ResolveMainPy(installRoot);
            if (mainPy is null)
            {
                return Skip($"no ComfyUI entry point under '{installRoot}'");
            }

            var repositoryPath = Path.GetDirectoryName(mainPy)!;
            var filePath = Path.Combine(repositoryPath, ComfyExtraModelPaths.FileName);

            var roots = await ResolveRootsAsync(packages, installRoot, cancellationToken)
                .ConfigureAwait(false);

            if (roots.Count == 0)
            {
                // Leaving whatever is there beats truncating the file to a header: the engine's own
                // models/ folder still works, and an empty registry is a transient state (the
                // startup backfill may not have run yet).
                return Skip("no model folders are registered");
            }

            var content = EngineModelPathsFile.Compose(roots);
            var existing = File.Exists(filePath) ? await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false) : null;

            var rootPaths = roots.Select(r => r.BasePath).ToList();

            if (string.Equals(existing, content, StringComparison.Ordinal))
            {
                LogDebug(
                    $"Engine model paths already up to date ({rootPaths.Count} folder(s)): " +
                    string.Join(" | ", rootPaths));
                return new EngineModelPathsSyncResult(false, filePath, rootPaths);
            }

            await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);

            LogInfo(
                $"Wired {rootPaths.Count} model folder(s) into the engine ({filePath}): " +
                string.Join(" | ", rootPaths));

            return new EngineModelPathsSyncResult(true, filePath, rootPaths);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Failed to sync the engine's extra_model_paths.yaml.");
            _unifiedLogger?.Warn(LogCategory.Configuration, LogSource,
                $"Could not update the engine's model folders: {ex.Message}");
            return new EngineModelPathsSyncResult(false, null, [], ex.Message);
        }
    }

    /// <summary>
    /// The libraries to declare, in search order, each carrying the category mapping of whichever
    /// installation declared it.
    /// </summary>
    private async Task<IReadOnlyList<EngineModelRoot>> ResolveRootsAsync(
        IEnumerable<InstallerPackage> packages,
        string engineInstallRoot,
        CancellationToken cancellationToken)
    {
        // The catalog is the app's single authority on "where are the user's models": enabled Base
        // Model Folders, starred default first, existing directories only, plus the app's own
        // LocalAppData model folder (where Core downloads land).
        var searchRoots = await _modelFolderCatalog.GetSearchRootsAsync(cancellationToken)
            .ConfigureAwait(false);

        // Every ComfyUI installation's yaml, so a library declared with renamed category folders
        // (text_encoders: TextEncoders/) reaches the engine with that mapping intact. The engine
        // itself is excluded — reading back the file we are about to write would make the mapping
        // self-referential and freeze the first version of it in place forever.
        var sections = packages
            .Where(p => p.Type == InstallerType.ComfyUI && !p.IsAppManaged)
            .Where(p => !string.IsNullOrWhiteSpace(p.InstallationPath))
            .SelectMany(p => ComfyExtraModelPaths.ParseFile(ResolveRepositoryPath(p.InstallationPath)))
            .ToList();

        var roots = new List<EngineModelRoot>();

        foreach (var root in searchRoots)
        {
            // The engine's own models/ folder is registered like any other, but ComfyUI already
            // searches it by default; declaring it again would only add noise.
            if (FolderPathMatch.Contains(engineInstallRoot, root))
            {
                continue;
            }

            roots.Add(new EngineModelRoot(root, ComfyExtraModelPaths.CategoriesFor(sections, root)));
        }

        return roots;
    }

    /// <summary>
    /// Where <c>extra_model_paths.yaml</c> lives for an installation — next to <c>main.py</c>,
    /// which is either the registered path itself or a <c>ComfyUI/</c> subfolder of it.
    /// </summary>
    private static string ResolveRepositoryPath(string installationPath)
    {
        var mainPy = ManagedEngineLocator.ResolveMainPy(installationPath);
        return mainPy is null ? installationPath : Path.GetDirectoryName(mainPy)!;
    }

    private EngineModelPathsSyncResult Skip(string reason)
    {
        LogDebug($"Engine model paths not synced: {reason}.");
        return new EngineModelPathsSyncResult(false, null, [], reason);
    }

    private void LogInfo(string message)
    {
        Logger.Information(message);
        _unifiedLogger?.Info(LogCategory.Configuration, LogSource, message);
    }

    private void LogDebug(string message)
    {
        Logger.Debug(message);
        _unifiedLogger?.Debug(LogCategory.Configuration, LogSource, message);
    }
}
