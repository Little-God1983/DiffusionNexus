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
/// <c>meta</c> was null for every image sampled. A download waits in the store's staging folder until
/// <see cref="RecipeStore.Save"/> adopts it; a saved recipe's original serves later pastes and the preview.</item>
/// </list>
/// </summary>
public sealed class CivitaiImageRecipeService
{
    private const string LogSource = "CivitaiRecipe";

    internal const string NotFoundText = "not found on Civitai";
    internal const string NoLongerAvailableText = "model no longer available on Civitai";
    internal const string NotAnImageText = "The file Civitai served for this image is not a PNG, JPEG or WebP image.";

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

    public async Task<CivitaiImageRecipeLoadResult> LoadAsync(long imageId, string? apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
        // Asked alongside the public call: it needs only the image id.
        var siteTask = hasApiKey ? GetSiteDataAsync(imageId, apiKey!, ct) : Task.FromResult<CivitaiSiteData?>(null);

        var page = await _client.GetImagesAsync(new CivitaiImagesQuery { ImageId = imageId, NsfwLevel = "X" }, apiKey, ct);
        var image = page?.Items.FirstOrDefault(i => i.Id == imageId);
        if (image is null)
        {
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe: GET images?imageId={imageId} → not found ({sw.ElapsedMilliseconds} ms)");
            return new(null, $"Civitai has no public image with ID {imageId}.");
        }

        var rating = image.BrowsingLevel ?? image.NsfwLevel;
        var publicIds = image.ModelVersionIds ?? [];
        _logger?.Info(LogCategory.Network, LogSource,
            $"Recipe: GET images?imageId={imageId} → {image.Type ?? "image"} by {image.Username ?? "unknown"}, rating {rating?.ToString() ?? "none"}, " +
            $"{publicIds.Count} resource(s) ({sw.ElapsedMilliseconds} ms)");

        var site = await siteTask;
        // The public list first; the page's list adds what the public API left out (image 146878246: all six).
        var versionIds = publicIds.Concat(site?.Resources.Select(r => r.VersionId) ?? []).Distinct().ToList();

        var resourcesTask = Task.WhenAll(versionIds.Select(id => LoadResourceAsync(id, apiKey, ct)));
        var originalTask = GetOriginalAsync(image, imageId, ct);
        try
        {
            await Task.WhenAll(resourcesTask, originalTask);
        }
        catch
        {
            // The original may have landed before the load was cancelled; no save will adopt it.
            if (originalTask.IsCompletedSuccessfully) RecipeStore.DiscardStaged(originalTask.Result.Path);
            throw;
        }
        var resources = resourcesTask.Result;
        var (originalPath, data) = originalTask.Result;

        var versions = resources.Where(r => r.Version is not null).Select(r => r.Version!).ToList();
        var match = RecipeResourceMatcher.Match(versions, data.Loras);
        var withUses = resources
            .Select(r => match.ByVersionId.TryGetValue(r.VersionId, out var use) ? r with { Use = use } : r)
            .Select(r => WithSiteWeight(r, site))
            .ToList();

        // The file wins; Civitai's data only stands in when the file carries none.
        if (data.Source == RecipeDataSource.None && site?.Meta is { } meta)
        {
            data = meta with { Format = data.Format, Loras = data.Loras, SavedBy = data.SavedBy };
        }

        _logger?.Info(LogCategory.Network, LogSource,
            $"Recipe {imageId}: {data.Source} data, {data.Loras.Count} LoRA(s) in the file, {match.ByVersionId.Count} matched to a resource, " +
            $"{match.Unlinked.Count} not linked on Civitai, {withUses.Count(r => r.LoadError is not null)} resource(s) not loaded, " +
            $"{withUses.Count(r => r.Use?.Weight is not null)} weight(s) known ({sw.ElapsedMilliseconds} ms total)",
            data.ReadError);

        return new(new CivitaiImageRecipe(imageId, image, rating, originalPath, data, withUses, match.Unlinked, hasApiKey), null);
    }

    /// <summary>
    /// Loads again the resources of a saved recipe that failed for a reason that may pass (429, timeout).
    /// "Not found" answers are final and are not asked twice. Returns <paramref name="recipe"/> itself when
    /// nothing needed a retry.
    /// </summary>
    public async Task<CivitaiImageRecipe> RetryFailedResourcesAsync(CivitaiImageRecipe recipe, string? apiKey, CancellationToken ct)
    {
        var failed = recipe.Resources.Where(r => r.LoadError is not null && !r.IsDefiniteFailure).Select(r => r.VersionId).ToList();
        if (failed.Count == 0) return recipe;

        var reloaded = (await Task.WhenAll(failed.Select(id => LoadResourceAsync(id, apiKey, ct)))).ToDictionary(r => r.VersionId);

        // Only the entries no resource holds yet can be claimed by a newly loaded version.
        var taken = recipe.Resources.Where(r => r.Use is not null && !reloaded.ContainsKey(r.VersionId)).Select(r => r.Use!).ToList();
        var free = recipe.Data.Loras.Where(u => !taken.Contains(u)).ToList();
        var loaded = reloaded.Values.Where(r => r.Version is not null).Select(r => r.Version!).ToList();
        var match = RecipeResourceMatcher.Match(loaded, free);

        var resources = recipe.Resources.Select(old =>
        {
            if (!reloaded.TryGetValue(old.VersionId, out var now)) return old;
            if (now.LoadError is not null) return old with { LoadError = now.LoadError, Version = now.Version ?? old.Version };
            if (!match.ByVersionId.TryGetValue(old.VersionId, out var use)) return now with { Use = old.Use };
            // A weight Civitai's page gave on the first paste stays when the file entry carries none.
            return now with { Use = use.Weight is null && old.Use?.Weight is { } weight ? use with { Weight = weight } : use };
        }).ToList();
        var claimed = match.ByVersionId.Values.ToList();
        var unlinked = recipe.UnlinkedLoras.Where(u => !claimed.Contains(u)).ToList();

        _logger?.Info(LogCategory.Network, LogSource,
            $"Recipe {recipe.ImageId}: retried {failed.Count} resource(s), {resources.Count(r => failed.Contains(r.VersionId) && r.LoadError is null)} loaded now.");
        return recipe with { Resources = resources, UnlinkedLoras = unlinked };
    }

    /// <summary>A weight Civitai's page knows, for a resource the file gave none.</summary>
    private static CivitaiRecipeResource WithSiteWeight(CivitaiRecipeResource resource, CivitaiSiteData? site)
    {
        if (resource.Use?.Weight is not null) return resource;
        var strength = site?.Resources.FirstOrDefault(s => s.VersionId == resource.VersionId)?.Strength;
        if (strength is null) return resource;
        var use = resource.Use ?? new RecipeLoraUse(resource.Model?.Name ?? $"Version {resource.VersionId}", null, null, null, resource.VersionId);
        return resource with { Use = use with { Weight = strength } };
    }

    /// <summary>Never fails the recipe: the endpoint is Civitai's website's, not a public API.</summary>
    private async Task<CivitaiSiteData?> GetSiteDataAsync(long imageId, string apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var site = await CivitaiSiteDataReader.GetAsync(_http, imageId, apiKey, ct);
            _logger?.Info(LogCategory.Network, LogSource, site is null
                ? $"Recipe {imageId}: Civitai's site data unavailable (refused or changed shape) ({sw.ElapsedMilliseconds} ms)"
                : $"Recipe {imageId}: Civitai's site data — {site.Resources.Count} resource(s), {site.Resources.Count(r => r.Strength is not null)} weight(s), " +
                  $"generation data {(site.Meta is null ? "none" : "yes")} ({sw.ElapsedMilliseconds} ms)");
            return site;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {imageId}: Civitai's site data failed: {ex.Message}");
            return null;
        }
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
                return new(versionId, null, null, null, NotFoundText);
            }

            var model = await _client.GetModelAsync(version.ModelId, apiKey, ct);
            if (model is null || model.Mode is not null)
            {
                _logger?.Info(LogCategory.Network, LogSource,
                    $"Recipe: version {versionId} → model {version.ModelId} not available (mode {model?.Mode?.ToString() ?? "missing"}, {sw.ElapsedMilliseconds} ms)");
                return new(versionId, version, null, null, NoLongerAvailableText);
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

        var folder = RecipeStore.FolderPath(_cacheDirectory, imageId);
        var cached = RecipeStore.OriginalNames
            .Select(name => Path.Combine(folder, name))
            .FirstOrDefault(f => File.Exists(f) && new FileInfo(f).Length > 0);
        if (cached is not null)
        {
            _logger?.Info(LogCategory.Network, LogSource, $"Recipe {imageId}: original file from the cache ({Path.GetFileName(cached)}).");
            return (cached, ImageRecipeReader.Read(cached));
        }

        // Into a staging folder of its own, not the recipe folder: paste + Enter starts two loads of the same
        // image at once, and a load that ends without a save (cancelled, superseded, failed) must leave no
        // recipe folder behind. RecipeStore.Save moves the file into the recipe folder.
        var staging = RecipeStore.NewStagingFolder(_cacheDirectory, imageId);
        var temp = Path.Combine(staging, "original.download");
        var sw = Stopwatch.StartNew();
        try
        {
            using (var response = await _http.GetAsync(image.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                Directory.CreateDirectory(staging);
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
                TryDeleteFolder(staging);
                _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {imageId}: the original file is {format}, not an image the recipe can read — not cached.", image.Url);
                return (null, ImageRecipeData.Empty(RecipeFileFormat.Unknown, NotAnImageText));
            }

            var path = Path.Combine(staging, "original" + extension);
            File.Move(temp, path);
            _logger?.Info(LogCategory.Network, LogSource,
                $"Recipe {imageId}: original file downloaded — {format}, {new FileInfo(path).Length / 1024} KB ({sw.ElapsedMilliseconds} ms)");
            return (path, ImageRecipeReader.Read(path));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            TryDeleteFolder(staging);
            _logger?.Warn(LogCategory.Network, LogSource, $"Recipe {imageId}: original file download failed: {ex.Message}", image.Url);
            return (null, ImageRecipeData.Empty(RecipeFileFormat.Unknown, $"Could not download the original image file ({ex.Message})."));
        }
        catch
        {
            TryDeleteFolder(staging);
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

    private static void TryDeleteFolder(string folder)
    {
        try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); } catch { /* best-effort; the store removes it once it is a day old */ }
    }
}
