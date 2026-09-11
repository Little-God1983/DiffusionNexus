namespace DiffusionNexus.Domain.Services;

/// <summary>
/// Manages per-folder image favorites using a single JSON file per folder.
/// </summary>
public interface IImageFavoritesService
{
    /// <summary>
    /// Returns the set of favorite file names (not full paths) for the given folder.
    /// </summary>
    Task<IReadOnlySet<string>> GetFavoritesAsync(string folderPath, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a specific file is marked as favorite.
    /// </summary>
    Task<bool> IsFavoriteAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Toggles the favorite state for a file. Returns the new state.
    /// </summary>
    Task<bool> ToggleFavoriteAsync(string filePath, CancellationToken ct = default);

    /// <summary>
    /// Sets the favorite state for a file explicitly.
    /// </summary>
    Task SetFavoriteAsync(string filePath, bool isFavorite, CancellationToken ct = default);

    /// <summary>
    /// Drops favorites whose file no longer exists in <paramref name="folderPath"/> and persists
    /// the result. Returns how many entries were removed. Removes nothing when the folder itself
    /// is missing, so an unplugged drive does not wipe its favorites.
    /// </summary>
    Task<int> RemoveMissingAsync(string folderPath, CancellationToken ct = default);
}
