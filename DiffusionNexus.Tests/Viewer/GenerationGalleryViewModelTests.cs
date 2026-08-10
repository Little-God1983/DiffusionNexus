using System.Globalization;
using DiffusionNexus.DataAccess.Data;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Infrastructure.Services;
using DiffusionNexus.Service.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Unit tests for <see cref="GenerationGalleryViewModel"/>.
/// </summary>
public class GenerationGalleryViewModelTests : IDisposable
{
    private readonly List<string> _tempPaths = [];

    [Fact]
    public async Task LoadMediaAsync_NoEnabledSources_ShowsConfigurationMessage()
    {
        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = "C:\\Missing", IsEnabled = false }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.HasNoMedia.Should().BeTrue();
        viewModel.MediaItems.Should().BeEmpty();
        viewModel.NoMediaMessage.Should().Contain("Settings");
    }

    [Fact]
    public async Task LoadMediaAsync_LoadsMediaFromEnabledSourcesOnly()
    {
        var enabledPath = CreateTempDirectory();
        var disabledPath = CreateTempDirectory();

        var enabledImage = Path.Combine(enabledPath, "alpha.png");
        var enabledVideo = Path.Combine(enabledPath, "beta.mp4");
        var disabledImage = Path.Combine(disabledPath, "ignored.jpg");

        File.WriteAllText(enabledImage, "test");
        File.WriteAllText(enabledVideo, "test");
        File.WriteAllText(disabledImage, "test");

        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = enabledPath, IsEnabled = true, Order = 0 },
                new() { FolderPath = disabledPath, IsEnabled = false, Order = 1 }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(enabledImage);
        viewModel.MediaItems.Select(item => item.FilePath).Should().Contain(enabledVideo);
        viewModel.MediaItems.Select(item => item.FilePath).Should().NotContain(disabledImage);
    }

    [Fact]
    public async Task SelectedSortOption_SortsByNameAndCreationDate()
    {
        var galleryPath = CreateTempDirectory();
        var olderFile = Path.Combine(galleryPath, "alpha.png");
        var newerFile = Path.Combine(galleryPath, "beta.png");

        File.WriteAllText(olderFile, "test");
        File.WriteAllText(newerFile, "test");

        var oldTime = DateTime.UtcNow.AddDays(-2);
        var newTime = DateTime.UtcNow.AddDays(-1);
        File.SetCreationTimeUtc(olderFile, oldTime);
        File.SetCreationTimeUtc(newerFile, newTime);

        var settings = new AppSettings
        {
            ImageGalleries = new List<ImageGallery>
            {
                new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }
            }
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectedSortOption = "Name";
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.First().FilePath.Should().Be(olderFile);

        viewModel.SelectedSortOption = "Creation date";
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.First().FilePath.Should().Be(newerFile);
    }

    [Fact]
    public void OpenFolderInExplorerCommand_WhenNoSelection_CannotExecute()
    {
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "test.png");
        File.WriteAllText(image, "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);

        viewModel.OpenFolderInExplorerCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_WhenImageSelected_CanExecute()
    {
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "test.png");
        File.WriteAllText(image, "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);

        viewModel.OpenFolderInExplorerCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_MoreThan3Origins_ShowsConfirmDialog()
    {
        // Create 4 distinct folders to trigger the warning
        var folders = Enumerable.Range(0, 4).Select(_ => CreateTempDirectory()).ToList();
        foreach (var folder in folders)
        {
            File.WriteAllText(Path.Combine(folder, "img.png"), "test");
        }

        var settings = new AppSettings
        {
            ImageGalleries = folders.Select((f, i) => new ImageGallery
            {
                FolderPath = f, IsEnabled = true, Order = i
            }).ToList()
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);
        viewModel.DialogService = mockDialog.Object;
        viewModel.ProcessLauncher = new Mock<IProcessLauncher>().Object;

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        // Select all items (one per folder = 4 distinct origins)
        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);
        for (var i = 1; i < viewModel.MediaItems.Count; i++)
        {
            viewModel.SelectWithModifiers(viewModel.MediaItems[i], false, true);
        }

        await viewModel.OpenFolderInExplorerCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowConfirmAsync(
            "Open Multiple Folders",
            It.Is<string>(msg => msg.Contains("4"))), Times.Once);
    }

    [Fact]
    public async Task OpenFolderInExplorerCommand_3OrFewerOrigins_NoConfirmDialog()
    {
        // Create 2 distinct folders — should not trigger the warning
        var folders = Enumerable.Range(0, 2).Select(_ => CreateTempDirectory()).ToList();
        foreach (var folder in folders)
        {
            File.WriteAllText(Path.Combine(folder, "img.png"), "test");
        }

        var settings = new AppSettings
        {
            ImageGalleries = folders.Select((f, i) => new ImageGallery
            {
                FolderPath = f, IsEnabled = true, Order = i
            }).ToList()
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockDialog = new Mock<IDialogService>();

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);
        viewModel.DialogService = mockDialog.Object;
        viewModel.ProcessLauncher = new Mock<IProcessLauncher>().Object;

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectWithModifiers(viewModel.MediaItems[0], false, false);
        for (var i = 1; i < viewModel.MediaItems.Count; i++)
        {
            viewModel.SelectWithModifiers(viewModel.MediaItems[i], false, true);
        }

        await viewModel.OpenFolderInExplorerCommand.ExecuteAsync(null);

        mockDialog.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WhenShowFavoritesOnlyEnabled_ThenOnlyFavoriteItemsShown()
    {
        var galleryPath = CreateTempDirectory();
        var favImage = Path.Combine(galleryPath, "fav.png");
        var normalImage = Path.Combine(galleryPath, "normal.png");
        File.WriteAllText(favImage, "test");
        File.WriteAllText(normalImage, "test");

        var favoritesService = new ImageFavoritesService();
        await favoritesService.SetFavoriteAsync(favImage, true);

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().HaveCount(2);

        viewModel.ShowFavoritesOnly = true;
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().ContainSingle();
        viewModel.MediaItems[0].FilePath.Should().Be(favImage);
    }

    [Fact]
    public async Task WhenSelectAllFavorites_ThenOnlyFavoritesAreSelected()
    {
        var galleryPath = CreateTempDirectory();
        var favImage = Path.Combine(galleryPath, "fav.png");
        var normalImage = Path.Combine(galleryPath, "normal.png");
        File.WriteAllText(favImage, "test");
        File.WriteAllText(normalImage, "test");

        var favoritesService = new ImageFavoritesService();
        await favoritesService.SetFavoriteAsync(favImage, true);

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectAllFavoritesCommand.Execute(null);

        viewModel.SelectionCount.Should().Be(1);
        var selectedItem = viewModel.MediaItems.Single(item => item.IsSelected);
        selectedItem.FilePath.Should().Be(favImage);
    }

    [Fact]
    public async Task WhenToggleFavorite_ThenItemFavoriteStateChanges()
    {
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "test.png");
        File.WriteAllText(image, "test");

        var favoritesService = new ImageFavoritesService();

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        var item = viewModel.MediaItems[0];
        item.IsFavorite.Should().BeFalse();

        await viewModel.ToggleFavoriteCommand.ExecuteAsync(item);
        item.IsFavorite.Should().BeTrue();

        await viewModel.ToggleFavoriteCommand.ExecuteAsync(item);
        item.IsFavorite.Should().BeFalse();
    }

    [Fact]
    public async Task WhenToggleSelectedFavorites_AllNonFavorites_ThenMarksAllAsFavorites()
    {
        var galleryPath = CreateTempDirectory();
        var image1 = Path.Combine(galleryPath, "a.png");
        var image2 = Path.Combine(galleryPath, "b.png");
        File.WriteAllText(image1, "test");
        File.WriteAllText(image2, "test");

        var favoritesService = new ImageFavoritesService();

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        // Select all
        viewModel.SelectAllCommand.Execute(null);
        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeFalse());

        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);

        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeTrue());
    }

    [Fact]
    public async Task WhenToggleSelectedFavorites_AllFavorites_ThenUnmarksAll()
    {
        var galleryPath = CreateTempDirectory();
        var image1 = Path.Combine(galleryPath, "a.png");
        var image2 = Path.Combine(galleryPath, "b.png");
        File.WriteAllText(image1, "test");
        File.WriteAllText(image2, "test");

        var favoritesService = new ImageFavoritesService();
        await favoritesService.SetFavoriteAsync(image1, true);
        await favoritesService.SetFavoriteAsync(image2, true);

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectAllCommand.Execute(null);
        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeTrue());

        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);

        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeFalse());
    }

    [Fact]
    public async Task WhenToggleSelectedFavorites_MixedSelection_ThenUnmarksAll()
    {
        var galleryPath = CreateTempDirectory();
        var favImage = Path.Combine(galleryPath, "fav.png");
        var normalImage = Path.Combine(galleryPath, "normal.png");
        File.WriteAllText(favImage, "test");
        File.WriteAllText(normalImage, "test");

        var favoritesService = new ImageFavoritesService();
        await favoritesService.SetFavoriteAsync(favImage, true);

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        // Select all — one favorite + one non-favorite = mixed
        viewModel.SelectAllCommand.Execute(null);

        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);

        // Mixed selection should unmark all
        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeFalse());
    }

    [Fact]
    public async Task WhenToggleSelectedFavorites_MixedSelectionClickedTwice_ThenMarksAll()
    {
        var galleryPath = CreateTempDirectory();
        var favImage = Path.Combine(galleryPath, "fav.png");
        var normalImage = Path.Combine(galleryPath, "normal.png");
        File.WriteAllText(favImage, "test");
        File.WriteAllText(normalImage, "test");

        var favoritesService = new ImageFavoritesService();
        await favoritesService.SetFavoriteAsync(favImage, true);

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectAllCommand.Execute(null);

        // First click: mixed → unmark all
        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeFalse());

        // Second click: all non-favorite → mark all
        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().AllSatisfy(item => item.IsFavorite.Should().BeTrue());
    }

    [Fact]
    public async Task WhenToggleSelectedFavorites_ButtonTextReflectsState()
    {
        var galleryPath = CreateTempDirectory();
        var image1 = Path.Combine(galleryPath, "a.png");
        var image2 = Path.Combine(galleryPath, "b.png");
        File.WriteAllText(image1, "test");
        File.WriteAllText(image2, "test");

        var favoritesService = new ImageFavoritesService();

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: favoritesService);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectAllCommand.Execute(null);

        // All non-favorites: button should offer to mark
        viewModel.ToggleFavoritesButtonText.Should().Contain("Mark as Favorites");

        // Mark all as favorites
        await viewModel.ToggleSelectedFavoritesCommand.ExecuteAsync(null);

        // All favorites: button should offer to unmark
        viewModel.ToggleFavoritesButtonText.Should().Contain("Unmark");
    }

    [Fact]
    public void LoadMediaAsync_DoesNotRunMediaScanOnCallingThread()
    {
        // Startup regression guard for issue #397: the media scan does synchronous
        // per-file IO and must not run inline on the thread that invokes the
        // command (the UI thread at startup).
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "img.png"), "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };

        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        // The favorites lookup runs once per scanned folder inside the enumeration
        // loop and completes synchronously (like the real service when no
        // .favorites.json exists), so its callback records the scan thread.
        var scanThreadIds = new List<int>();
        var mockFavorites = new Mock<IImageFavoritesService>();
        mockFavorites.Setup(service => service.GetFavoritesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => scanThreadIds.Add(Environment.CurrentManagedThreadId))
            .ReturnsAsync(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            favoritesService: mockFavorites.Object);

        // Invoke the command from a dedicated non-pool thread standing in for the
        // UI thread, so offloaded thread-pool work can never land back on it.
        var callingThreadId = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                callingThreadId = Environment.CurrentManagedThreadId;
                viewModel.LoadMediaCommand.ExecuteAsync(null).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the gallery load must complete");

        failure.Should().BeNull();
        viewModel.MediaItems.Should().ContainSingle();
        scanThreadIds.Should().NotBeEmpty();
        scanThreadIds.Should().NotContain(callingThreadId,
            "the media scan does blocking file IO and must not run on the thread that invoked the command (issue #397)");
    }

    [Fact]
    public async Task BuildTagIndexCommand_UpdatesIndexedCount_AndPopulatesTagCloud()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.BuildIndexAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TagIndexBuildResult(Indexed: 2, Skipped: 0, Failed: 0, NsfwCount: 0));
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(2);
        mockTagIndex.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TagFrequency("dog", 2) });
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);

        viewModel.IndexedImageCount.Should().Be(2);
        viewModel.TagCloud.Should().ContainSingle(t => t.Name == "dog" && t.Count == 2);
    }

    [Fact]
    public void ToggleAdvancedSearchCommand_TogglesIsAdvancedSearchOpen()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        viewModel.IsAdvancedSearchOpen.Should().BeFalse();
        viewModel.ToggleAdvancedSearchCommand.Execute(null);
        viewModel.IsAdvancedSearchOpen.Should().BeTrue();
        viewModel.ToggleAdvancedSearchCommand.Execute(null);
        viewModel.IsAdvancedSearchOpen.Should().BeFalse();
    }

    [Fact]
    public void ToggleTagFilterCommand_AddsThenRemovesTag_AndTracksHasActiveTagFilters()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var viewModel = new GenerationGalleryViewModel(mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null);

        viewModel.HasActiveTagFilters.Should().BeFalse();
        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.ActiveTagFilters.Should().Contain("dog");
        viewModel.HasActiveTagFilters.Should().BeTrue();

        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.ActiveTagFilters.Should().NotContain("dog");
        viewModel.HasActiveTagFilters.Should().BeFalse();
    }

    [Fact]
    public async Task ApplySortingAndGrouping_WithActiveTagFilter_RestrictsToSearchAsyncResults()
    {
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(s => s.GetSettingsAsync()).ReturnsAsync(new AppSettings
        {
            ImageGalleries = { new ImageGallery { FolderPath = Path.GetTempPath(), IsEnabled = true } }
        });
        var mockEventAggregator = new Mock<IDatasetEventAggregator>();
        var mockDatasetState = new Mock<IDatasetState>();
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>()); // no file matches "dog" in this fixture

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object, mockEventAggregator.Object, mockDatasetState.Object, null,
            tagIndexService: mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.ToggleTagFilterCommand.Execute("dog");
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().BeEmpty();
        mockTagIndex.Verify(t => t.SearchAsync(
            It.Is<IReadOnlyList<string>>(tags => tags.Contains("dog")),
            NsfwFilterMode.ShowAll,
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task BuildTagIndexCommand_ReAppliesActiveFilters_SoNewlyIndexedImagesAppear()
    {
        // The first-run dead end: a user opens Advanced Search and picks a
        // filter before anything is indexed, so nothing matches and the grid
        // empties. Clicking "Build Tag Index" is the obvious way out — but it
        // only works if the build re-runs the filter pipeline afterwards.
        // Without that the grid stays empty and the only escape is toggling
        // some unrelated filter.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");
        File.WriteAllText(Path.Combine(galleryPath, "b.png"), "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        // The fixture's index starts empty and is filled by the build, so
        // SearchAsync genuinely changes its answer across the two passes.
        IReadOnlyList<string> indexedPaths = Array.Empty<string>();
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => indexedPaths.Count);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TagFrequency("dog", 2) });
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => indexedPaths);
        mockTagIndex.Setup(t => t.BuildIndexAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() => indexedPaths = Directory.GetFiles(galleryPath))
            .ReturnsAsync(new TagIndexBuildResult(Indexed: 2, Skipped: 0, Failed: 0, NsfwCount: 0));

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().HaveCount(2);

        viewModel.ToggleTagFilterCommand.Execute("dog");
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.Should().BeEmpty("nothing is indexed yet, so the tag matches no file");

        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().HaveCount(2,
            "the freshly indexed files satisfy the active filter and must reappear without the user toggling anything else");
    }

    [Fact]
    public void SetNsfwFilter_CountsAsAnActiveFilter_AndClearTagFiltersResetsIt()
    {
        // HasActiveTagFilters drives the active-filter strip, which carries
        // the only "Clear filters" affordance. The NSFW mode filters on its
        // own, with no tag selected, so it has to count — otherwise the
        // gallery is visibly filtered with nothing on screen saying so.
        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);

        var hasActiveFiltersNotifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GenerationGalleryViewModel.HasActiveTagFilters))
                Interlocked.Increment(ref hasActiveFiltersNotifications);
        };

        viewModel.HasActiveTagFilters.Should().BeFalse();
        viewModel.ActiveTagFilters.Should().BeEmpty();

        viewModel.SetNsfwFilterCommand.Execute(nameof(NsfwFilterMode.HideNsfw));

        viewModel.NsfwFilter.Should().Be(NsfwFilterMode.HideNsfw);
        viewModel.IsNsfwFilterHideNsfw.Should().BeTrue();
        viewModel.HasActiveTagFilters.Should().BeTrue("the NSFW mode alone narrows the gallery");
        Volatile.Read(ref hasActiveFiltersNotifications).Should().BeGreaterThan(0,
            "the strip only appears if the change is actually notified");

        viewModel.ClearTagFiltersCommand.Execute(null);

        viewModel.NsfwFilter.Should().Be(NsfwFilterMode.ShowAll, "'Clear filters' has to clear all of them");
        viewModel.IsNsfwFilterShowAll.Should().BeTrue();
        viewModel.IsNsfwFilterHideNsfw.Should().BeFalse();
        viewModel.HasActiveTagFilters.Should().BeFalse();
    }

    [Fact]
    public async Task WhenFiltersMatchNothing_ThenEmptyStateBlamesTheFilters_NotTheFolderConfiguration()
    {
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().ContainSingle();

        // Hide NSFW works on the KNOWN-NSFW set. With nothing indexed that
        // set is empty, so nothing is hidden — an unindexed file is "not
        // known NSFW", not "excluded from the universe" (blanking the whole
        // gallery here was a bug).
        viewModel.SetNsfwFilterCommand.Execute(nameof(NsfwFilterMode.HideNsfw));
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.Should().ContainSingle("Hide NSFW must not hide images that were never indexed");

        // NSFW-only inverts that: only known-NSFW files match, and nothing is
        // known — the grid empties, and the message must blame the filter,
        // not the folder configuration.
        viewModel.SetNsfwFilterCommand.Execute(nameof(NsfwFilterMode.NsfwOnly));
        await viewModel.WaitForSortingAsync();

        viewModel.HasNoMedia.Should().BeTrue();
        viewModel.NoMediaMessage.Should().Contain("filters");
        viewModel.NoMediaMessage.Should().NotContain("Settings");

        viewModel.ClearTagFiltersCommand.Execute(null);
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().ContainSingle();
        viewModel.NoMediaMessage.Should().Contain("Settings",
            "with no filter active the empty state goes back to the folder-configuration guidance");
    }

    [Fact]
    public async Task LoadMediaAsync_WithNothingIndexed_SkipsTheTileHydrationQuery()
    {
        // A user who has never clicked "Build Tag Index" must pay nothing for
        // this feature on every gallery load: with an empty index the lookup
        // can only come back empty, so it should not run at all.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.IndexedImageCount.Should().Be(0);
        mockTagIndex.Verify(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the status pill still needs the count refresh");
        mockTagIndex.Verify(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LoadMediaAsync_WithAnIndexedGallery_StillHydratesTileTagData()
    {
        // The other half of the gate: skipping when nothing is indexed must
        // not turn into skipping when something is.
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "a.png");
        File.WriteAllText(image, "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>(StringComparer.OrdinalIgnoreCase)
            {
                [Path.GetFullPath(image)] = new ImageTagLookup(IsNsfw: true, Tags: ["dog", "outdoor"]),
            });

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        mockTagIndex.Verify(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        var item = viewModel.MediaItems.Single();
        item.IsNsfw.Should().BeTrue();
        item.Tags.Should().BeEquivalentTo(new[] { "dog", "outdoor" });
    }

    [Fact]
    public void IsTaggingAvailable_ReflectsWhetherATagIndexServiceExists()
    {
        // Issue #489: the view binds every tagging affordance's visibility to
        // this single gate, so a configuration without the service shows no
        // tagging UI instead of clickable buttons that silently do nothing.
        var without = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null);
        without.IsTaggingAvailable.Should().BeFalse();

        var with = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: new Mock<ITagIndexService>().Object);
        with.IsTaggingAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task LoadMediaAsync_NeverSeedsAnNsfwModeFromSettings()
    {
        // Regression guard for a removed behavior: an earlier iteration
        // seeded the drawer's NSFW mode from AppSettings.ShowNsfw (off by
        // default). In real use that silently opened every gallery in Hide
        // NSFW mode, and because the tagger rates much ordinary content
        // "sensitive", a tag filter matching thousands of images showed a
        // fraction of them with nothing on screen explaining why. The NSFW
        // mode is opt-in, per session, full stop.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }],
            ShowNsfw = false,
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        var viewModel = new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: new Mock<ITagIndexService>().Object);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        await viewModel.WaitForSortingAsync();

        viewModel.NsfwFilter.Should().Be(NsfwFilterMode.ShowAll);
        viewModel.HasActiveTagFilters.Should().BeFalse();
        viewModel.MediaItems.Should().ContainSingle("no invisible filter may hide gallery content");
    }

    [Fact]
    public async Task WhenTagMatchesAreHiddenByTheDateFilter_TheEmptyStateNamesTheCulprit()
    {
        // Field-diagnosed: 331 images matched the tag, the grid showed zero,
        // and nothing on screen said why — the default "Last 3 Months" date
        // window was quietly excluding every match (a partially built index
        // covers the oldest files first, exactly what a recent-only window
        // hides). The empty state must name the culprit and the fix.
        var galleryPath = CreateTempDirectory();
        var oldImage = Path.Combine(galleryPath, "old.png");
        File.WriteAllText(oldImage, "test");
        File.SetCreationTimeUtc(oldImage, DateTime.UtcNow.AddYears(-1));

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { oldImage });

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.SelectedDateFilter.Should().Be("Last 3 Months", "precondition: the default window hides year-old files");

        viewModel.ToggleTagFilterCommand.Execute("dog");
        await viewModel.WaitForSortingAsync();

        viewModel.MediaItems.Should().BeEmpty();
        viewModel.NoMediaMessage.Should().Contain("date filter")
            .And.Contain("Last 3 Months")
            .And.Contain("All Time", "the message must hand the user the fix, not just the diagnosis");

        viewModel.SelectedDateFilter = "All Time";
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.Should().ContainSingle("widening the window reveals the match, proving the diagnosis");
    }

    [Fact]
    public async Task RebuildTagIndexCommand_ClearsTheIndex_ThenRunsAFullBuild()
    {
        // The incremental build skips unchanged files forever, so a row
        // written by an older build (different tagger/threshold, or simply
        // bad) is sticky — Rebuild is the escape hatch: wipe, then re-tag.
        var galleryPath = CreateTempDirectory();
        var image = Path.Combine(galleryPath, "a.png");
        File.WriteAllText(image, "test");

        var calls = new List<string>();
        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        mockTagIndex.Setup(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("remove"))
            .ReturnsAsync(1);
        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() => calls.Add("build"))
            .ReturnsAsync(new TagIndexBuildResult(1, 0, 0, 0));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await viewModel.RebuildTagIndexCommand.ExecuteAsync(null);

        calls.Should().Equal("remove", "build");
        mockTagIndex.Verify(t => t.RemoveIndexEntriesAsync(
            It.Is<IReadOnlyList<string>>(p => p.Contains(image)), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RebuildTagIndexCommand_WhenTheWipeFails_DoesNotBuildOnTopOfStaleRows()
    {
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        mockTagIndex.Setup(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 5: 'database is locked'."));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await viewModel.RebuildTagIndexCommand.ExecuteAsync(null);

        mockTagIndex.Verify(t => t.BuildIndexAsync(
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()), Times.Never);
        viewModel.StatusMessage.Should().NotBeNullOrEmpty("the user must learn the rebuild did not happen");
    }

    [Fact]
    public async Task BuildTagIndexCommand_CompletesItsTrackedTask_InTheUnifiedConsole()
    {
        // Issue #488: the ~30-second model download was already visible in
        // the Unified Console but the potentially far longer index build was
        // not. The build registers a tracked task and finishes it with the
        // same summary the gallery shows.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var tracker = new TaskTracker(new Mock<IUnifiedLogger>().Object);
        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(Indexed: 1, Skipped: 0, Failed: 0, NsfwCount: 0));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object, tracker);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);

        var tracked = tracker.AllTasks.Should().ContainSingle(t => t.Name == "Building tag index").Subject;
        tracked.Status.Should().Be(TrackedTaskStatus.Completed);
        tracked.StatusText.Should().Be(viewModel.StatusMessage,
            "the console shows the same summary as the gallery");
    }

    [Fact]
    public async Task BuildTagIndexCommand_CanBeCancelledFromTheUnifiedConsole()
    {
        // The tracker is handed the same CancellationTokenSource as the
        // toolbar Cancel button, so the console's per-task Cancel stops a
        // build started from the gallery.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var tracker = new TaskTracker(new Mock<IUnifiedLogger>().Object);

        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(0);
        mockTagIndex.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TagFrequency>());
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<string> _, IProgress<TagIndexBuildProgress> _, CancellationToken ct) =>
            {
                buildStarted.TrySetResult();
                // Ends only through cancellation — exactly the long build the
                // console needs to be able to stop.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return new TagIndexBuildResult(0, 0, 0, 0);
            });

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object, tracker);

        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        var buildTask = viewModel.BuildTagIndexCommand.ExecuteAsync(null);
        await buildStarted.Task;

        var tracked = tracker.AllTasks.Should().ContainSingle(t => t.Name == "Building tag index").Subject;
        tracked.Status.Should().Be(TrackedTaskStatus.Running);

        tracker.CancelTask(tracked.TaskId);
        await buildTask;

        viewModel.StatusMessage.Should().Contain("cancelled");
        viewModel.IsIndexingTagIndex.Should().BeFalse();
        tracked.IsTerminal.Should().BeTrue();
    }

    [Fact]
    public async Task TagCloudSearchText_FiltersTheVisibleChips_WithoutTouchingActiveFilters()
    {
        // A fully indexed gallery fills the cloud's 200-chip budget with booru
        // tags — the filter box narrows what is SHOWN, case-insensitively,
        // and must never alter which filters are ACTIVE.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        mockTagIndex.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new TagFrequency("dog", 3), new TagFrequency("door", 2), new TagFrequency("cat", 1) });
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<string>());

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);

        viewModel.TagCloud.Should().HaveCount(3, "no filter shows the whole cloud");

        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.TagCloudSearchText = "do";

        viewModel.TagCloud.Select(t => t.Name).Should().BeEquivalentTo(new[] { "dog", "door" });
        viewModel.ActiveTagFilters.Should().ContainSingle().Which.Should().Be("dog",
            "the box filters the display, not the active filter set");
        viewModel.TagCloudHeader.Should().Contain("3 tags",
            "the header describes the index and must not shrink while typing");

        viewModel.TagCloudSearchText = "DOOR";
        viewModel.TagCloud.Select(t => t.Name).Should().BeEquivalentTo(new[] { "door" }, "matching is case-insensitive");

        // Deactivate "dog" while the filter hides its chip, then clear the
        // filter: the chip must come back with its CURRENT state, not the
        // state it had when it was hidden.
        viewModel.ToggleTagFilterCommand.Execute("dog");
        viewModel.TagCloudSearchText = null;

        viewModel.TagCloud.Should().HaveCount(3);
        viewModel.TagCloud.Single(t => t.Name == "dog").IsActive.Should().BeFalse(
            "chips hidden by the display filter stay in sync with the active filter set");
    }

    // ---- Final-review fixes: threading, resilience, feedback, cancellation ----

    [Fact]
    public void BuildTagIndexCommand_DoesNotRunTheIndexBuildOnTheCallingThread()
    {
        // Same bug class as issue #397, which LoadMediaAsync already guards
        // against a few lines away: BuildIndexAsync decodes every image,
        // allocates a width*height*4 buffer per file, stats files and runs
        // SQLite queries that complete synchronously. None of that yields, so
        // running it inline freezes the window for the whole build.
        var buildThreadIds = new List<int>();
        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Callback(() => buildThreadIds.Add(Environment.CurrentManagedThreadId))
            .ReturnsAsync(new TagIndexBuildResult(1, 0, 0, 0));

        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        var callingThreadId = RunOnDedicatedThread(
            null,
            () => viewModel.BuildTagIndexCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        buildThreadIds.Should().NotBeEmpty();
        buildThreadIds.Should().NotContain(callingThreadId,
            "the index build does blocking image decoding and DB IO and must not run on the thread that invoked the command (issue #397)");
    }

    [Fact]
    public void BuildTagIndexCommand_RoutesProgressBackThroughTheCapturedSynchronizationContext()
    {
        // Offloading the build (test above) is only safe because the
        // Progress<T> is still constructed on the UI thread, so its callbacks
        // — which write BusyMessage, a bound property — go back through the
        // captured context instead of firing on the pool thread that reported
        // them. Verified rather than assumed.
        var uiContext = new RecordingSynchronizationContext();
        var postsDuringReport = 0;
        var reportingThreadId = 0;
        var busyMessageAtReport = (string?)null;

        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, IProgress<TagIndexBuildProgress>?, CancellationToken>((_, progress, _) =>
            {
                reportingThreadId = Environment.CurrentManagedThreadId;
                var before = uiContext.PostCount;
                progress!.Report(new TagIndexBuildProgress(3, 10, "C:\\g\\img.png"));
                postsDuringReport = uiContext.PostCount - before;
                busyMessageAtReport = viewModel.BusyMessage;
            })
            .ReturnsAsync(new TagIndexBuildResult(1, 0, 0, 0));

        var uiThreadId = RunOnDedicatedThread(
            uiContext,
            () => viewModel.BuildTagIndexCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        reportingThreadId.Should().NotBe(uiThreadId,
            "the report has to come off the pool thread for the marshalling to matter");
        postsDuringReport.Should().BeGreaterThan(0,
            "Progress<T> must hand the callback to the synchronization context it captured on the UI thread");
        busyMessageAtReport.Should().Contain("3", "the recording context runs the callback inline, so the message is already applied");
    }

    [Fact]
    public void BuildTagIndexCommand_ShowsTheDownloadPhaseText_InsteadOfAFrozenFileCounter()
    {
        // The model download is a phase, not a file, and it runs for minutes
        // before a single image is touched. A handler that only asked whether
        // CurrentFile was null sat on "Indexing images… 0/N" the whole time —
        // actively misleading at the one moment the user is most likely to
        // think the app has hung.
        var uiContext = new RecordingSynchronizationContext();
        var messages = new List<string?>();

        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(1, 0, 0, 0));
        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<string>, IProgress<TagIndexBuildProgress>?, CancellationToken>((_, progress, _) =>
            {
                // The recording context runs each callback inline, so
                // BusyMessage is already applied when Report returns.
                progress!.Report(new TagIndexBuildProgress(0, 12, null, "Downloading tagger model…"));
                messages.Add(viewModel.BusyMessage);
                progress.Report(new TagIndexBuildProgress(4, 12, "C:\\g\\img.png"));
                messages.Add(viewModel.BusyMessage);
                progress.Report(new TagIndexBuildProgress(12, 12, null));
                messages.Add(viewModel.BusyMessage);
            })
            .ReturnsAsync(new TagIndexBuildResult(1, 0, 0, 0));

        RunOnDedicatedThread(uiContext, () => viewModel.BuildTagIndexCommand.ExecuteAsync(null).GetAwaiter().GetResult());

        messages.Should().HaveCount(3);
        messages[0].Should().Be("Downloading tagger model…", "phase text is displayed verbatim");
        messages[1].Should().Contain("4").And.Contain("12");
        messages[2].Should().Be("Finalizing index…");
    }

    [Fact]
    public async Task LoadMediaCommand_WhenTheIndexedCountQueryThrows_StillLoadsTheGallery()
    {
        // DatabaseRecoveryService can stamp a migration as applied without
        // creating its tables when the DB is locked at startup — permanently,
        // because later startups then see nothing pending. That surfaces here
        // as "no such table". RunBusyAsync does not catch, so an escape is an
        // unhandled exception in Avalonia's dispatcher loop.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 1: 'no such table: ImageMediaTagIndexes'."));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);

        var act = async () => await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync("a broken tag index must cost the user their tag data, not the gallery");
        viewModel.MediaItems.Should().ContainSingle();
        viewModel.IndexedImageCount.Should().Be(0, "the count keeps its prior value when the query fails");
    }

    [Fact]
    public async Task LoadMediaCommand_WhenTheTileTagLookupThrows_StillLoadsTheGallery()
    {
        // The other unguarded call on the same path: the count query can
        // succeed against a half-migrated schema and the hydration join still
        // blow up.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(7);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 1: 'no such table: ImageMediaTagAssignments'."));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);

        var act = async () => await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await act.Should().NotThrowAsync();
        viewModel.MediaItems.Should().ContainSingle();
        viewModel.MediaItems.Single().Tags.Should().BeEmpty("the tiles simply have no tag data to show");
    }

    [Fact]
    public async Task ApplySortingAndGrouping_WhenTagSearchThrows_FailsClosedWithAStatusMessage()
    {
        // This one runs from property setters and fire-and-forget
        // continuations, so a fault does not even have a command to surface
        // through. It must not crash — but it must not fail OPEN either: a
        // content filter the user believes is active silently showing
        // everything (e.g. NSFW images during screen-sharing) is worse than
        // an empty grid. The pipeline hides results and says why.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.SearchAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<NsfwFilterMode>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 1: 'no such table: ImageMediaTagIndexes'."));

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        viewModel.ToggleTagFilterCommand.Execute("dog");
        var act = async () => await viewModel.WaitForSortingAsync();

        await act.Should().NotThrowAsync();
        viewModel.MediaItems.Should().BeEmpty("a filter that cannot be resolved fails closed rather than showing unfiltered results");
        viewModel.StatusMessage.Should().NotBeNullOrEmpty("the user must be told the filter is unavailable");
    }

    [Fact]
    public async Task BuildTagIndexCommand_ReportsWhatTheBuildActuallyDid()
    {
        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(Indexed: 1180, Skipped: 40, Failed: 14, NsfwCount: 3));

        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().NotBeNullOrEmpty(
            "a build that vanishes without a word is indistinguishable from one that did nothing");
        viewModel.StatusMessage.Should().Contain(1180.ToString("N0", CultureInfo.CurrentCulture));
        viewModel.StatusMessage.Should().Contain("40").And.Contain("14");
    }

    [Fact]
    public async Task BuildTagIndexCommand_WhenNothingCouldBeIndexed_SaysSoDistinctly()
    {
        // A failed model download comes back as "everything failed". Reporting
        // that as "indexed 0 · skipped 0 · failed 3" reads like three awkward
        // files rather than "none of this worked".
        var totalFailure = BuildableTagIndex(new TagIndexBuildResult(Indexed: 0, Skipped: 0, Failed: 3, NsfwCount: 0));
        var partial = BuildableTagIndex(new TagIndexBuildResult(Indexed: 1, Skipped: 0, Failed: 2, NsfwCount: 0));

        var failedViewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object, new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object, null, tagIndexService: totalFailure.Object);
        var partialViewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object, new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object, null, tagIndexService: partial.Object);

        await failedViewModel.BuildTagIndexCommand.ExecuteAsync(null);
        await partialViewModel.BuildTagIndexCommand.ExecuteAsync(null);

        failedViewModel.StatusMessage.Should().Contain("failed").And.Contain("log");
        failedViewModel.StatusMessage.Should().NotBe(partialViewModel.StatusMessage,
            "'nothing worked' and 'some files failed' are different situations for the user");
    }

    [Fact]
    public async Task CancelTagIndexCommand_StopsTheRunningBuild_AndReportsItAsCancelled()
    {
        using var buildStarted = new ManualResetEventSlim();
        using var releaseBuild = new ManualResetEventSlim();

        var mockTagIndex = BuildableTagIndex(new TagIndexBuildResult(0, 0, 0, 0));
        mockTagIndex.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<string> _, IProgress<TagIndexBuildProgress>? _, CancellationToken ct) =>
            {
                buildStarted.Set();
                releaseBuild.Wait(TimeSpan.FromSeconds(30));

                // Mirrors the real service's policy: cancellation propagates.
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new TagIndexBuildResult(0, 0, 0, 0));
            });

        var viewModel = new GenerationGalleryViewModel(
            new Mock<IAppSettingsService>().Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: mockTagIndex.Object);

        viewModel.CancelTagIndexCommand.CanExecute(null).Should().BeFalse("there is nothing to cancel yet");

        var build = viewModel.BuildTagIndexCommand.ExecuteAsync(null);
        buildStarted.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("the build must actually start");

        viewModel.IsIndexingTagIndex.Should().BeTrue("the Cancel button is bound to this");
        viewModel.CancelTagIndexCommand.CanExecute(null).Should().BeTrue();

        viewModel.CancelTagIndexCommand.Execute(null);
        releaseBuild.Set();

        var act = async () => await build;
        await act.Should().NotThrowAsync("cancelling is a normal outcome, not an unhandled error");

        viewModel.StatusMessage.Should().Contain("cancelled");
        viewModel.IsIndexingTagIndex.Should().BeFalse();
        viewModel.CancelTagIndexCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task TagFilter_AgainstTheRealService_ShowsTheIndexedMatches_AfterABuildWithTheFilterAlreadyActive()
    {
        // End-to-end repro of a field report: 55 images indexed with "1girl",
        // chip active, rating on Show all — and the grid stayed empty. Runs
        // the REAL TagIndexService against a real SQLite file, with ComfyUI
        // style %placeholder% filenames like the live gallery had, in the
        // exact order the user acted: filter first (index still empty), then
        // the build.
        var galleryPath = CreateTempDirectory();
        var girlA = Path.Combine(galleryPath, "%batch_index%_%counter%_00001_.png");
        var girlB = Path.Combine(galleryPath, "%seed%_00002_.png");
        var other = Path.Combine(galleryPath, "landscape_00003_.png");
        SaveTinyPng(girlA, width: 8);
        SaveTinyPng(girlB, width: 8);
        SaveTinyPng(other, width: 4);

        var dbDir = CreateTempDirectory();
        var options = DiffusionNexusCoreDbContext.CreateOptions(dbDir);
        await using (var context = new DiffusionNexusCoreDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, float _, CancellationToken _) =>
                SixLabors.ImageSharp.Image.Identify(path).Width == 8
                    ? ImageTagResult.Succeeded(new[] { new ImageTagScore("1girl", 0.95f) }, "sensitive", 0.9f, isNsfw: false)
                    : ImageTagResult.Succeeded(new[] { new ImageTagScore("landscape", 0.9f) }, "general", 0.9f, isNsfw: false));
        var tagIndex = new TagIndexService(new TestDbContextFactory(options), tagging.Object);

        var viewModel = CreateGalleryViewModel(galleryPath, tagIndex);
        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        viewModel.MediaItems.Should().HaveCount(3);

        // Filter first — nothing indexed yet, so the grid legitimately empties.
        viewModel.ToggleTagFilterCommand.Execute("1girl");
        await viewModel.WaitForSortingAsync();
        viewModel.MediaItems.Should().BeEmpty("nothing is indexed yet");

        await viewModel.BuildTagIndexCommand.ExecuteAsync(null);
        await viewModel.WaitForSortingAsync();

        viewModel.IndexedImageCount.Should().Be(3);
        viewModel.MediaItems.Select(i => i.FilePath).Should().BeEquivalentTo(
            new[] { girlA, girlB },
            "the freshly indexed 1girl matches must appear while the chip is active");
    }

    [Fact]
    public async Task DeleteImageCommand_PrunesTheDeletedFileFromTheTagIndex_AndCorrectsTheCounters()
    {
        // Nothing used to remove an index row when its file left the gallery,
        // so "N / M indexed" and the tag cloud drifted permanently stale after
        // the very first delete. Wired against the real service and a real
        // SQLite file so the row deletion is actually observable.
        var galleryPath = CreateTempDirectory();
        var deleted = Path.Combine(galleryPath, "gone.png");
        var kept = Path.Combine(galleryPath, "stays.png");
        SaveTinyPng(deleted);
        SaveTinyPng(kept);

        var dbDir = CreateTempDirectory();
        var options = DiffusionNexusCoreDbContext.CreateOptions(dbDir);
        await using (var context = new DiffusionNexusCoreDbContext(options))
        {
            await context.Database.EnsureCreatedAsync();
        }

        var tagging = new Mock<IImageTaggingService>();
        tagging.Setup(t => t.GetModelStatus()).Returns(ModelStatus.Ready);
        tagging.Setup(t => t.TagImageAsync(It.IsAny<string>(), It.IsAny<float>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ImageTagResult.Succeeded(new[] { new ImageTagScore("dog", 0.9f) }, "general", 0.9f, isNsfw: false));
        var tagIndex = new TagIndexService(new TestDbContextFactory(options), tagging.Object);
        (await tagIndex.BuildIndexAsync(new[] { deleted, kept })).Indexed.Should().Be(2);

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var viewModel = CreateGalleryViewModel(galleryPath, tagIndex);
        viewModel.DialogService = mockDialog.Object;
        await viewModel.LoadMediaCommand.ExecuteAsync(null);
        viewModel.TotalGalleryImageCount.Should().Be(2);
        viewModel.IndexedImageCount.Should().Be(2);

        var doomed = viewModel.MediaItems.Single(i => string.Equals(i.FilePath, deleted, StringComparison.OrdinalIgnoreCase));
        await viewModel.DeleteImageCommand.ExecuteAsync(doomed);
        await viewModel.WaitForTagIndexPruneAsync();

        (await tagIndex.GetIndexedCountAsync()).Should().Be(1, "the deleted file's index row must be gone");
        (await tagIndex.GetTagsForFilesAsync(new[] { deleted })).Should().BeEmpty();
        (await tagIndex.GetTagsForFilesAsync(new[] { kept })).Should().ContainKey(Path.GetFullPath(kept));

        viewModel.TotalGalleryImageCount.Should().Be(1, "the '/ M' half of the indexed pill counts gallery images");
        viewModel.IndexedImageCount.Should().Be(1, "the 'N /' half tracks rows that were actually removed");
    }

    [Fact]
    public async Task RemovingAnUnindexedImage_LeavesTheIndexedCountAlone()
    {
        // The prune fires for every removed image without first asking whether
        // it was ever indexed, so "no row deleted" must not decrement anything.
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(5);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        viewModel.DialogService = mockDialog.Object;
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await viewModel.DeleteImageCommand.ExecuteAsync(viewModel.MediaItems.Single());
        await viewModel.WaitForTagIndexPruneAsync();

        mockTagIndex.Verify(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once);
        viewModel.IndexedImageCount.Should().Be(5);
        viewModel.TotalGalleryImageCount.Should().Be(0);
    }

    [Fact]
    public async Task DeleteImageCommand_WhenThePruneThrows_DoesNotFailTheDelete()
    {
        var galleryPath = CreateTempDirectory();
        File.WriteAllText(Path.Combine(galleryPath, "a.png"), "test");

        var mockTagIndex = new Mock<ITagIndexService>();
        mockTagIndex.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(1);
        mockTagIndex.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        mockTagIndex.Setup(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite Error 5: 'database is locked'."));

        var mockDialog = new Mock<IDialogService>();
        mockDialog.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        var viewModel = CreateGalleryViewModel(galleryPath, mockTagIndex.Object);
        viewModel.DialogService = mockDialog.Object;
        await viewModel.LoadMediaCommand.ExecuteAsync(null);

        await viewModel.DeleteImageCommand.ExecuteAsync(viewModel.MediaItems.Single());
        var act = async () => await viewModel.WaitForTagIndexPruneAsync();

        await act.Should().NotThrowAsync("index pruning is best-effort cleanup, not part of the delete");
        mockTagIndex.Verify(t => t.RemoveIndexEntriesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Once,
            "the assertion above is only meaningful if the prune was actually attempted");
        viewModel.MediaItems.Should().BeEmpty();
    }

    public void Dispose()
    {
        foreach (var path in _tempPaths)
        {
            // Best effort: a test that opened a SQLite file may still be
            // holding it through the provider's connection pool. Narrowed to
            // the file-in-use/permission cases — a blanket catch would also
            // hide a real handle leak from ever surfacing.
            try { Directory.Delete(path, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* leave it to the OS */ }
        }
    }

    private string CreateTempDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "DiffusionNexusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempPaths.Add(root);
        return root;
    }

    private static void SaveTinyPng(string path, int width = 4)
    {
        using var image = new Image<Rgba32>(width, 4);
        image.SaveAsPng(path);
    }

    /// <summary>
    /// A gallery ViewModel over a single enabled folder, with the supplied tag
    /// index service.
    /// </summary>
    private static GenerationGalleryViewModel CreateGalleryViewModel(
        string galleryPath, ITagIndexService tagIndexService, ITaskTracker? taskTracker = null)
    {
        var settings = new AppSettings
        {
            ImageGalleries = [new() { FolderPath = galleryPath, IsEnabled = true, Order = 0 }]
        };
        var mockSettings = new Mock<IAppSettingsService>();
        mockSettings.Setup(service => service.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        return new GenerationGalleryViewModel(
            mockSettings.Object,
            new Mock<IDatasetEventAggregator>().Object,
            new Mock<IDatasetState>().Object,
            null,
            tagIndexService: tagIndexService,
            taskTracker: taskTracker);
    }

    /// <summary>
    /// A tag index mock whose build succeeds with <paramref name="result"/> and
    /// whose post-build refresh queries all answer harmlessly.
    /// </summary>
    private static Mock<ITagIndexService> BuildableTagIndex(TagIndexBuildResult result)
    {
        var mock = new Mock<ITagIndexService>();
        mock.Setup(t => t.BuildIndexAsync(
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<IProgress<TagIndexBuildProgress>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        mock.Setup(t => t.GetIndexedCountAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(result.Indexed);
        mock.Setup(t => t.GetTagCloudAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TagFrequency>());
        mock.Setup(t => t.GetTagsForFilesAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, ImageTagLookup>());
        return mock;
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a dedicated non-pool thread, optionally
    /// with <paramref name="context"/> installed, standing in for the UI
    /// thread: work offloaded to the thread pool can never land back on it.
    /// Returns that thread's managed id.
    /// </summary>
    private static int RunOnDedicatedThread(SynchronizationContext? context, Action body)
    {
        var threadId = 0;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                threadId = Environment.CurrentManagedThreadId;
                if (context is not null)
                {
                    SynchronizationContext.SetSynchronizationContext(context);
                }

                body();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the operation must complete");
        failure.Should().BeNull();
        return threadId;
    }

    /// <summary>
    /// Stands in for Avalonia's UI synchronization context. Counts every
    /// <see cref="Post"/> and runs the callback inline, so a test can tell
    /// whether <see cref="Progress{T}"/> went through the context it captured
    /// or fired straight off the thread that reported.
    /// </summary>
    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            d(state);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<DiffusionNexusCoreDbContext>
    {
        private readonly DbContextOptions<DiffusionNexusCoreDbContext> _options;

        public TestDbContextFactory(DbContextOptions<DiffusionNexusCoreDbContext> options) => _options = options;

        public DiffusionNexusCoreDbContext CreateDbContext() => new(_options);

        public Task<DiffusionNexusCoreDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CreateDbContext());
    }
}
