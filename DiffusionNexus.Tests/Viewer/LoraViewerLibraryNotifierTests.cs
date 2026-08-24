using DiffusionNexus.DataAccess.Repositories.Interfaces;
using DiffusionNexus.DataAccess.UnitOfWork;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// The Installed tab's half of the "library gained a model" signal (spec RC5): before it,
/// only the surface that ran a download could refresh the grid, so the Browse queue's
/// downloads never showed up until a manual refresh. The viewer now subscribes to
/// <see cref="ILibraryChangeNotifier"/>, and — because a queue batch raises one signal per
/// file — coalesces the arrivals into a single rebuild.
/// <para>
/// No Avalonia platform is initialised (that deadlocks the suite), so the dispatcher hop in
/// <c>OnLibraryModelDownloaded</c> is not exercised here; the coalescing half it posts to is
/// driven directly, exactly as it runs on the UI thread — synchronously up to its first await.
/// </para>
/// </summary>
public class LoraViewerLibraryNotifierTests
{
    private sealed class FakeLibraryChangeNotifier : ILibraryChangeNotifier
    {
        public event EventHandler<ModelDownloadedEventArgs>? ModelDownloaded;

        public int SubscriberCount => ModelDownloaded?.GetInvocationList().Length ?? 0;

        public void NotifyModelDownloaded(int modelId)
            => ModelDownloaded?.Invoke(this, new ModelDownloadedEventArgs(modelId));
    }

    private readonly Mock<IModelSyncService> _modelSync = new();
    private readonly Mock<IAppSettingsService> _settings = new();
    private readonly Mock<IUnitOfWork> _unitOfWork = new();
    private readonly Mock<IModelRepository> _models = new();

    private LoraViewerViewModel CreateViewModel(ILibraryChangeNotifier? notifier)
    {
        _modelSync.Setup(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<InstalledModelFile>());
        _unitOfWork.SetupGet(u => u.Models).Returns(_models.Object);

        var services = new ServiceCollection();
        services.AddSingleton(_modelSync.Object);
        services.AddSingleton(_unitOfWork.Object);
        var provider = services.BuildServiceProvider();

        return new LoraViewerViewModel(
            _settings.Object,
            _modelSync.Object,
            civitaiClient: null,
            secureStorage: null,
            logger: null,
            baseModelCatalog: null,
            updateChecker: null,
            librarySync: null,
            uiScheduler: new ImmediateUiScheduler(),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
            changeNotifier: notifier);
    }

    [Fact]
    public void ConstructionSubscribesToTheLibraryChangeNotifier()
    {
        var notifier = new FakeLibraryChangeNotifier();

        _ = CreateViewModel(notifier);

        notifier.SubscriberCount.Should().Be(1,
            "the Installed tab must rebuild whichever surface downloaded — including the Browse queue");
    }

    [Fact]
    public async Task ABatchOfArrivalsCoalescesIntoASingleRebuild()
    {
        var vm = CreateViewModel(new FakeLibraryChangeNotifier());

        // 20 queue jobs finishing inside the debounce window (the handler posts one call each).
        var arrivals = Enumerable.Range(0, 20).Select(_ => vm.CoalesceRebuildAsync()).ToList();
        await Task.WhenAll(arrivals);

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Once,
            "one rebuild must cover every arrival during the debounce window");
    }

    [Fact]
    public async Task AnArrivalAfterTheWindowClosesRebuildsAgain()
    {
        var vm = CreateViewModel(new FakeLibraryChangeNotifier());

        await vm.CoalesceRebuildAsync();
        await vm.CoalesceRebuildAsync();

        _modelSync.Verify(s => s.LoadCachedFilesAsync(It.IsAny<CancellationToken>()), Times.Exactly(2),
            "the coalescing flag must reset once the rebuild it covered has run");
    }
}
