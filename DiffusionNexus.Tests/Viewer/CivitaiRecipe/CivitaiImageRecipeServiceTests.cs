using System.Net;
using DiffusionNexus.Civitai;
using DiffusionNexus.Civitai.Models;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: one pasted image link → the image (with Civitai's rating), its resources from the API, and
/// the generation data from the original file, downloaded once into a cache.
/// </summary>
public sealed class CivitaiImageRecipeServiceTests : IDisposable
{
    private readonly Mock<ICivitaiClient> _client = new();
    private readonly string _cache = Directory.CreateTempSubdirectory("dn-recipe-cache").FullName;
    private readonly StubHandler _http = new();

    public void Dispose()
    {
        try { Directory.Delete(_cache, recursive: true); } catch { /* best-effort */ }
    }

    private CivitaiImageRecipeService Service() => new(_client.Object, new HttpClient(_http), _cache, null);

    private void HasImage(long id, int? browsingLevel = 1, string type = "image", params int[] versionIds) =>
        _client.Setup(c => c.GetImagesAsync(It.Is<CivitaiImagesQuery>(q => q.ImageId == id), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>
            {
                Items =
                [
                    new CivitaiModelImage
                    {
                        Id = id, Url = $"https://image.civitai.com/x/original=true/{id}.jpeg", Type = type,
                        BrowsingLevel = browsingLevel, Width = 1256, Height = 1672, Username = "purplelady",
                        BaseModel = "Krea 2", ModelVersionIds = versionIds,
                    },
                ],
            });

    private void HasLora(int versionId, int modelId, string file, string? autoV2 = null)
    {
        var version = new CivitaiModelVersion
        {
            Id = versionId, ModelId = modelId, Name = $"v{versionId}", BaseModel = "Krea 2",
            Files = [new CivitaiModelFile { Name = file, Primary = true, Hashes = new CivitaiFileHashes { AutoV2 = autoV2 } }],
        };
        _client.Setup(c => c.GetModelVersionAsync(versionId, It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(version);
        _client.Setup(c => c.GetModelAsync(modelId, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModel { Id = modelId, Name = $"Model {modelId}", Type = CivitaiModelType.LORA, ModelVersions = [version] });
    }

    [Fact]
    public async Task LoadAsync_AsksForEveryRating()
    {
        await Service().LoadAsync(147036988, null, default);

        _client.Verify(c => c.GetImagesAsync(
            It.Is<CivitaiImagesQuery>(q => q.ImageId == 147036988 && q.NsfwLevel == "X"),
            It.IsAny<string?>(), It.IsAny<CancellationToken>()));
    }

    [Fact]
    public async Task UnknownImage_GivesMessageAndNoRecipe()
    {
        _client.Setup(c => c.GetImagesAsync(It.IsAny<CivitaiImagesQuery>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiPagedResponse<CivitaiModelImage>());

        var result = await Service().LoadAsync(5, null, default);

        result.Recipe.Should().BeNull();
        result.Message.Should().Be("Civitai has no public image with ID 5.");
    }

    [Fact]
    public async Task NsfwImage_IsNsfwWithCivitaiRedLink()
    {
        HasImage(7, browsingLevel: 8);
        _http.Respond = _ => Png(("parameters", "a\nSteps: 1"));

        var recipe = (await Service().LoadAsync(7, null, default)).Recipe!;

        recipe.IsSafe.Should().BeFalse();
        recipe.RatingLabel.Should().Be("X");
        recipe.CivitaiUrl.Should().Be("https://civitai.red/images/7");
    }

    [Fact]
    public async Task SafeImage_IsSfwWithCivitaiComLink()
    {
        HasImage(8, browsingLevel: 2);
        _http.Respond = _ => Png(("parameters", "a\nSteps: 1"));

        var recipe = (await Service().LoadAsync(8, null, default)).Recipe!;

        recipe.IsSafe.Should().BeTrue();
        recipe.RatingLabel.Should().Be("PG-13");
        recipe.CivitaiUrl.Should().Be("https://civitai.com/images/8");
    }

    [Fact]
    public async Task OneResourceFailing_KeepsTheOthers()
    {
        HasImage(9, 1, "image", 100, 200);
        _client.Setup(c => c.GetModelVersionAsync(100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("429 Too Many Requests"));
        HasLora(200, 20, "b.safetensors");
        _http.Respond = _ => Png();

        var recipe = (await Service().LoadAsync(9, null, default)).Recipe!;

        recipe.Resources.Select(r => r.VersionId).Should().Equal(100, 200);
        recipe.Resources[0].LoadError.Should().Be("429 Too Many Requests");
        recipe.Resources[0].Model.Should().BeNull();
        recipe.Resources[1].Model!.Id.Should().Be(20);
        recipe.Resources[1].LoadError.Should().BeNull();
    }

    [Fact]
    public async Task MissingVersionAndGoneModel_AreSaidPerRow()
    {
        HasImage(10, 1, "image", 100, 200);
        _client.Setup(c => c.GetModelVersionAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CivitaiModelVersion { Id = 200, ModelId = 20, Name = "v" });
        _http.Respond = _ => Png();

        var recipe = (await Service().LoadAsync(10, null, default)).Recipe!;

        recipe.Resources[0].LoadError.Should().Be("not found on Civitai");
        recipe.Resources[1].LoadError.Should().Be("model no longer available on Civitai");
        recipe.Resources[1].Version!.Id.Should().Be(200);
    }

    [Fact]
    public async Task VideoPost_IsNotDownloaded_AndHasNoData()
    {
        HasImage(11, 1, "video");

        var recipe = (await Service().LoadAsync(11, null, default)).Recipe!;

        _http.Calls.Should().Be(0);
        recipe.Data.Source.Should().Be(RecipeDataSource.None);
        recipe.Data.Format.Should().Be(RecipeFileFormat.Video);
        recipe.OriginalFilePath.Should().BeNull();
    }

    [Fact]
    public async Task PngServedUnderJpegUrl_IsCachedAsPng_AndReusedNextTime()
    {
        HasImage(12);
        _http.Respond = _ => Png(("parameters", "a cat\nSteps: 20"));

        var first = (await Service().LoadAsync(12, null, default)).Recipe!;
        var second = (await Service().LoadAsync(12, null, default)).Recipe!;

        first.OriginalFilePath.Should().Be(Path.Combine(_cache, "12.png"));
        second.OriginalFilePath.Should().Be(first.OriginalFilePath);
        second.Data.Prompt.Should().Be("a cat");
        _http.Calls.Should().Be(1);
    }

    [Fact]
    public async Task DownloadFailure_StillListsResources()
    {
        HasImage(13, 1, "image", 100, 200);
        HasLora(100, 10, "a.safetensors");
        HasLora(200, 20, "b.safetensors");
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);

        var recipe = (await Service().LoadAsync(13, null, default)).Recipe!;

        recipe.Data.ReadError.Should().StartWith("Could not download the original image file");
        recipe.Data.Source.Should().Be(RecipeDataSource.None);
        recipe.OriginalFilePath.Should().BeNull();
        recipe.Resources.Should().HaveCount(2);
        Directory.GetFiles(_cache).Should().BeEmpty("a failed download leaves nothing half-written in the cache");
    }

    [Fact]
    public async Task UnknownFormat_IsNotCached_AndIsSaidPlainly()
    {
        HasImage(15, 1, "image", 100);
        HasLora(100, 10, "a.safetensors");
        _http.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>challenge</html>") };

        var recipe = (await Service().LoadAsync(15, null, default)).Recipe!;
        await Service().LoadAsync(15, null, default);

        recipe.Data.ReadError.Should().Be("The file Civitai served for this image is not a PNG, JPEG or WebP image.");
        recipe.OriginalFilePath.Should().BeNull();
        recipe.Resources.Should().ContainSingle();
        Directory.GetFiles(_cache).Should().BeEmpty();
        _http.Calls.Should().Be(2, "nothing was cached, so the next paste tries again");
    }

    [Fact]
    public async Task TwoLoadsOfTheSameImageAtOnce_BothRead()
    {
        // Paste + Enter starts the debounced search and the command: two loads of one image.
        HasImage(16);
        var release = new TaskCompletionSource();
        var slow = new GatedHandler(release.Task, () => Png(("parameters", "a cat\nSteps: 20")));
        var service = new CivitaiImageRecipeService(_client.Object, new HttpClient(slow), _cache, null);

        var first = service.LoadAsync(16, null, default);
        var second = service.LoadAsync(16, null, default);
        await slow.BothStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        release.SetResult();
        var results = await Task.WhenAll(first, second);

        results.Should().AllSatisfy(r => r.Recipe!.Data.Prompt.Should().Be("a cat"));
    }

    private sealed class GatedHandler(Task gate, Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        private int _started;
        public TaskCompletionSource BothStarted { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _started) == 2) BothStarted.SetResult();
            await gate;
            return respond();
        }
    }

    [Fact]
    public async Task WeightsAreMatchedOntoResources()
    {
        HasImage(14, 1, "image", 100);
        HasLora(100, 10, "renamed.safetensors", autoV2: "ABC1234567");
        _http.Respond = _ => Png(("parameters", "a cat <lora:foo:0.7> <lora:private:0.3>\nSteps: 20, Lora hashes: \"foo: abc1234567, private: 9999999999\""));

        var recipe = (await Service().LoadAsync(14, null, default)).Recipe!;

        recipe.Resources[0].Use!.Weight.Should().Be(0.7);
        recipe.UnlinkedLoras.Should().ContainSingle().Which.Name.Should().Be("private");
    }

    #region Civitai site data (API key)

    // Shape of image.getGenerationData as Civitai answered it on 2026-10-28 (trimmed).
    private const string SiteData = """
        {"result":{"data":{"json":{"type":"image","onSite":false,
          "meta":{"prompt":"a fox in snow","negativePrompt":"blurry","cfgScale":5,"steps":20,"sampler":"Euler a","seed":7,"Model":"sdxl"},
          "resources":[
            {"imageId":20,"modelVersionId":100,"strength":0.7,"modelId":10,"modelName":"Foo","modelType":"LORA","versionName":"v1"},
            {"imageId":20,"modelVersionId":900,"strength":null,"modelId":90,"modelName":"Ckpt","modelType":"Checkpoint","versionName":"v1"}]}}}}
        """;

    private static bool IsSiteCall(HttpRequestMessage r) => r.RequestUri!.AbsolutePath.StartsWith("/api/trpc/image.getGenerationData");

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task WithApiKey_SiteResourcesFillAnEmptyPublicList_WithTheirWeights()
    {
        // Image 146878246 (2026-10-28): public modelVersionIds = [], the page's list comes from this call.
        HasImage(20);
        HasLora(100, 10, "foo.safetensors");
        HasLora(900, 90, "ckpt.safetensors");
        _http.Respond = r => IsSiteCall(r) ? Json(SiteData) : Png();

        var recipe = (await Service().LoadAsync(20, "secret-key", default)).Recipe!;

        recipe.Resources.Select(r => r.VersionId).Should().Equal(100, 900);
        recipe.Resources[0].Use!.Weight.Should().Be(0.7);
        recipe.Resources[1].Use.Should().BeNull("a checkpoint has no strength");
        var site = _http.Requests.Single(IsSiteCall);
        site.Headers.Authorization!.ToString().Should().Be("Bearer secret-key");
        Uri.UnescapeDataString(site.RequestUri!.Query).Should().Be("?input={\"json\":{\"id\":20}}");
        recipe.HasApiKey.Should().BeTrue();
    }

    [Fact]
    public async Task WithApiKey_SiteMetaFillsAFileWithoutData()
    {
        HasImage(21);
        _http.Respond = r => IsSiteCall(r) ? Json(SiteData) : Png(("Software", "Topaz Gigapixel 1.3.0"));

        var data = (await Service().LoadAsync(21, "k", default)).Recipe!.Data;

        data.Source.Should().Be(RecipeDataSource.CivitaiSite);
        data.SourceNote.Should().Be("Read from Civitai's data for this image (the file itself carries none)");
        data.Prompt.Should().Be("a fox in snow");
        data.NegativePrompt.Should().Be("blurry");
        data.Steps.Should().Be(20);
        data.Cfg.Should().Be(5);
        data.Seed.Should().Be(7);
        data.Sampler.Should().Be("Euler a");
        data.Checkpoint.Should().Be("sdxl");
    }

    [Fact]
    public async Task FileDataAndWeightsWinOverTheSite()
    {
        HasImage(22, 1, "image", 100);
        HasLora(100, 10, "foo.safetensors");
        _http.Respond = r => IsSiteCall(r) ? Json(SiteData) : Png(("parameters", "a cat <lora:foo:0.4>\nSteps: 30"));

        var recipe = (await Service().LoadAsync(22, "k", default)).Recipe!;

        recipe.Data.Source.Should().Be(RecipeDataSource.SettingsText);
        recipe.Data.Prompt.Should().Be("a cat");
        recipe.Resources.Single(r => r.VersionId == 100).Use!.Weight.Should().Be(0.4);
    }

    [Fact]
    public async Task WithoutApiKey_TheSiteIsNotAsked()
    {
        HasImage(23);
        _http.Respond = r => IsSiteCall(r) ? Json(SiteData) : Png();

        var recipe = (await Service().LoadAsync(23, null, default)).Recipe!;

        _http.Requests.Should().NotContain(r => IsSiteCall(r));
        recipe.HasApiKey.Should().BeFalse();
        recipe.Resources.Should().BeEmpty();
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "{}")]
    [InlineData(HttpStatusCode.OK, "<html>changed</html>")]
    [InlineData(HttpStatusCode.OK, "{\"result\":{\"data\":{\"json\":{\"resources\":\"not a list\",\"meta\":7}}}}")]
    public async Task SiteFailure_KeepsThePublicRecipe(HttpStatusCode status, string body)
    {
        HasImage(24, 1, "image", 100);
        HasLora(100, 10, "foo.safetensors");
        _http.Respond = r => IsSiteCall(r)
            ? new HttpResponseMessage(status) { Content = new StringContent(body) }
            : Png(("parameters", "a cat\nSteps: 30"));

        var recipe = (await Service().LoadAsync(24, "k", default)).Recipe!;

        recipe.Resources.Select(r => r.VersionId).Should().Equal(100);
        recipe.Data.Prompt.Should().Be("a cat");
    }

    #endregion

    private static HttpResponseMessage Png(params (string Key, string Value)[] texts) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(RecipeTestFiles.PngWithText(texts)) };

    private sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public int Calls { get; private set; }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            lock (Requests) Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }
}
