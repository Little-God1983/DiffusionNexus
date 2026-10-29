using System.Text.Json;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// The recipes on disk (#667): one folder per Civitai image, <c>&lt;root&gt;\&lt;imageId&gt;\</c>, holding
/// <c>original.png|jpg|webp</c> (none for a video) and <c>recipe.json</c>. The app never deletes a
/// recipe by itself; only <see cref="Delete"/>, called after the user confirmed.
/// An original downloaded for a recipe not saved yet waits in <c>&lt;root&gt;\.staging\&lt;imageId&gt;-&lt;random&gt;\</c>,
/// which is not a recipe folder: a load that ends without a save leaves no card behind.
/// </summary>
public sealed class RecipeStore
{
    private const string LogSource = "CivitaiRecipe";
    private const string JsonName = "recipe.json";
    private const string StagingName = ".staging";
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    // A stable and a dev build may share the root and run at once: only a day-old staging folder is abandoned.
    private static readonly TimeSpan StaleStagingAge = TimeSpan.FromHours(24);

    private readonly IUnifiedLogger? _logger;

    // The last parse of each folder's recipe.json; LoadAll runs after every change and must not parse them all again.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ReadStamp Stamp, StoredRecipe Stored)> _parsed =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The read error of a recipe a newer version wrote.</summary>
    public const string NewerFormatText = "Made by a newer version of DiffusionNexus";

    /// <summary>The only names an original is stored under; an in-flight temp download never matches.</summary>
    internal static readonly string[] OriginalNames = ["original.png", "original.jpg", "original.webp"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // One writer at a time: two saves of the same image share the temp file.
    private readonly object _gate = new();

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Recipes");

    public RecipeStore(string root, IUnifiedLogger? logger)
    {
        Root = root;
        _logger = logger;
        RemoveStaleStaging();
    }

    public string Root { get; }

    /// <summary>Raised after every Save, Update, SetKept and Delete.</summary>
    public event EventHandler? Changed;

    public string FolderFor(long imageId) => FolderPath(Root, imageId);

    internal static string FolderPath(string root, long imageId) =>
        Path.Combine(root, imageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A new staging folder for one download of <paramref name="imageId"/>'s original; not created yet.</summary>
    internal static string NewStagingFolder(string root, long imageId) =>
        Path.Combine(root, StagingName, $"{imageId.ToString(System.Globalization.CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}");

    /// <summary>Whether <paramref name="path"/> is an original waiting in a staging folder.</summary>
    internal static bool IsStaged(string? path) =>
        path is not null && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(path))) == StagingName;

    /// <summary>Deletes the staging folder of an original no save adopted; a path that is not staged is left alone.</summary>
    internal static void DiscardStaged(string? path)
    {
        if (!IsStaged(path)) return;
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
        catch (DirectoryNotFoundException) { /* adopted, or never written */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* the next start removes it once it is a day old */ }
    }

    private void RemoveStaleStaging()
    {
        var staging = Path.Combine(Root, StagingName);
        try
        {
            if (!Directory.Exists(staging)) return;
            foreach (var entry in new DirectoryInfo(staging).EnumerateFileSystemInfos())
            {
                if (entry.LastWriteTimeUtc > DateTime.UtcNow - StaleStagingAge) continue;
                if (entry is DirectoryInfo folder) folder.Delete(recursive: true);
                else entry.Delete();
                _logger?.Info(LogCategory.FileSystem, LogSource, $"Abandoned download {entry.Name} removed from the recipe staging folder.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"The recipe staging folder could not be cleaned: {ex.Message}", staging);
        }
    }

    /// <summary>Test seam: runs at the start of every <see cref="LoadAll"/>.</summary>
    internal Action? LoadingAll { get; set; }

    /// <summary>Every folder under the root whose name is an image id, broken ones included.</summary>
    public IReadOnlyList<StoredRecipe> LoadAll()
    {
        LoadingAll?.Invoke();
        if (!Directory.Exists(Root)) return [];
        var result = new List<StoredRecipe>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            var name = Path.GetFileName(folder);
            if (!long.TryParse(name, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)) continue;
            // "0042" is not 42's folder: FolderFor(42) is "42", and two cards would share one id.
            if (name != id.ToString(System.Globalization.CultureInfo.InvariantCulture)) continue;
            result.Add(Read(id, folder));
        }
        return result;
    }

    public StoredRecipe? TryLoad(long imageId)
    {
        var folder = FolderFor(imageId);
        return Directory.Exists(folder) ? Read(imageId, folder) : null;
    }

    /// <summary>
    /// Writes the recipe; an existing entry for the image keeps its Kept flag and is merged by <see cref="RecipeMerge"/>.
    /// A staged original (<see cref="IsStaged"/>) moves into the recipe folder first, replacing an older one; its
    /// staging folder is gone afterwards whether the save succeeded or not.
    /// </summary>
    public StoredRecipe Save(CivitaiImageRecipe recipe, DateTimeOffset pastedAt)
    {
        StoredRecipe stored;
        var staged = recipe.OriginalFilePath;
        try
        {
            lock (_gate)
            {
                var current = TryLoad(recipe.ImageId);
                ThrowIfNewerFormat(current);
                var existing = current?.Saved;
                if (existing is not null) recipe = RecipeMerge.Refresh(existing.Recipe, recipe);

                var folder = FolderFor(recipe.ImageId);
                var isNew = current is null;
                try
                {
                    if (IsStaged(staged)) recipe = recipe with { OriginalFilePath = AdoptStaged(staged!, folder) };
                    stored = Write(new SavedRecipe(SavedRecipe.CurrentFormat, pastedAt, existing?.Kept ?? false, recipe));
                }
                catch (Exception) when (isNew)
                {
                    // The folder is this save's own: left with only the original it would be a broken card.
                    try { Directory.Delete(folder, recursive: true); } catch { /* best-effort */ }
                    throw;
                }
            }
        }
        finally
        {
            DiscardStaged(staged);
        }
        _logger?.Info(LogCategory.FileSystem, LogSource, $"Recipe {recipe.ImageId} saved ({stored.SizeBytes / 1024} KB).");
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    /// <summary>
    /// Replaces the recipe's content and keeps its PastedAt and Kept (the retry of a failed resource).
    /// Writes only a recipe that still exists: a retry that ends after the user deleted it must not bring it back (null).
    /// </summary>
    public StoredRecipe? Update(CivitaiImageRecipe recipe)
    {
        StoredRecipe stored;
        lock (_gate)
        {
            var current = TryLoad(recipe.ImageId);
            ThrowIfNewerFormat(current);
            var existing = current?.Saved;
            if (existing is null) return null;
            stored = Write(new SavedRecipe(SavedRecipe.CurrentFormat, existing.PastedAt, existing.Kept, recipe));
        }
        _logger?.Info(LogCategory.FileSystem, LogSource, $"Recipe {recipe.ImageId} updated.");
        Changed?.Invoke(this, EventArgs.Empty);
        return stored;
    }

    public void SetKept(IEnumerable<long> imageIds, bool kept)
    {
        var changed = 0;
        try
        {
            lock (_gate)
            {
                foreach (var id in imageIds)
                {
                    var saved = TryLoad(id)?.Saved;
                    if (saved is null || saved.Kept == kept) continue;
                    Write(saved with { Kept = kept });
                    changed++;
                }
            }
        }
        finally
        {
            _logger?.Info(LogCategory.FileSystem, LogSource, $"{changed} recipe(s) {(kept ? "kept" : "moved back to History")}.");
            if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Deletes the folders; returns the bytes freed (0 for a folder that is not there).</summary>
    public long Delete(IEnumerable<long> imageIds) => Delete(imageIds, out _);

    /// <summary>As <see cref="Delete(IEnumerable{long})"/>; <paramref name="failures"/> says why each folder that stayed could not be deleted.</summary>
    public long Delete(IEnumerable<long> imageIds, out IReadOnlyList<string> failures)
    {
        long freed = 0;
        var count = 0;
        var failed = new List<string>();
        failures = failed;
        lock (_gate)
        {
            foreach (var id in imageIds)
            {
                var folder = FolderFor(id);
                if (!Directory.Exists(folder)) continue;
                var size = SizeOf(folder);
                try
                {
                    Directory.Delete(folder, recursive: true);
                    _parsed.TryRemove(folder, out _);
                    freed += size;
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {id} could not be deleted: {ex.Message}", folder);
                    failed.Add(ex.Message);
                }
            }
        }
        _logger?.Info(LogCategory.FileSystem, LogSource, $"{count} recipe(s) deleted, {freed / 1024} KB freed.");
        Changed?.Invoke(this, EventArgs.Empty);
        return freed;
    }

    /// <summary>Moves a staged original into <paramref name="folder"/> and removes the other originals; returns the folder's original.</summary>
    private string? AdoptStaged(string staged, string folder)
    {
        var target = Path.Combine(folder, Path.GetFileName(staged));
        try
        {
            Directory.CreateDirectory(folder);
            File.Move(staged, target, overwrite: true);
            foreach (var other in OriginalNames.Select(n => Path.Combine(folder, n)).Where(p => p != target && File.Exists(p)))
            {
                File.Delete(other);
            }
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // An older original being read (a thumbnail) cannot be replaced; the recipe keeps that one.
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {Path.GetFileName(folder)}: the new original could not replace the old one: {ex.Message}", folder);
            return OriginalNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
        }
    }

    private StoredRecipe Write(SavedRecipe saved)
    {
        var folder = FolderFor(saved.Recipe.ImageId);
        var path = Path.Combine(folder, JsonName);
        var temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temp); } catch { /* best-effort */ }
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {saved.Recipe.ImageId} could not be written: {ex.Message}", path);
            throw;
        }
        // Two writes inside one file-time tick can leave the same write time and length (a re-paste that only
        // moves PastedAt): the stamp cannot tell them apart, so this store's own write always reads afresh.
        _parsed.TryRemove(folder, out _);
        return Read(saved.Recipe.ImageId, folder);
    }

    private StoredRecipe Read(long imageId, string folder)
    {
        var path = Path.Combine(folder, JsonName);
        DeleteStaleTemp(path + ".tmp");
        var files = FilesOf(folder);
        var size = files.Where(f => f.Extension != ".download").Sum(f => f.Length);
        var json = files.FirstOrDefault(f => string.Equals(f.Name, JsonName, StringComparison.OrdinalIgnoreCase));
        if (json is null) return new(imageId, folder, null, size, $"{JsonName} is missing.");

        // The same recipe.json and original as last time: the parsed recipe is still right (they are 250–300 KB).
        var originalName = OriginalNames.FirstOrDefault(n => files.Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase)));
        var originalLength = originalName is null ? 0 : files.First(f => string.Equals(f.Name, originalName, StringComparison.OrdinalIgnoreCase)).Length;
        var stamp = new ReadStamp(json.LastWriteTimeUtc, json.Length, originalName, originalLength, size);
        if (_parsed.TryGetValue(folder, out var hit) && hit.Stamp == stamp) return hit.Stored;

        try
        {
            var saved = JsonSerializer.Deserialize<SavedRecipe>(File.ReadAllText(path), JsonOptions);
            if (saved?.FormatVersion > SavedRecipe.CurrentFormat) return new(imageId, folder, null, size, NewerFormatText, IsNewerFormat: true);
            // A missing member binds to null; the cards read all four without asking.
            if (saved?.Recipe is not { Image: not null, Data: not null, Resources: not null, UnlinkedLoras: not null })
            {
                return new(imageId, folder, null, size, $"{JsonName} holds no recipe.");
            }
            // The folder is the truth: a moved profile must still find its original.
            var original = originalName is null ? null : Path.Combine(folder, originalName);
            var stored = new StoredRecipe(imageId, folder, saved with { Recipe = saved.Recipe with { OriginalFilePath = original } }, size, null);
            _parsed[folder] = (stamp, stored);
            return stored;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {imageId} could not be read: {ex.Message}", path);
            return new(imageId, folder, null, size, ex.Message);
        }
    }

    private static FileInfo[] FilesOf(string folder)
    {
        try { return new DirectoryInfo(folder).GetFiles(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    /// <summary>What a parsed recipe was read from; any change means it is read again.</summary>
    private readonly record struct ReadStamp(DateTime JsonWrittenUtc, long JsonLength, string? Original, long OriginalLength, long FolderSize);

    private static long SizeOf(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Extension != ".download").Sum(f => f.Length); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
    }

    /// <summary>A temp file a crashed save left behind; a young one may be a save writing right now.</summary>
    private static void DeleteStaleTemp(string temp)
    {
        try
        {
            if (File.Exists(temp) && File.GetLastWriteTimeUtc(temp) < DateTime.UtcNow - StaleTempAge) File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* next read tries again */ }
    }

    /// <summary>Refuses to write over a file a newer version wrote: this version would drop what it does not know.</summary>
    private static void ThrowIfNewerFormat(StoredRecipe? current)
    {
        if (current is { IsNewerFormat: true })
        {
            throw new InvalidOperationException(
                $"Recipe {current.ImageId} was saved by a newer version of DiffusionNexus; this version leaves it as it is.");
        }
    }
}
