using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    public ModelFolderCatalog(IAppSettingsService settingsService)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModelFolderOption>> GetDownloadTargetsAsync(CancellationToken cancellationToken = default)
    {
        var folders = await _settingsService
            .GetEnabledBaseModelFoldersAsync(cancellationToken)
            .ConfigureAwait(false);

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
