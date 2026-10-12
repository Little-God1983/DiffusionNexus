using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using Serilog;

namespace DiffusionNexus.UI.Services.Diffusion;

/// <inheritdoc cref="IModelFolderCatalog"/>
public sealed class ModelFolderCatalog : IModelFolderCatalog
{
    private static readonly ILogger Logger = Log.ForContext<ModelFolderCatalog>();

    private readonly IAppSettingsService _settingsService;

    /// <summary>
    /// Built-in model storage root used when no Base Model Folder is configured:
    /// <c>%LOCALAPPDATA%\DiffusionNexus\Models</c>.
    /// </summary>
    public static string FallbackRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Models");

    private readonly IUnitOfWork? _unitOfWork;

    /// <param name="settingsService">The Base Model Folders.</param>
    /// <param name="unitOfWork">
    /// Reads the installer packages, so folders inside the app-managed Engine are never a download
    /// target. Without it every enabled folder is offered.
    /// </param>
    public ModelFolderCatalog(IAppSettingsService settingsService, IUnitOfWork? unitOfWork = null)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _unitOfWork = unitOfWork;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelFolderOption>> GetDownloadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var enabled = await _settingsService
            .GetEnabledBaseModelFoldersAsync(cancellationToken)
            .ConfigureAwait(false);

        // A folder inside the app-managed Engine is read (search roots) but never downloaded into:
        // Uninstall Engine deletes everything inside it.
        var engineRoots = await AppManagedRootsAsync(cancellationToken).ConfigureAwait(false);
        var folders = enabled
            .Where(f => !engineRoots.Any(root => FolderPathMatch.Contains(root, f.FolderPath)))
            .ToList();

        if (folders.Count == 0)
        {
            return [new ModelFolderOption(FallbackRoot, IsDefault: true, Directory.Exists(FallbackRoot))];
        }

        // Default first, then the remaining rows in their configured order.
        // When no row is flagged, the first enabled row acts as the default.
        var ordered = folders
            .OrderByDescending(f => f.IsDefault)
            .ThenBy(f => f.Order)
            .ToList();

        var targets = ordered
            .Select((f, index) => new ModelFolderOption(
                f.FolderPath,
                IsDefault: index == 0,
                Directory.Exists(f.FolderPath)))
            .ToList();

        // The app's own folder is always selectable, even when other folders are
        // registered — it just never claims the default from a configured row.
        if (!targets.Any(t => string.Equals(t.Path, FallbackRoot, StringComparison.OrdinalIgnoreCase)))
        {
            targets.Add(new ModelFolderOption(FallbackRoot, IsDefault: false, Directory.Exists(FallbackRoot)));
        }

        return targets;
    }

    /// <inheritdoc />
    public async Task<string> GetDefaultDownloadRootAsync(CancellationToken cancellationToken = default)
    {
        var targets = await GetDownloadTargetsAsync(cancellationToken).ConfigureAwait(false);
        var root = targets[0].Path;

        try
        {
            Directory.CreateDirectory(root);
            return root;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex,
                "Default model folder {Root} cannot be created — falling back to {Fallback}",
                root, FallbackRoot);
            Directory.CreateDirectory(FallbackRoot);
            return FallbackRoot;
        }
    }

    /// <summary>
    /// The install folders of the app-managed packages (the Diffusion Nexus Engine). By path, not by the
    /// row's package link: an old startup backfill linked the user's own libraries to the Engine row. A
    /// blank, relative or drive-root path is skipped (a corrupted row must not hide a whole drive), and a
    /// failing read skips the filter rather than blocking downloads.
    /// </summary>
    private async Task<IReadOnlyList<string>> AppManagedRootsAsync(CancellationToken cancellationToken)
    {
        if (_unitOfWork is null) return [];
        try
        {
            _unitOfWork.ClearChangeTracker();
            var packages = await _unitOfWork.InstallerPackages.GetAllAsync(cancellationToken).ConfigureAwait(false);
            return packages
                .Where(p => p.IsAppManaged && Engine.EngineRemovalPlanner.IsSafeEngineRoot(p.InstallationPath))
                .Select(p => p.InstallationPath)
                .ToList();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Logger.Warning(ex, "Could not read the installer packages; every enabled model folder is offered as a download target");
            return [];
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSearchRootsAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _settingsService
            .GetEnabledBaseModelFoldersAsync(cancellationToken)
            .ConfigureAwait(false);

        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in folders.OrderByDescending(f => f.IsDefault).ThenBy(f => f.Order).Select(f => f.FolderPath)
                     .Append(FallbackRoot))
        {
            if (string.IsNullOrWhiteSpace(path) || !seen.Add(path))
                continue;

            if (Directory.Exists(path))
                roots.Add(path);
        }

        return roots;
    }
}
