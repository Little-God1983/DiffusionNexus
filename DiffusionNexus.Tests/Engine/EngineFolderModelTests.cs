using System.Net;
using DiffusionNexus.UI.Services.Engine;
using FluentAssertions;

namespace DiffusionNexus.Tests.Engine;

public sealed class EngineFolderModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dn-folder-model-{Guid.NewGuid():N}");

    private static readonly byte[] Weights = Enumerable.Repeat((byte)7, 4096).ToArray();
    private static readonly byte[] Config = "{\"a\":1}"u8.ToArray();

    private static readonly EngineFolderModel Model = new(
        "Tiny-VL",
        "models/prompt_generator/Tiny-VL",
        "https://example.test/repo/resolve/abc",
        [new("config.json", Config.Length), new("model.safetensors", Weights.Length)]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private void Write(string file, byte[] bytes)
    {
        var path = Model.FilePath(_root, Model.Files.Single(f => f.Path == file));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    private sealed class FakeServer(Func<string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request.RequestUri.AbsolutePath));
        }
    }

    private static HttpResponseMessage Serve(string path)
    {
        var bytes = path.EndsWith("config.json", StringComparison.Ordinal) ? Config : Weights;
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            path.EndsWith(".json", StringComparison.Ordinal) ? "text/plain" : "application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    [Fact]
    public void MissingFiles_EmptyFolder_ListsEveryFile()
    {
        Model.MissingFiles(_root).Should().HaveCount(2);
        Model.IsComplete(_root).Should().BeFalse();
    }

    [Fact]
    public void MissingFiles_FolderWithOnlyTheSmallFiles_ListsTheWeights()
    {
        // The broken state seen on the owner's machine: a cut-off first-run download left the
        // config files and no weights, and the node never repairs it.
        Write("config.json", Config);

        Model.MissingFiles(_root).Select(f => f.Path).Should().Equal("model.safetensors");
    }

    [Fact]
    public void MissingFiles_WrongSize_CountsAsMissing()
    {
        Write("config.json", Config);
        Write("model.safetensors", Weights[..100]);

        Model.MissingFiles(_root).Select(f => f.Path).Should().Equal("model.safetensors");
    }

    [Fact]
    public void IsComplete_AllFilesAtTheirSize()
    {
        Write("config.json", Config);
        Write("model.safetensors", Weights);

        Model.IsComplete(_root).Should().BeTrue();
    }

    [Fact]
    public async Task Download_FetchesOnlyTheMissingFiles()
    {
        Write("config.json", Config);
        var server = new FakeServer(Serve);
        var sut = new EngineFolderModelDownloader(new HttpClient(server));

        var result = await sut.DownloadAsync(_root, Model, null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Downloaded.Should().Be(1);
        server.Requests.Should().Equal("/repo/resolve/abc/model.safetensors");
        Model.IsComplete(_root).Should().BeTrue();
    }

    [Fact]
    public async Task Download_ReplacesAWrongSizeFile()
    {
        Write("config.json", Config);
        Write("model.safetensors", Weights[..100]);
        var sut = new EngineFolderModelDownloader(new HttpClient(new FakeServer(Serve)));

        var result = await sut.DownloadAsync(_root, Model, null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        Model.IsComplete(_root).Should().BeTrue();
    }

    [Fact]
    public async Task Download_Complete_MakesNoRequest()
    {
        Write("config.json", Config);
        Write("model.safetensors", Weights);
        var server = new FakeServer(Serve);
        var sut = new EngineFolderModelDownloader(new HttpClient(server));

        var result = await sut.DownloadAsync(_root, Model, null, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        server.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Download_ServerError_IsReportedPerFile_AndLeavesTheModelIncomplete()
    {
        var sut = new EngineFolderModelDownloader(new HttpClient(new FakeServer(p =>
            p.EndsWith(".safetensors", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Serve(p))));

        var result = await sut.DownloadAsync(_root, Model, null, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Failures.Should().ContainSingle().Which.Should().StartWith("model.safetensors: ");
        Model.MissingFiles(_root).Select(f => f.Path).Should().Equal("model.safetensors");
    }

    [Fact]
    public async Task Download_ServedFileOfTheWrongSize_IsAFailure_AndRemoved()
    {
        var sut = new EngineFolderModelDownloader(new HttpClient(new FakeServer(p =>
            p.EndsWith(".safetensors", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Weights[..10]) }
                : Serve(p))));

        var result = await sut.DownloadAsync(_root, Model, null, CancellationToken.None);

        result.Failures.Should().ContainSingle().Which.Should().Contain("expected 4096");
        File.Exists(Model.FilePath(_root, Model.Files[1])).Should().BeFalse();
    }

    [Fact]
    public void Qwen3VL_PointsAtThePathTheNodeLoadsFrom()
    {
        var model = EngineFolderModels.Qwen3VL4BInstructFp8;

        model.RelativeFolder.Should().Be("models/prompt_generator/Qwen3-VL-4B-Instruct-FP8");
        model.Files.Should().Contain(f => f.Path == "model-00001-of-00002.safetensors" && f.Size == 5366863440);
        model.FileUrl(model.Files[0]).Should().StartWith("https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct-FP8/resolve/");
    }
}
