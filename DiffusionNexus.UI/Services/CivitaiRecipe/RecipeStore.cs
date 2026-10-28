using System.Text.Json;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// The recipes on disk (#667): one folder per Civitai image, <c>&lt;root&gt;\&lt;imageId&gt;\</c>, holding
/// <c>original.png|jpg|webp</c> (none for a video) and <c>recipe.json</c>. The app never deletes a
/// recipe by itself; only <see cref="Delete"/>, called after the user confirmed.
/// </summary>
public sealed class RecipeStore(string root, IUnifiedLogger? logger)
{
    private const string LogSource = "CivitaiRecipe";
    private const string JsonName = "recipe.json";

    /// <summary>The only names an original is stored under; an in-flight temp download never matches.</summary>
    internal static readonly string[] OriginalNames = ["original.png", "original.jpg", "original.webp"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // One writer at a time: two saves of the same image share the temp file.
    private readonly object _gate = new();

    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Recipes");

    public string Root { get; } = root;

    /// <summary>Raised after every Save, Update, SetKept and Delete.</summary>
    public event EventHandler? Changed;

    public string FolderFor(long imageId) => FolderPath(Root, imageId);

    internal static string FolderPath(string root, long imageId) =>
        Path.Combine(root, imageId.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Every folder under the root whose name is an image id, broken ones included.</summary>
    public IReadOnlyList<StoredRecipe> LoadAll()
    {
        if (!Directory.Exists(Root)) return [];
        var result = new List<StoredRecipe>();
        foreach (var folder in Directory.EnumerateDirectories(Root))
        {
            if (!long.TryParse(Path.GetFileName(folder), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)) continue;
            result.Add(Read(id, folder));
        }
        return result;
    }

    public StoredRecipe? TryLoad(long imageId)
    {
        var folder = FolderFor(imageId);
        return Directory.Exists(folder) ? Read(imageId, folder) : null;
    }

    /// <summary>Writes the recipe; an existing entry for the image keeps its Kept flag and is merged by <see cref="RecipeMerge"/>.</summary>
    public StoredRecipe Save(CivitaiImageRecipe recipe, DateTimeOffset pastedAt)
    {
        StoredRecipe stored;
        lock (_gate)
        {
            var existing = TryLoad(recipe.ImageId)?.Saved;
            if (existing is not null) recipe = RecipeMerge.Refresh(existing.Recipe, recipe);
            stored = Write(new SavedRecipe(SavedRecipe.CurrentFormat, pastedAt, existing?.Kept ?? false, recipe));
        }
        logger?.Info(LogCategory.FileSystem, LogSource, $"Recipe {recipe.ImageId} saved ({stored.SizeBytes / 1024} KB).");
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
            var existing = TryLoad(recipe.ImageId)?.Saved;
            if (existing is null) return null;
            stored = Write(new SavedRecipe(SavedRecipe.CurrentFormat, existing.PastedAt, existing.Kept, recipe));
        }
        logger?.Info(LogCategory.FileSystem, LogSource, $"Recipe {recipe.ImageId} updated.");
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
            logger?.Info(LogCategory.FileSystem, LogSource, $"{changed} recipe(s) {(kept ? "kept" : "moved back to History")}.");
            if (changed > 0) Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Deletes the folders; returns the bytes freed (0 for a folder that is not there).</summary>
    public long Delete(IEnumerable<long> imageIds)
    {
        long freed = 0;
        var count = 0;
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
                    freed += size;
                    count++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {id} could not be deleted: {ex.Message}", folder);
                }
            }
        }
        logger?.Info(LogCategory.FileSystem, LogSource, $"{count} recipe(s) deleted, {freed / 1024} KB freed.");
        Changed?.Invoke(this, EventArgs.Empty);
        return freed;
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
            logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {saved.Recipe.ImageId} could not be written: {ex.Message}", path);
            throw;
        }
        return Read(saved.Recipe.ImageId, folder);
    }

    private StoredRecipe Read(long imageId, string folder)
    {
        var size = SizeOf(folder);
        var path = Path.Combine(folder, JsonName);
        if (!File.Exists(path)) return new(imageId, folder, null, size, $"{JsonName} is missing.");
        try
        {
            var saved = JsonSerializer.Deserialize<SavedRecipe>(File.ReadAllText(path), JsonOptions);
            if (saved?.Recipe is null) return new(imageId, folder, null, size, $"{JsonName} holds no recipe.");
            // The folder is the truth: a moved profile must still find its original.
            var original = OriginalNames.Select(n => Path.Combine(folder, n)).FirstOrDefault(File.Exists);
            return new(imageId, folder, saved with { Recipe = saved.Recipe with { OriginalFilePath = original } }, size, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger?.Warn(LogCategory.FileSystem, LogSource, $"Recipe {imageId} could not be read: {ex.Message}", path);
            return new(imageId, folder, null, size, ex.Message);
        }
    }

    private static long SizeOf(string folder)
    {
        try { return new DirectoryInfo(folder).EnumerateFiles().Where(f => f.Extension != ".download").Sum(f => f.Length); }
        catch (IOException) { return 0; }
    }
}
