using System.Collections.Concurrent;
using System.Text.Json;
using DiffusionNexus.Domain.Services;

namespace DiffusionNexus.UI.Services;

/// <summary>
/// Persists favorite file names in a <c>.favorites.json</c> file per folder.
/// Uses an in-memory cache to avoid repeated disk reads.
/// </summary>
public sealed class ImageFavoritesService : IImageFavoritesService
{
    private const string FavoritesFileName = ".favorites.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Per-folder cache. Key = normalized folder path, Value = set of favorite file names (case-insensitive).
    /// </summary>
    private readonly ConcurrentDictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-folder lock to serialize reads/writes for the same folder.
    /// </summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<IReadOnlySet<string>> GetFavoritesAsync(string folderPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(folderPath);
        var normalized = NormalizeFolderPath(folderPath);
        var set = await GetOrLoadAsync(normalized, ct).ConfigureAwait(false);
        return set;
    }

    /// <inheritdoc />
    public async Task<bool> IsFavoriteAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var folder = NormalizeFolderPath(Path.GetDirectoryName(filePath)!);
        var fileName = Path.GetFileName(filePath);
        var set = await GetOrLoadAsync(folder, ct).ConfigureAwait(false);
        return set.Contains(fileName);
    }

    /// <inheritdoc />
    public async Task<bool> ToggleFavoriteAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var folder = NormalizeFolderPath(Path.GetDirectoryName(filePath)!);
        var fileName = Path.GetFileName(filePath);
        var semaphore = GetLock(folder);

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var set = await GetOrLoadAsync(folder, ct).ConfigureAwait(false);
            bool newState;
            if (!set.Remove(fileName))
            {
                set.Add(fileName);
                newState = true;
            }
            else
            {
                newState = false;
            }

            await PersistAsync(folder, set, ct).ConfigureAwait(false);
            return newState;
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <inheritdoc />
    public async Task SetFavoriteAsync(string filePath, bool isFavorite, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var folder = NormalizeFolderPath(Path.GetDirectoryName(filePath)!);
        var fileName = Path.GetFileName(filePath);
        var semaphore = GetLock(folder);

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var set = await GetOrLoadAsync(folder, ct).ConfigureAwait(false);
            var changed = isFavorite ? set.Add(fileName) : set.Remove(fileName);
            if (changed)
            {
                await PersistAsync(folder, set, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<HashSet<string>> GetOrLoadAsync(string normalizedFolder, CancellationToken ct)
    {
        if (_cache.TryGetValue(normalizedFolder, out var cached))
        {
            return cached;
        }

        var jsonPath = Path.Combine(normalizedFolder, FavoritesFileName);
        HashSet<string> set;

        if (File.Exists(jsonPath))
        {
            try
            {
                await using var stream = File.OpenRead(jsonPath);
                var list = await JsonSerializer.DeserializeAsync<List<string>>(stream, cancellationToken: ct)
                    .ConfigureAwait(false);
                set = new HashSet<string>(list ?? [], StringComparer.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        else
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        _cache[normalizedFolder] = set;
        return set;
    }

    private static async Task PersistAsync(string normalizedFolder, HashSet<string> favorites, CancellationToken ct)
    {
        var jsonPath = Path.Combine(normalizedFolder, FavoritesFileName);
        if (favorites.Count == 0)
        {
            // Clean up the file when no favorites remain
            try
            {
                if (File.Exists(jsonPath))
                {
                    File.Delete(jsonPath);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup
            }

            return;
        }

        var sorted = favorites.OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
        await using var stream = File.Create(jsonPath);
        await JsonSerializer.SerializeAsync(stream, sorted, SerializerOptions, ct).ConfigureAwait(false);
    }

    private SemaphoreSlim GetLock(string normalizedFolder) =>
        _locks.GetOrAdd(normalizedFolder, _ => new SemaphoreSlim(1, 1));

    private static string NormalizeFolderPath(string folderPath) =>
        Path.GetFullPath(folderPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
