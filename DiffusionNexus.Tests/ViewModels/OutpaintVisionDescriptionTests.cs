using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// #607: after a Vision outpaint the Unified Console shows what Qwen3-VL wrote, so the user can see
/// the prompt the result came from.
/// </summary>
public class OutpaintVisionDescriptionTests
{
    private readonly List<(string Level, string Message)> _logged = [];

    private OutpaintingViewModel Sut(ComfyUIResult result)
    {
        var client = new Mock<IComfyUIWrapperService>();
        client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("up.png");
        client.Setup(c => c.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "qwen-image-2512-Q8_0.gguf" });
        client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("p1");
        client.Setup(c => c.WaitForCompletionAsync("p1", It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        client.Setup(c => c.GetResultAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(result);
        client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);

        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(It.IsAny<LogCategory>(), "Outpaint", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => _logged.Add(("Info", m)));
        logger.Setup(l => l.Warn(It.IsAny<LogCategory>(), "Outpaint", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => _logged.Add(("Warn", m)));

        return new OutpaintingViewModel(() => true, () => 512, () => 512, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            unifiedLogger: logger.Object);
    }

    private static string TempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-vision-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    private static ComfyUIResult Result(params string[] texts)
    {
        var result = new ComfyUIResult();
        result.Texts.AddRange(texts);
        result.Images.Add(new ComfyUIImage("out.png", "", "output", "http://test/view"));
        return result;
    }

    [Fact]
    public async Task VisionRun_LogsTheDescription()
    {
        var vm = Sut(Result("A serene garden at dusk."));

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        _logged.Should().Contain(("Info", "Vision description: A serene garden at dusk."));
    }

    [Fact]
    public async Task VisionRun_WithoutText_WarnsThatNoDescriptionCameBack()
    {
        var vm = Sut(Result());

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        _logged.Should().Contain(("Warn", "Vision returned no description."));
    }

    [Fact]
    public async Task PromptRun_LogsNoDescription()
    {
        var vm = Sut(Result("left over text"));
        vm.PositivePrompt = "a beach";

        await vm.ProcessOutpaintAsync(TempImage(), useVision: false, 64, 0, 64, 0);

        _logged.Should().NotContain(e => e.Message.StartsWith("Vision"));
    }
}
