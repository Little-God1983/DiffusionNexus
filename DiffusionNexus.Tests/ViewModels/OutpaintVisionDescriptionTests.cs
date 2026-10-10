using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
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
public class OutpaintVisionDescriptionTests : IDisposable
{
    private readonly List<(string Level, string Message)> _logged = [];
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
            File.Delete(path);
    }

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

        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [OutpaintingViewModel.VisionModelName] = @"D:\m\model.gguf",
                [OutpaintingViewModel.VisionProjectorName] = @"D:\m\mmproj.gguf",
            }),
            unifiedLogger: logger.Object);
        vm.VisionReadiness.CheckReadinessAsync().GetAwaiter().GetResult();
        return vm;
    }

    private string TempImage()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dn-vision-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        _tempFiles.Add(path);
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

    // ── #607: the GGUF node gets the model files as paths the readiness check found ──

    private static IFeatureReadinessService ReadinessWithPaths(IReadOnlyDictionary<string, string> paths)
    {
        var readiness = new Mock<IFeatureReadinessService>();
        readiness.Setup(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Feature f, CancellationToken _) => new FeatureReadinessResult
            {
                Feature = f, Backend = BackendKind.Engine, ActiveBackendName = "Diffusion Nexus Engine",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = [], ModelPaths = paths
            });
        return readiness.Object;
    }

    private (Mock<IComfyUIWrapperService> Client, Func<Dictionary<string, Action<JsonNode>>?> Overrides) ClientCapturingOverrides()
    {
        Dictionary<string, Action<JsonNode>>? captured = null;
        var client = new Mock<IComfyUIWrapperService>();
        client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("up.png");
        client.Setup(c => c.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "qwen-image-2512-Q8_0.gguf" });
        client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, Dictionary<string, Action<JsonNode>> o, CancellationToken _) => { captured = o; return "p1"; });
        client.Setup(c => c.WaitForCompletionAsync("p1", It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        client.Setup(c => c.GetResultAsync("p1", It.IsAny<CancellationToken>())).ReturnsAsync(Result("A quiet street."));
        client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        return (client, () => captured);
    }

    [Fact]
    public async Task VisionRun_HandsTheGgufNodeTheModelPathsTheCheckFound()
    {
        var (client, overrides) = ClientCapturingOverrides();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [OutpaintingViewModel.VisionModelName] = @"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf",
                [OutpaintingViewModel.VisionProjectorName] = @"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            }));
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        vm.HasError.Should().BeFalse();
        var node = JsonNode.Parse("""{"inputs": {"seed": 0, "config_override": ""}}""")!;
        overrides()!["256"](node);
        var config = JsonNode.Parse(node["inputs"]!["config_override"]!.GetValue<string>())!;
        config["model_path"]!.GetValue<string>().Should().Be(@"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf");
        config["mmproj_path"]!.GetValue<string>().Should().Be(@"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf");
        config["output_max_tokens"]!.GetValue<int>().Should().Be(400, "a looping description must not run to the node's 2048 default");
        node["inputs"]!["seed"]!.GetValue<long>().Should().BeInRange(0, uint.MaxValue, "the GGUF node's seed input is 32-bit");
        overrides()!.Should().NotContainKey("5", "the Vision workflow wires the prompt from the node");
    }

    [Fact]
    public async Task VisionRun_WithoutTheModelPaths_StopsBeforeQueuing_WithTheInstallHint()
    {
        var (client, overrides) = ClientCapturingOverrides();
        var messages = new List<string?>();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>()));
        vm.StatusMessageChanged += (_, m) => messages.Add(m);
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        vm.HasError.Should().BeTrue();
        vm.ProgressDisplayText.Should().Be("Qwen3-VL model files not found");
        messages.Should().Contain(m => m != null && m.Contains("Install Outpaint Vision"));
        overrides().Should().BeNull("nothing was queued");
    }

    // Review: Generate (Vision) is clickable before the first check finished, when the paths are not known yet.
    [Fact]
    public async Task VisionRun_BeforeTheFirstCheckFinished_ChecksForThePaths_InsteadOfReportingThemMissing()
    {
        var (client, overrides) = ClientCapturingOverrides();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [OutpaintingViewModel.VisionModelName] = @"D:\m\model.gguf",
                [OutpaintingViewModel.VisionProjectorName] = @"D:\m\mmproj.gguf",
            }));

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        vm.HasError.Should().BeFalse();
        overrides()!.Should().ContainKey("256");
    }

    [Fact]
    public void BuildVisionConfig_IsJsonWithEscapedWindowsPaths()
    {
        var json = OutpaintingViewModel.BuildVisionConfig(@"C:\m\model.gguf", @"C:\m\mmproj.gguf");

        var config = JsonNode.Parse(json)!;
        config["model_path"]!.GetValue<string>().Should().Be(@"C:\m\model.gguf");
        config["chat_handler"]!.GetValue<string>().Should().Be("qwen3");
        config["temperature"]!.GetValue<double>().Should().Be(0.3);
    }

    // ── Owner smoke: the scale node got largest_size 0 (the editor's ImageWidth was 0) and the result was 296x80 ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PadNode_ReadsTheLoadedImage_SoNoSizeFromTheEditorIsInvolved(bool useVision)
    {
        var (client, overrides) = ClientCapturingOverrides();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [OutpaintingViewModel.VisionModelName] = @"D:\m\model.gguf",
                [OutpaintingViewModel.VisionProjectorName] = @"D:\m\mmproj.gguf",
            }));
        await vm.VisionReadiness.CheckReadinessAsync();
        vm.PositivePrompt = "a beach";

        await vm.ProcessOutpaintAsync(TempImage(), useVision, 64, 0, 64, 0);

        var pad = JsonNode.Parse("""{"inputs": {"image": ["17", 0], "left": 0, "top": 0, "right": 0, "bottom": 0}}""")!;
        overrides()!["26"](pad);
        pad["inputs"]!["image"]!.ToJsonString().Should().Be("""["16",0]""", "the pad node reads LoadImage, not the scale node");
        pad["inputs"]!["right"]!.GetValue<int>().Should().Be(64);
        overrides()!.Should().NotContainKey("17", "nothing feeds from the scale node any more, so it does not run");
    }
}
