using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace DiffusionNexus.UI.Views.Controls;

/// <summary>
/// Shared helpers for putting image file paths onto the system clipboard and dragging them out to
/// other apps (Explorer, …). Extracted so any image surface (Generation Gallery, pipeline result
/// strip, …) copies/drags files the same way. Windows-focused; other platforms degrade gracefully.
/// </summary>
public static class ImageFileTransfer
{
    /// <summary>
    /// Copies the given files to the clipboard as file references, so they can be pasted into Windows
    /// Explorer or other apps. No-ops when there is nothing to copy or no clipboard is available.
    /// </summary>
    public static async Task CopyFilesToClipboardAsync(TopLevel? topLevel, IReadOnlyList<string> filePaths)
    {
        if (topLevel is null || filePaths.Count == 0) return;

        var clipboard = topLevel.Clipboard;
        if (clipboard is null) return;

        var storageItems = await ResolveStorageItemsAsync(topLevel, filePaths);
        if (storageItems.Count == 0) return;

        var dataObject = new DataTransfer();
        foreach (var item in storageItems)
            dataObject.Add(DataTransferItem.CreateFile(item));

        // TODO: Linux implementation for clipboard file copy.
        await clipboard.SetDataAsync(dataObject);
    }

    /// <summary>
    /// Builds a <see cref="DataTransfer"/> of file references for a drag-out operation, or null when
    /// none of the paths could be resolved to storage items.
    /// </summary>
    public static async Task<DataTransfer?> BuildFileDragDataAsync(TopLevel? topLevel, IReadOnlyList<string> filePaths)
    {
        if (topLevel is null || filePaths.Count == 0) return null;

        var storageItems = await ResolveStorageItemsAsync(topLevel, filePaths);
        if (storageItems.Count == 0) return null;

        var dataObject = new DataTransfer();
        foreach (var item in storageItems)
            dataObject.Add(DataTransferItem.CreateFile(item));
        return dataObject;
    }

    /// <summary>
    /// Resolves file paths to <see cref="IStorageItem"/> instances for clipboard and drag-and-drop.
    /// Paths that can't be resolved (deleted / inaccessible) are skipped.
    /// </summary>
    public static async Task<List<IStorageItem>> ResolveStorageItemsAsync(TopLevel? topLevel, IReadOnlyList<string> filePaths)
    {
        var storageProvider = topLevel?.StorageProvider;
        if (storageProvider is null) return [];

        var items = new List<IStorageItem>(filePaths.Count);
        foreach (var path in filePaths)
        {
            try
            {
                var file = await storageProvider.TryGetFileFromPathAsync(
                    new Uri($"file:///{path.Replace('\\', '/').TrimStart('/')}"));
                if (file is not null)
                    items.Add(file);
            }
            catch
            {
                // Skip files that can't be resolved (deleted, inaccessible).
            }
        }

        return items;
    }
}
