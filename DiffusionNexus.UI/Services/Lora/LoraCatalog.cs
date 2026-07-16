using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace DiffusionNexus.UI.Services.Lora;

/// <summary>
/// Default <see cref="ILoraCatalog"/>. Reads the cached model DB the LoRA Viewer's Installed tab is
/// built from (<see cref="IModelSyncService.LoadCachedFilesAsync"/>) — which already restricts to the
/// enabled <c>LoraSources</c> roots and LoRA-family types — and filters by the raw base-model string
/// (<c>ModelVersion.BaseModelRaw</c>). A fresh DI scope is created per query so the (transient,
/// DbContext-backed) sync service never shares state across threads/calls.
/// </summary>
public sealed class LoraCatalog : ILoraCatalog
{
    private static readonly ILogger Log = Serilog.Log.ForContext<LoraCatalog>();

    private readonly IServiceScopeFactory _scopeFactory;

    public LoraCatalog(IServiceScopeFactory scopeFactory)
        => _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));

    public async Task<IReadOnlyList<AvailableLora>> GetInstalledLorasAsync(
        IReadOnlyCollection<string>? baseModelFilter,
        CancellationToken cancellationToken = default)
    {
        var filter = baseModelFilter is { Count: > 0 }
            ? new HashSet<string>(baseModelFilter, StringComparer.OrdinalIgnoreCase)
            : null;

        try
        {
            // One scope: the (transient, DbContext-backed) sync service + unit-of-work share a DbContext,
            // used strictly sequentially below.
            using var scope = _scopeFactory.CreateScope();
            var sync = scope.ServiceProvider.GetRequiredService<IModelSyncService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            var files = await sync.LoadCachedFilesAsync(cancellationToken).ConfigureAwait(false);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<AvailableLora>();

            foreach (var f in files)
            {
                var path = f.File.LocalPath;
                if (string.IsNullOrWhiteSpace(path))
                    continue;

                var baseModel = f.Version.BaseModelRaw;
                if (filter is not null && (string.IsNullOrWhiteSpace(baseModel) || !filter.Contains(baseModel)))
                    continue;

                if (!seen.Add(path)) // one tile per disk copy (#380); the picker only needs the path once
                    continue;

                var name = !string.IsNullOrWhiteSpace(f.Model.Name)
                    ? f.Model.Name
                    : Path.GetFileNameWithoutExtension(path);

                var thumbnail = await LoadThumbnailAsync(unitOfWork, f, cancellationToken).ConfigureAwait(false);

                result.Add(new AvailableLora(name, path, baseModel, thumbnail));
            }

            return result
                .OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load installed LoRAs from the model database.");
            return [];
        }
    }

    /// <summary>
    /// Loads the LoRA's preview image (primary image's deferred thumbnail BLOB), the same source the
    /// LoRA Viewer Installed tab renders. Returns null if there's no cached preview.
    /// </summary>
    private static async Task<Bitmap?> LoadThumbnailAsync(
        IUnitOfWork unitOfWork, InstalledModelFile file, CancellationToken cancellationToken)
    {
        try
        {
            var primaryImage = file.Version.Images?.OrderBy(i => i.SortOrder).FirstOrDefault();
            if (primaryImage is null)
                return null;

            var (data, _) = await unitOfWork.Models
                .GetImageThumbnailDataAsync(primaryImage.Id, cancellationToken)
                .ConfigureAwait(false);

            if (data is not { Length: > 0 })
                return null;

            using var ms = new MemoryStream(data);
            return new Bitmap(ms);
        }
        catch
        {
            return null; // preview is decorative — never fail the catalog over it
        }
    }
}
