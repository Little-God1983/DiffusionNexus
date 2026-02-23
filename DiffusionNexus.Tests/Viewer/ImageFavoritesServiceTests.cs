using DiffusionNexus.UI.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Unit tests for <see cref="ImageFavoritesService"/>.
/// </summary>
public class ImageFavoritesServiceTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    [Fact]
    public async Task WhenNoFavoritesFileExists_ThenReturnsEmptySet()
    {
        var folder = CreateTempDirectory();
        var service = new ImageFavoritesService();

        var result = await service.GetFavoritesAsync(folder);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenToggleFavorite_ThenFileBecomeFavorite()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");
        var service = new ImageFavoritesService();

        var result = await service.ToggleFavoriteAsync(filePath);

        result.Should().BeTrue();
        (await service.IsFavoriteAsync(filePath)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenToggleFavoriteTwice_ThenFileIsNoLongerFavorite()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");
        var service = new ImageFavoritesService();

        await service.ToggleFavoriteAsync(filePath);
        var result = await service.ToggleFavoriteAsync(filePath);

        result.Should().BeFalse();
        (await service.IsFavoriteAsync(filePath)).Should().BeFalse();
    }

    [Fact]
    public async Task WhenFavoriteIsToggled_ThenJsonFileIsCreatedInFolder()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");
        var service = new ImageFavoritesService();

        await service.ToggleFavoriteAsync(filePath);

        var jsonPath = Path.Combine(folder, ".favorites.json");
        File.Exists(jsonPath).Should().BeTrue();
    }

    [Fact]
    public async Task WhenAllFavoritesRemoved_ThenJsonFileIsDeleted()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");
        var service = new ImageFavoritesService();

        await service.ToggleFavoriteAsync(filePath);
        await service.ToggleFavoriteAsync(filePath);

        var jsonPath = Path.Combine(folder, ".favorites.json");
        File.Exists(jsonPath).Should().BeFalse();
    }

    [Fact]
    public async Task WhenSetFavoriteExplicitly_ThenStateIsSetCorrectly()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");
        var service = new ImageFavoritesService();

        await service.SetFavoriteAsync(filePath, true);
        (await service.IsFavoriteAsync(filePath)).Should().BeTrue();

        await service.SetFavoriteAsync(filePath, false);
        (await service.IsFavoriteAsync(filePath)).Should().BeFalse();
    }

    [Fact]
    public async Task WhenMultipleFilesInSameFolder_ThenAllTrackedInOneJsonFile()
    {
        var folder = CreateTempDirectory();
        var file1 = Path.Combine(folder, "img1.png");
        var file2 = Path.Combine(folder, "img2.png");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        var service = new ImageFavoritesService();

        await service.ToggleFavoriteAsync(file1);
        await service.ToggleFavoriteAsync(file2);

        var favorites = await service.GetFavoritesAsync(folder);
        favorites.Should().HaveCount(2);
        favorites.Should().Contain("img1.png");
        favorites.Should().Contain("img2.png");
    }

    [Fact]
    public async Task WhenNewServiceInstanceCreated_ThenFavoritesPersistedFromDisk()
    {
        var folder = CreateTempDirectory();
        var filePath = Path.Combine(folder, "image.png");
        File.WriteAllText(filePath, "test");

        var service1 = new ImageFavoritesService();
        await service1.ToggleFavoriteAsync(filePath);

        var service2 = new ImageFavoritesService();
        (await service2.IsFavoriteAsync(filePath)).Should().BeTrue();
    }

    [Fact]
    public async Task WhenFilesInDifferentFolders_ThenEachFolderHasOwnJsonFile()
    {
        var folder1 = CreateTempDirectory();
        var folder2 = CreateTempDirectory();
        var file1 = Path.Combine(folder1, "img.png");
        var file2 = Path.Combine(folder2, "img.png");
        File.WriteAllText(file1, "test");
        File.WriteAllText(file2, "test");
        var service = new ImageFavoritesService();

        await service.ToggleFavoriteAsync(file1);

        (await service.IsFavoriteAsync(file1)).Should().BeTrue();
        (await service.IsFavoriteAsync(file2)).Should().BeFalse();

        File.Exists(Path.Combine(folder1, ".favorites.json")).Should().BeTrue();
        File.Exists(Path.Combine(folder2, ".favorites.json")).Should().BeFalse();
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            if (Directory.Exists(path))
            {
                try
                {
                    Directory.Delete(path, true);
                }
                catch
                {
                    // Best-effort cleanup
                }
            }
        }
    }

    private string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiffusionNexusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempPaths.Add(root);
        return root;
    }
}
