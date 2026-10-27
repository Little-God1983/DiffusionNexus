using System.Diagnostics;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Domain.Services.UnifiedLogging;

namespace DiffusionNexus.UI.Services.CivitaiRecipe;

/// <summary>
/// Loads the recipe of one Civitai image (#628):
/// <list type="number">
/// <item>the image from <c>/images?imageId=N&amp;nsfw=X</c> — its rating, size, creator and the version
/// ids of the resources it was made with (without <c>nsfw=X</c> an NSFW image comes back as nothing);</item>
/// <item>each resource's version and model, through the API gateway, one failure per row;</item>
/// <item>the generation data from the original file, which is the only source — the public API's
/// <c>meta</c> was null for every image sampled. The file is cached by image id and also serves
/// as the view's preview.</item>
/// </list>
/// </summary>
public sealed class CivitaiImageRecipeService
{
    private const string LogSource = "CivitaiRecipe";

    internal const string NotAnImageText = "The file Civitai served for this image is not a PNG, JPEG or WebP image.";

    /// <summary>Only files the recipe can read are cached, so a cache hit is always one of these.</summary>
    private static readonly string[] CachedExtensions = [".png", ".jpg", ".webp"];

    private readonly ICivitaiClient _client;
    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly IUnifiedLogger? _logger;

    public CivitaiImageRecipeService(ICivitaiClient client, HttpClient http, string cacheDirectory, IUnifiedLogger? logger)
    {
        _client = client;
        _http = http;
        _cacheDirectory = cacheDirectory;
        _logger = logger;
    }

    public static string DefaultCacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DiffusionNexus", "Cache", "civitai-recipes");

    public async Task<CivitaiImageRecipeLoadResult> LoadAsync(long imageId, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var page = await _client.GetImagesAsync(new CivitaiImagesQuery { ImageId = imageId, NsfwLevel = "X" }, apiKey, ct);
        var image = page?.Items.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
        {
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe: GET images?imageId={imageId} → not found ({sw.ElapsedMilliseconds} ms)");
            return new(null, $"Civitai has no public image with ID {imageId}.");
        }

        var rating = image.BrowsingLevel ?? image.NsfwLevel;
        var versionIds = image.ModelVersionIds ?? [];
        _logger?.Info(LogCategory.Network, LogSource,
            $"Recipe: GET images?imageId={imageId} → {image.Type ?? "image"} by {image.Username ?? "unknown"}, rating {rating?.ToString() ?? "none"}, " +
            $"{versionIds.Count} resource(s) ({sw.ElapsedMilliseconds} ms)");

        var resourcesTask = Task.WhenAll(versionIds.Select(id => LoadResourceAsync(id, apiKey, ct)));
        var originalTask = GetOriginalAsync(image, imageId, ct);
        await Task.WhenAll(resourcesTask, originalTask);
        var resources = resourcesTask.Result;
        var (originalPath, data) = originalTask.Result;

        var versions = resources.Where(r => r.Version is not null).Select(r => r.Version!).ToList();
        var match = RecipeResourceMatcher.Match(versions, data.Loras);
        var withUses = resources
            .Select(r => match.ByVersionId.TryGetValue(r.VersionId, out var use) ? r with { Use = use } : r)
            .ToList();

        _logger?.Info(LogCategory.Network, LogSource,
            $"Recipe {imageId}: {data.Source} data, {data.Loras.Count} LoRA(s) in the file, {match.ByVersionId.Count} matched to a resource, " +
            $"{match.Unlinked.Count} not linked on Civitai, {withUses.Count(r => r.LoadError is not null)} resource(s) not loaded ({sw.ElapsedMilliseconds} ms total)",
            data.ReadError);

        return new(new CivitaiImageRecipe(imageId, image, rating, originalPath, data, withUses, match.Unlinked), null);
    }

    private async Task<CivitaiRecipeResource> LoadResourceAsync(int versionId, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var version = await _client.GetModelVersionAsync(versionId, apiKey, ct);
            if (version is null)
            {
                _logger?.Info(LogCategory.Network, LogSource, $"Recipe: GET model-versions/{versionId} → not found ({sw.ElapsedMilliseconds} ms)");
                return new(versionId, null, null, null, "not found on Civitai");
            }

            var model = await _client.GetModelAsync(version.ModelId, apiKey, ct);
            if (model is null || model.Mode is not null)
            {
                _logger?.Info(LogCategory.Network, LogSource,
                    $"Recipe: version {versionId} → model {version.ModelId} not available (mode {model?.Mode?.ToString() ?? "missing"}, {sw.ElapsedMilliseconds} ms)");
                return new(versionId, version, null, null, "model no longer available on Civitai");
            }

            _logger?.Info(LogCategory.Network, LogSource,
                $"Recipe: version {versionId} → \"{model.Name}\" ({model.Type}, {version.BaseModel}) ({sw.ElapsedMilliseconds} ms)");
            var listed = model.ModelVersions.FirstOrDefault(v => v.Id == versionId) ?? version;
            return new(versionId, listed, model, null, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.Warn(LogCategory.Network, LogSource, $"Recipe: version {versionId} could not be loaded: {ex.Message}", ex.ToString());
            return new(versionId, null, null, null, ex.Message);
        }
    }

    private async Task<(string? Path, ImageRecipeData Data)> GetOriginalAsync(CivitaiModelImage image, long imageId, CancellationToken ct)
    {
        if (string.Equals(image.Type, "video", StringComparison.OrdinalIgnoreCase))
        {
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe {imageId}: video post — the file is not read for generation data.");
            return (null, ImageRecipeData.Empty(RecipeFileFormat.Video));
        }

        Directory.CreateDirectory(_cacheDirectory);
        var cached = CachedExtensions
            .Select(ext => Path.Combine(_cacheDirectory, imageId + ext))
            .FirstOrDefault(f => File.Exists(f) && new FileInfo(f).Length > 0);
        if (cached is not null)
        {
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe {imageId}: original file from the cache ({Path.GetFileName(cached)}).");
            return (cached, ImageRecipeReader.Read(cached));
        }

        // A name of its own: paste + Enter starts two loads of the same image at once.
        var temp = Path.Combine(_cacheDirectory, $"{imageId}.{Guid.NewGuid():N}.download");
        var sw = Stopwatch.StartNew();
        try
        {
            using (var response = await _http.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(temp);
                await response.Content.CopyToAsync(file, ct);
            }

            var format = ReadFormat(temp);
            var extension = format switch
            {
                RecipeFileFormat.Png => ".png",
                RecipeFileFormat.Jpeg => ".jpg",
                RecipeFileFormat.WebP => ".webp",
                _ => null,
            };
            if (extension is null)
            {
                // An HTML challenge page or an image type nothing here reads: cached, it would answer every
                // later paste of this image with the same nothing.
                TryDelete(temp);
                _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {imageId}: the original file is {format}, not an image the recipe can read — not cached.", image.Url);
                return (null, ImageRecipeData.Empty(RecipeFileFormat.Unknown, NotAnImageText));
            }

            var path = Path.Combine(_cacheDirectory, imageId + extension);
            try
            {
                // No overwrite: replacing a file another load is reading fails with "access denied".
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another load of the same image got there first; theirs is the same file.
                TryDelete(temp);
            }
            _logger?.Info(LogCategory.Network, LogSource,
                $"Recipe {imageId}: original file downloaded — {format}, {new FileInfo(path).Length / 1024} KB ({sw.ElapsedMilliseconds} ms)");
            return (path, ImageRecipeReader.Read(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TryDelete(temp);
            _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {imageId}: original file download failed: {ex.Message}", image.Url);
            return (null, ImageRecipeData.Empty(RecipeFileFormat.Unknown, $"Could not download the original image file ({ex.Message})."));
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static RecipeFileFormat ReadFormat(string path)
    {
        Span<byte> head = stackalloc byte[16];
        using var stream = File.OpenRead(path);
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return RecipeFileReader.DetectFormat(head[..read]);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }
}
