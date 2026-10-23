using System.Text.Json.Nodes;
using DiffusionNexus.UI.Services.Vision;
using FluentAssertions;

namespace DiffusionNexus.Tests.Services.Vision;

public class QwenVlGgufTests
{
    [Fact]
    public void BuildConfig_IsJsonWithEscapedWindowsPaths_AndABoundedAnswer()
    {
        var config = JsonNode.Parse(QwenVlGguf.BuildConfig(@"C:\m\model.gguf", @"C:\m\mmproj.gguf"))!;

        config["model_path"]!.GetValue<string>().Should().Be(@"C:\m\model.gguf");
        config["mmproj_path"]!.GetValue<string>().Should().Be(@"C:\m\mmproj.gguf");
        config["chat_handler"]!.GetValue<string>().Should().Be("qwen3");
        config["output_max_tokens"]!.GetValue<int>().Should().Be(400);
        config["temperature"]!.GetValue<double>().Should().Be(0.3);
    }

    [Fact]
    public void TryGetPaths_NeedsBothFiles()
    {
        var both = new Dictionary<string, string> { [QwenVlGguf.ModelName] = "m.gguf", [QwenVlGguf.ProjectorName] = "p.gguf" };
        QwenVlGguf.TryGetPaths(both, out var m, out var p).Should().BeTrue();
        (m, p).Should().Be(("m.gguf", "p.gguf"));

        QwenVlGguf.TryGetPaths(new Dictionary<string, string> { [QwenVlGguf.ModelName] = "m.gguf" }, out _, out _)
            .Should().BeFalse("the projector is missing");
    }

    [Theory]
    [InlineData(5L, 5L)]
    [InlineData(0x1_0000_0005L, 5L)]
    [InlineData(long.MaxValue, 0xFFFFFFFFL)]
    public void Seed_FitsTheNodes32BitInput(long seed, long expected) =>
        QwenVlGguf.Seed(seed).Should().Be(expected);
}
