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

    private sealed class PngHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(RecipeTestFiles.PngWithText(("parameters", "a cat\nSteps: 20"))),
            });
    }
}
