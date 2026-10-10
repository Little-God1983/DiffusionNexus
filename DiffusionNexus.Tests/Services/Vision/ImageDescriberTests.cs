using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services.Vision;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services.Vision;

public class ImageDescriberTests
{
    private readonly Mock<IComfyUIWrapperService> _client = new();
    private readonly List<Dictionary<string, Action<JsonNode>>> _queued = [];

    public ImageDescriberTests()
    {
        _client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Dictionary<string, Action<JsonNode>> o, CancellationToken _) => { _queued.Add(o); return $"p{_queued.Count}"; });
        _client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { var r = new ComfyUIResult(); r.Texts.Add("  A red fox in fresh snow.  "); return r; });
    }

    private ImageDescriber Sut() => new(_client.Object, @"D:\m\model.gguf", @"D:\m\mmproj.gguf");

    internal static (string Image, string Mode, long Seed, JsonNode Config) Applied(Dictionary<string, Action<JsonNode>> o)
    {
        var load = JsonNode.Parse("""{"inputs":{"image":""}}""")!;
        o["1"](load);
        var node = JsonNode.Parse("""{"inputs":{"mode":"","seed":0,"config_override":""}}""")!;
        o["3"](node);
        return (load["inputs"]!["image"]!.GetValue<string>(), node["inputs"]!["mode"]!.GetValue<string>(),
            node["inputs"]!["seed"]!.GetValue<long>(), JsonNode.Parse(node["inputs"]!["config_override"]!.GetValue<string>())!);
    }

    [Theory]
    [InlineData(true, "keep_vram")]
    [InlineData(false, "direct_clean")]
    public async Task Describe_SetsTheImage_TheMode_AndTheModelPaths(bool keepLoaded, string mode)
    {
        var text = await Sut().DescribeAsync("up-1.png", keepLoaded, CancellationToken.None);

        text.Should().Be("A red fox in fresh snow.");
        var applied = Applied(_queued.Single());
        applied.Image.Should().Be("up-1.png");
        applied.Mode.Should().Be(mode);
        applied.Seed.Should().BeInRange(0, uint.MaxValue);
        applied.Config["model_path"]!.GetValue<string>().Should().Be(@"D:\m\model.gguf");
        applied.Config["mmproj_path"]!.GetValue<string>().Should().Be(@"D:\m\mmproj.gguf");
    }

    [Fact]
    public async Task Describe_NoText_IsNull()
    {
        _client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(new ComfyUIResult());

        (await Sut().DescribeAsync("up.png", keepLoaded: true, CancellationToken.None)).Should().BeNull();
    }

    // The free-up job reuses the loaded model (same config) and unloads it after answering.
    [Fact]
    public async Task Free_QueuesOneDirectCleanJob_WithTheSameConfig()
    {
        var sut = Sut();
        await sut.DescribeAsync("up-1.png", keepLoaded: true, CancellationToken.None);

        await sut.FreeAsync("up-1.png");

        _queued.Should().HaveCount(2);
        var free = Applied(_queued[1]);
        free.Mode.Should().Be("direct_clean");
        free.Config.ToJsonString().Should().Be(Applied(_queued[0]).Config.ToJsonString());
    }

    [Fact]
    public async Task Free_NeverThrows()
    {
        _client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        await Sut().Invoking(s => s.FreeAsync("up.png")).Should().NotThrowAsync();
    }
}
