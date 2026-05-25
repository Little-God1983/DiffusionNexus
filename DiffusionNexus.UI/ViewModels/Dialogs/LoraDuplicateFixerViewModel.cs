using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.UI.ViewModels.Dialogs;

/// <summary>
/// One file card in a duplicate group: thumbnail, name, folder, size, and a Keep button.
/// </summary>
public partial class LoraDuplicateFileItem : ObservableObject
{
    public required LoraDuplicateFile Data { get; init; }
    public required LoraDuplicateGroupItem Group { get; init; }
    public required IAsyncRelayCommand KeepCommand { get; init; }

    [ObservableProperty]
    private Bitmap? _thumbnail;

    public string FileName => Data.FileName;
    public string FolderPath => Data.FolderPath;
    public string DisplayName => Data.DisplayName;
    public string SizeDisplay => FormatBytes(Data.SizeBytes);

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:F1} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// One row in the fixer window: a single duplicate group with its file cards.
/// </summary>
public partial class LoraDuplicateGroupItem : ObservableObject
{
    public ObservableCollection<LoraDuplicateFileItem> Files { get; } = [];

    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = string.Empty;

    [ObservableProperty]
    private bool _isResolved;

    public string Header => Files.Count >= 2
        ? $"{Files.Count} duplicates · {FormatBytes(SizeBytes)} each"
        : $"{Files.Count} file · {FormatBytes(SizeBytes)}";

    public string SavingsDisplay =>
        Files.Count > 1
            ? $"{FormatBytes((Files.Count - 1) * SizeBytes)} would be freed"
            : string.Empty;

    internal void NotifyFilesChanged()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(SavingsDisplay));
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:F1} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:F0} KB";
        return $"{bytes} B";
    }
}

/// <summary>
/// ViewModel for the LoRA Duplicate Fixer window. Holds the list of groups,
/// loads thumbnails on demand, and executes the keep-one-delete-rest flow
/// (file + sidecar deletion + DB cleanup) when the user picks a winner.
/// </summary>
public partial class LoraDuplicateFixerViewModel : ObservableObject
{
    private static readonly string[] SidecarExtensions =
    [
        ".civitai.info",
        ".json",
        ".preview.png",
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".txt",
        ".info"
    ];

    public ObservableCollection<LoraDuplicateGroupItem> Groups { get; } = [];

    [ObservableProperty]
    private int _deletedCount;

    [ObservableProperty]
    private long _bytesReclaimed;

    public string BytesReclaimedDisplay => FormatBytes(BytesReclaimed);

    partial void OnBytesReclaimedChanged(long value)
        => OnPropertyChanged(nameof(BytesReclaimedDisplay));

    public IDialogService? DialogService { get; set; }
    public IUnifiedLogger? Logger { get; set; }

    /// <summary>
    /// Populates the fixer from finder results and kicks off async thumbnail loads.
    /// </summary>
    public void LoadGroups(IEnumerable<LoraDuplicateGroup> groups)
    {
        Groups.Clear();

        foreach (var src in groups)
        {
            var groupItem = new LoraDuplicateGroupItem
            {
                SizeBytes = src.SizeBytes,
                Sha256 = src.Sha256
            };

            foreach (var file in src.Files)
            {
                LoraDuplicateFileItem? item = null;
                item = new LoraDuplicateFileItem
                {
                    Data = file,
                    Group = groupItem,
                    KeepCommand = new AsyncRelayCommand(() => KeepAsync(item!))
                };
                groupItem.Files.Add(item);
            }

            Groups.Add(groupItem);
        }

        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        var ids = Groups
            .SelectMany(g => g.Files)
            .Where(f => f.Data.ThumbnailImageId is not null)
            .Select(f => (Item: f, ImageId: f.Data.ThumbnailImageId!.Value))
            .ToList();

        if (ids.Count == 0) return;

        try
        {
            var services = App.Services;
            if (services is null) return;

            using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateScope();
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

            foreach (var (item, imageId) in ids)
            {
                var (data, _) = await uow.Models.GetImageThumbnailDataAsync(imageId).ConfigureAwait(false);
                if (data is null || data.Length == 0) continue;

                try
                {
                    using var ms = new MemoryStream(data);
                    var bitmap = new Bitmap(ms);
                    await Dispatcher.UIThread.InvokeAsync(() => item.Thumbnail = bitmap);
                }
                catch
                {
                    // Skip thumbnails we can't decode rather than failing the whole window.
                }
            }
        }
        catch (Exception ex)
        {
            Logger?.Warn(LogCategory.General, "DuplicateFixer",
                $"Failed to load duplicate thumbnails: {ex.Message}");
        }
    }

    private async Task KeepAsync(LoraDuplicateFileItem keeper)
    {
        if (DialogService is null) return;
        var group = keeper.Group;
        if (group.IsResolved) return;

        var losers = group.Files.Where(f => f != keeper).ToList();
        if (losers.Count == 0)
        {
            group.IsResolved = true;
            return;
        }

        var loserSummary = string.Join(
            "\n",
            losers.Select(l => $"  • {l.FileName}\n     in {l.FolderPath}"));
        var confirmed = await DialogService.ShowConfirmAsync(
            "Delete duplicate LoRAs",
            $"Keep:\n  {keeper.FileName}\n  in {keeper.FolderPath}\n\n" +
            $"Permanently delete {losers.Count} duplicate(s):\n{loserSummary}\n\n" +
            "Files (and sidecar metadata/thumbnails) will be removed from disk and the database. " +
            "This cannot be undone.");
        if (!confirmed) return;

        var deleted = 0;
        var freed = 0L;

        foreach (var loser in losers)
        {
            try
            {
                DeleteOnDisk(loser.Data.FilePath);
                deleted++;
                freed += loser.Data.SizeBytes;
            }
            catch (Exception ex)
            {
                Logger?.Error(LogCategory.General, "DuplicateFixer",
                    $"Failed to delete '{loser.Data.FilePath}': {ex.Message}", ex);
                await DialogService.ShowMessageAsync(
                    "Delete failed",
                    $"Could not delete '{loser.FileName}':\n{ex.Message}\n\nThe file was left in place.");
                continue;
            }
        }

        try
        {
            await RemoveDbRecordsAsync(losers).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger?.Error(LogCategory.General, "DuplicateFixer",
                $"Failed to remove DB rows for resolved group: {ex.Message}", ex);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var loser in losers)
                group.Files.Remove(loser);

            group.NotifyFilesChanged();
            group.IsResolved = true;
            DeletedCount += deleted;
            BytesReclaimed += freed;
        });
    }

    private void DeleteOnDisk(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
            Logger?.Info(LogCategory.General, "DuplicateFixer", $"Deleted duplicate file: {path}");
        }

        // Delete sidecars sharing the same base name in the same directory.
        // Mirrors the convention used by LoRA imports for .civitai.info / preview images.
        var dir = Path.GetDirectoryName(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(baseName)) return;

        foreach (var ext in SidecarExtensions)
        {
            var sidecar = Path.Combine(dir, baseName + ext);
            if (!File.Exists(sidecar)) continue;
            try
            {
                File.Delete(sidecar);
                Logger?.Info(LogCategory.General, "DuplicateFixer", $"Deleted sidecar: {sidecar}");
            }
            catch (Exception ex)
            {
                Logger?.Warn(LogCategory.General, "DuplicateFixer",
                    $"Could not delete sidecar '{sidecar}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Removes the corresponding <c>ModelFile</c> rows from the database, then
    /// prunes versions and models that have no remaining files. Mirrors the
    /// orphan-cleanup loop in <c>ModelTileViewModel.ExecutePartialDeletion</c>.
    /// </summary>
    private async Task RemoveDbRecordsAsync(IReadOnlyList<LoraDuplicateFileItem> losers)
    {
        if (App.Services is null) return;

        var fileIds = losers.Select(l => l.Data.ModelFileId).Distinct().ToList();
        if (fileIds.Count == 0) return;

        using var scope = App.Services.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var dbContextFactory = scope.ServiceProvider
            .GetRequiredService<IDbContextFactory<DiffusionNexusCoreDbContext>>();
        await using var db = await dbContextFactory.CreateDbContextAsync().ConfigureAwait(false);

        var files = await db.Set<DiffusionNexus.Domain.Entities.ModelFile>()
            .Where(f => fileIds.Contains(f.Id))
            .ToListAsync().ConfigureAwait(false);
        if (files.Count == 0) return;

        var affectedVersionIds = files.Select(f => f.ModelVersionId).Distinct().ToList();

        db.Set<DiffusionNexus.Domain.Entities.ModelFile>().RemoveRange(files);
        await db.SaveChangesAsync().ConfigureAwait(false);

        // Prune versions with no remaining files (cascade removes Images/TriggerWords).
        var orphanVersions = await db.Set<DiffusionNexus.Domain.Entities.ModelVersion>()
            .Where(v => affectedVersionIds.Contains(v.Id))
            .Where(v => !db.Set<DiffusionNexus.Domain.Entities.ModelFile>().Any(f => f.ModelVersionId == v.Id))
            .ToListAsync().ConfigureAwait(false);

        if (orphanVersions.Count > 0)
        {
            var affectedModelIds = orphanVersions.Select(v => v.ModelId).Distinct().ToList();
            db.Set<DiffusionNexus.Domain.Entities.ModelVersion>().RemoveRange(orphanVersions);
            await db.SaveChangesAsync().ConfigureAwait(false);

            // Prune models with no remaining versions.
            var orphanModels = await db.Set<DiffusionNexus.Domain.Entities.Model>()
                .Where(m => affectedModelIds.Contains(m.Id))
                .Where(m => !db.Set<DiffusionNexus.Domain.Entities.ModelVersion>().Any(v => v.ModelId == m.Id))
                .ToListAsync().ConfigureAwait(false);

            if (orphanModels.Count > 0)
            {
                db.Set<DiffusionNexus.Domain.Entities.Model>().RemoveRange(orphanModels);
                await db.SaveChangesAsync().ConfigureAwait(false);
            }
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / (double)gb:F2} GB";
        if (bytes >= mb) return $"{bytes / (double)mb:F1} MB";
        if (bytes >= kb) return $"{bytes / (double)kb:F0} KB";
        return $"{bytes} B";
    }
}
