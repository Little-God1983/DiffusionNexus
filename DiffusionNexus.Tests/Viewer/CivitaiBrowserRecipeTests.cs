using System.Net;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.Tests.Viewer.CivitaiRecipe;
using DiffusionNexus.UI.Services.CivitaiBrowser;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using DiffusionNexus.UI.ViewModels.CivitaiBrowser;
using FluentAssertions;
using Avalonia.Threading;
using Moq;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// #628: a Civitai image link in the Browse Civitai search box shows the recipe view in place of
/// the cards; its "Add N to queue" goes through the browser's own queue path.
/// </summary>
[Collection(DispatcherDrainCollection.Name)]
public sealed class CivitaiBrowserRecipeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-browser-recipe").FullName;
    private readonly Mock<ICivitaiClient> _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private (CivitaiBrowserViewModel Vm, CivitaiDownloadQueue Queue) Create()
    {
        var queue = new CivitaiDownloadQueue(null, null, null, null, persistPathOverride: Path.Combine(_dir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_dir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null)
        {
            RecipeServiceFactory = () => new CivitaiImageRecipeService(
                _client.Object, new HttpClient(new PngHandler()), Path.Combine(_dir, "cache"), null),
        };
        return (vm, queue);
    }

    private void HasImage(long id, int rating, params int[] versionIds) =>
        _client.Setup(c => c.GetImagesAsync(It.Is<CivitaiImagesQuery>(q => q.ImageId == id), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>
            {
                Items = [new CivitaiModelImage { Id = id, Url = "https://image.civitai.com/x/a.jpeg", Type = "image", BrowsingLevel = rating, ModelVersionIds = versionIds }],
            });

    private void HasLora(int versionId, int modelId, params CivitaiModelImage[] images)
    {
        var version = new CivitaiModelVersion
        {
            Id = versionId, ModelId = modelId, Name = "v1", BaseModel = "SDXL 1.0", Images = images,
            DownloadUrl = $"https://civitai.com/api/download/models/{versionId}",
            Files = [new CivitaiModelFile { Name = $"lora{versionId}.safetensors", Primary = true, SizeKB = 100, DownloadUrl = $"https://civitai.com/api/download/models/{versionId}" }],
        };
        _client.Setup(c => c.GetModelVersionAsync(versionId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _client.Setup(c => c.GetModelAsync(modelId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = modelId, Name = $"LoRA {modelId}", Type = CivitaiModelType.LORA, ModelVersions = [version] });
    }

    /// <summary>The browser hands its results to the UI thread; pump the headless dispatcher until the search is done.</summary>
    private static async Task Search(CivitaiBrowserViewModel vm, string text)
    {
        vm.SearchText = text;
        var search = vm.SearchCommand.ExecuteAsync(null);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!search.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        search.IsCompleted.Should().BeTrue("the search must finish");
        await search;
    }

    [Fact]
    public async Task ImageLink_ShowsRecipe_AndNotice()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        var (vm, _) = Create();

        await Search(vm, "https://civitai.red/images/7");

        vm.IsRecipeView.Should().BeTrue();
        vm.Recipe!.Rows.Should().ContainSingle().Which.Name.Should().Be("LoRA 10");
        vm.Results.Should().BeEmpty();
        vm.IsLinkLookup.Should().BeTrue();
        vm.LinkLookupNotice.Should().Be("Showing the image from your link and the resources it was made with. Filters are ignored. Clear the search box to go back to your filtered search.");
    }

    [Fact]
    public async Task NameSearchAfterRecipe_ClearsIt()
    {
        HasImage(7, 1);
        var (vm, _) = Create();
        await Search(vm, "https://civitai.red/images/7");

        await Search(vm, "anime");

        vm.Recipe.Should().BeNull();
        vm.IsRecipeView.Should().BeFalse();
    }

    [Fact]
    public async Task UnknownImage_ShowsMessage_NoRecipe()
    {
        _client.Setup(c => c.GetImagesAsync(It.IsAny<CivitaiImagesQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>());
        var (vm, _) = Create();

        await Search(vm, "https://civitai.com/images/7");

        vm.Recipe.Should().BeNull();
        vm.StatusMessage.Should().Be("Civitai has no public image with ID 7.");
        vm.LinkLookupNotice.Should().BeNull();
    }

    [Fact]
    public async Task RecipeAddToQueue_UsesTheQueue()
    {
        HasImage(7, 1, 100, 200);
        HasLora(100, 10);
        HasLora(200, 20);
        var (vm, queue) = Create();
        queue.Destination.IsDownloadToFolder = true;
        queue.Destination.CustomFolderPath = _dir;
        await Search(vm, "https://civitai.com/images/7");

        vm.Recipe!.AddToQueueCommand.Execute(null);

        queue.Jobs.Select(j => j.VersionId).Should().BeEquivalentTo([100, 200]);
    }

    /// <summary>A pasted link ignores every filter, Show NSFW included (owner, 2026-10-28): nothing is hidden or swapped.</summary>
    [Fact]
    public async Task ShowNsfwOff_HidesNothingInTheRecipe()
    {
        HasImage(7, 8, 100);
        HasLora(100, 10, new CivitaiModelImage { Url = "https://image.civitai.com/x/nsfw-preview.jpeg", NsfwLevel = 8, Type = "image" },
            new CivitaiModelImage { Url = "https://image.civitai.com/x/sfw-preview.jpeg", NsfwLevel = 1, Type = "image" });
        var (vm, _) = Create();
        vm.ShowNsfw = false;

        await Search(vm, "https://civitai.red/images/7");

        var card = vm.Recipe!.Rows.Single().Card!;
        card.PreviewUrl.Should().Contain("nsfw-preview", "the resource keeps its own first preview");

        vm.ShowNsfw = true;
        vm.ShowNsfw = false;

        card.PreviewUrl.Should().Contain("nsfw-preview", "flipping Show NSFW does not reach the recipe");
    }

    private RecipeStore Store => _store ??= new RecipeStore(Path.Combine(_dir, "recipes"), null);
    private RecipeStore? _store;

    private CivitaiBrowserViewModel CreateWithStore(RecipeStore? store = null)
    {
        var (vm, _) = Create();
        vm.RecipeStore = store ?? Store;
        // As in the app: the service downloads into the store's own root.
        vm.RecipeServiceFactory = () => new CivitaiImageRecipeService(
            _client.Object, new HttpClient(new PngHandler()), vm.RecipeStore.Root, null);
        return vm;
    }

    private string[] StagingEntries() =>
        Directory.Exists(Path.Combine(Store.Root, ".staging")) ? Directory.GetFileSystemEntries(Path.Combine(Store.Root, ".staging")) : [];

    [Fact]
    public async Task PasteTwice_OneOriginal_NoStaging()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        var vm = CreateWithStore();

        await Search(vm, "https://civitai.com/images/7");
        await Search(vm, "https://civitai.com/images/7");

        Directory.GetFiles(Store.FolderFor(7), "original.*").Should().ContainSingle();
        StagingEntries().Should().BeEmpty();
    }

    /// <summary>A second paste cancels the first; the first must not leave a folder with only its original (a broken card).</summary>
    [Fact]
    public async Task SupersededPaste_SavesOnlyTheSecond_AndLeavesNoStaging()
    {
        HasImage(7, 1, 100);
        HasImage(8, 1);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.Setup(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { reached.TrySetResult(); await gate.Task; return null; });
        var vm = CreateWithStore();

        vm.SearchText = "https://civitai.com/images/7";
        var first = vm.SearchCommand.ExecuteAsync(null);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Search(vm, "https://civitai.com/images/8");
        gate.SetResult();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!first.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        await first;

        Store.LoadAll().Select(s => s.ImageId).Should().Equal(8);
        StagingEntries().Should().BeEmpty();
    }

    [Fact]
    public async Task Paste_SavesTheRecipe()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        var vm = CreateWithStore();

        await Search(vm, "https://civitai.com/images/7");

        var saved = Store.LoadAll().Should().ContainSingle().Which;
        saved.ImageId.Should().Be(7);
        saved.Saved!.Recipe.Resources.Should().ContainSingle().Which.VersionId.Should().Be(100);
    }

    [Fact]
    public async Task PasteTwice_OneSavedRecipe()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        var vm = CreateWithStore();

        await Search(vm, "https://civitai.com/images/7");
        await Search(vm, "https://civitai.com/images/7");

        Store.LoadAll().Should().ContainSingle();
    }

    [Fact]
    public async Task RefreshFails_ShowsSavedCopy_NotMoved()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        await Search(CreateWithStore(), "https://civitai.com/images/7");
        var pastedAt = Store.TryLoad(7)!.Saved!.PastedAt;

        _client.Setup(c => c.GetImagesAsync(It.IsAny<CivitaiImagesQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>());
        var vm = CreateWithStore();
        await Search(vm, "https://civitai.com/images/7");

        vm.Recipe.Should().NotBeNull();
        vm.LinkLookupNotice.Should().StartWith("Could not refresh this recipe from Civitai (").And.EndWith("). Showing the saved copy.");
        Store.TryLoad(7)!.Saved!.PastedAt.Should().Be(pastedAt);
    }

    [Fact]
    public async Task NoStore_NoSavedCopyToShow()
    {
        _client.Setup(c => c.GetImagesAsync(It.IsAny<CivitaiImagesQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>());
        var (vm, _) = Create();

        await Search(vm, "https://civitai.com/images/7");

        vm.Recipe.Should().BeNull();
    }

    [Fact]
    public async Task SaveFails_StillShowsTheRecipe_AndSaysSo()
    {
        HasImage(7, 1, 100);
        HasLora(100, 10);
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        var vm = CreateWithStore(new RecipeStore(blocker, null));

        await Search(vm, "https://civitai.com/images/7");

        vm.Recipe.Should().NotBeNull();
        vm.StatusMessage.Should().StartWith("The recipe could not be saved: ");
    }

    [Fact]
    public void InstalledIndexChanged_Raised_WhenTheIndexIsReplaced()
    {
        var (vm, _) = Create();
        var raised = 0;
        ((IRecipeHost)vm).InstalledIndexChanged += (_, _) => raised++;
        var index = new CivitaiInstalledIndex(new HashSet<int> { 5 }, new HashSet<string>());

        vm.ApplyInstalledSnapshot(index);

        raised.Should().Be(1);
        ((IRecipeHost)vm).Installed.Should().BeSameAs(index);
    }

    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The Recipes tab can be opened before Browse Civitai: it reads the library itself, without a search.</summary>
    [Fact]
    public async Task EnsureInstalledLoaded_BeforeTheBrowserWasShown_ReadsTheLibraryOnce()
    {
        var (vm, _) = Create();
        var reads = 0;
        var index = new CivitaiInstalledIndex(new HashSet<int> { 5 }, new HashSet<string>());
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.InstalledIndexSource = async () => { Interlocked.Increment(ref reads); await release.Task; return index; };
        var raised = 0;
        IRecipeHost host = vm;
        host.InstalledIndexChanged += (_, _) => raised++;
        host.IsInstalledLoaded.Should().BeFalse();

        var first = host.EnsureInstalledLoadedAsync();
        var second = host.EnsureInstalledLoadedAsync();
        release.SetResult();
        PumpUntil(() => first.IsCompleted && second.IsCompleted);
        await Task.WhenAll(first, second);
        await host.EnsureInstalledLoadedAsync();

        reads.Should().Be(1);
        raised.Should().Be(1);
        host.IsInstalledLoaded.Should().BeTrue();
        host.Installed.Should().BeSameAs(index);
    }

    [Fact]
    public void DownloadBurst_RefreshesTheInstalledSetOnce_UntilDisposed()
    {
        var notifier = new FakeNotifier();
        var queue = new CivitaiDownloadQueue(null, null, null, null, persistPathOverride: Path.Combine(_dir, "queue.json"));
        var waitlist = new CivitaiWaitlist(null, null, persistPathOverride: Path.Combine(_dir, "waitlist.json"));
        var vm = new CivitaiBrowserViewModel(null, null, null, queue, waitlist, null, changeNotifier: notifier)
        {
            InstalledRefreshDelay = TimeSpan.FromMilliseconds(100),
        };
        var reads = 0;
        vm.InstalledIndexSource = () =>
        {
            Interlocked.Increment(ref reads);
            return Task.FromResult<CivitaiInstalledIndex?>(new CivitaiInstalledIndex(new HashSet<int> { 5 }, new HashSet<string>()));
        };

        for (var i = 0; i < 5; i++) notifier.NotifyModelDownloaded(i);
        PumpUntil(() => vm.InstalledRefreshAfterDownload.IsCompleted && ((IRecipeHost)vm).IsInstalledLoaded);

        reads.Should().Be(1);
        ((IRecipeHost)vm).IsInstalledLoaded.Should().BeTrue();

        vm.Dispose();
        notifier.SubscriberCount.Should().Be(0);
    }

    private sealed class FakeNotifier : DiffusionNexus.Domain.Services.ILibraryChangeNotifier
    {
        public event EventHandler<DiffusionNexus.Domain.Services.ModelDownloadedEventArgs>? ModelDownloaded;

        public int SubscriberCount => ModelDownloaded?.GetInvocationList().Length ?? 0;

        public void NotifyModelDownloaded(int modelId) =>
            ModelDownloaded?.Invoke(this, new DiffusionNexus.Domain.Services.ModelDownloadedEventArgs(modelId));
    }

    private sealed class PngHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(RecipeTestFiles.PngWithText(("parameters", "a cat\nSteps: 20"))),
            });
    }
}
