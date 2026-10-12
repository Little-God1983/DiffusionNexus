using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.UI.Services.Vision;
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
                [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
                [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
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

    private const string DescribeWorkflow = "Qwen3-VL-Describe.json";
    private const string OutpaintWorkflow = "Qwen-Image-2512-outpaint-nonVision.json";

    private sealed record QueuedJob(string Workflow, Dictionary<string, Action<JsonNode>> Overrides);

    /// <summary>A client recording every queued job; the describe job answers <paramref name="description"/>.</summary>
    private (Mock<IComfyUIWrapperService> Client, List<QueuedJob> Queued) ClientRecordingJobs(
        string description = "A quiet street.")
    {
        var queued = new List<QueuedJob>();
        var client = new Mock<IComfyUIWrapperService>();
        client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("up.png");
        client.Setup(c => c.GetNodeInputOptionsAsync("UnetLoaderGGUF", "unet_name", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "qwen-image-2512-Q8_0.gguf" });
        client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, Dictionary<string, Action<JsonNode>> o, CancellationToken _) =>
            {
                queued.Add(new QueuedJob(Path.GetFileName(path), o));
                return $"p{queued.Count}";
            });
        client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var result = new ComfyUIResult();
                if (queued[int.Parse(id[1..]) - 1].Workflow == DescribeWorkflow) result.Texts.Add(description);
                else result.Images.Add(new ComfyUIImage("out.png", "", "output", "http://test/view"));
                return result;
            });
        client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        return (client, queued);
    }

    private OutpaintingViewModel VisionSut(IComfyUIWrapperService client, IUnifiedLogger? logger = null) =>
        new(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
                [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
            }),
            unifiedLogger: logger);

    private static JsonNode AppliedDescribeNode(QueuedJob job)
    {
        var node = JsonNode.Parse("""{"inputs": {"mode": "", "seed": 0, "config_override": "", "unload_all_models": false, "user_prompt": ""}}""")!;
        job.Overrides["3"](node);
        return node;
    }

    private static string AppliedPrompt(QueuedJob job)
    {
        var node = JsonNode.Parse("""{"inputs": {"text": ""}}""")!;
        job.Overrides["5"](node);
        return node["inputs"]!["text"]!.GetValue<string>();
    }

    [Fact]
    public async Task VisionRun_HandsTheGgufNodeTheModelPathsTheCheckFound()
    {
        var (client, queued) = ClientRecordingJobs();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [QwenVlGguf.ModelName] = @"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf",
                [QwenVlGguf.ProjectorName] = @"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf",
            }));
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        vm.HasError.Should().BeFalse();
        var node = AppliedDescribeNode(queued.First(j => j.Workflow == DescribeWorkflow));
        var config = JsonNode.Parse(node["inputs"]!["config_override"]!.GetValue<string>())!;
        config["model_path"]!.GetValue<string>().Should().Be(@"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.Q6_K.gguf");
        config["mmproj_path"]!.GetValue<string>().Should().Be(@"D:\Models\Captioning\Qwen3-VL-8B-Abliterated-Caption-it.mmproj-f16.gguf");
        config["output_max_tokens"]!.GetValue<int>().Should().Be(400, "a looping description must not run to the node's 2048 default");
        node["inputs"]!["seed"]!.GetValue<long>().Should().BeInRange(0, uint.MaxValue, "the GGUF node's seed input is 32-bit");
    }

    // ── #616: describe in a job of its own, then outpaint with the description as the prompt ──

    [Fact]
    public async Task VisionRun_DescribesFirst_ThenOutpaintsWithTheDescriptionAsThePrompt()
    {
        var (client, queued) = ClientRecordingJobs("A quiet street.");
        var vm = VisionSut(client.Object);
        byte[]? image = null;
        vm.ResultReady += (_, bytes) => image = bytes;

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        queued.Select(j => j.Workflow).Should().Equal(DescribeWorkflow, OutpaintWorkflow);
        AppliedPrompt(queued[1]).Should().Be("A quiet street.");
        image.Should().NotBeNull();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task VisionRun_AsksForTheSurroundings_UnloadsComfyUIsModelsFirst_AndFreesQwen3VLAfterwards()
    {
        var (client, queued) = ClientRecordingJobs();

        await VisionSut(client.Object).ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        var node = AppliedDescribeNode(queued[0]);
        node["inputs"]!["user_prompt"]!.GetValue<string>().Should().Be(OutpaintingViewModel.SurroundingsPrompt);
        node["inputs"]!["unload_all_models"]!.GetValue<bool>().Should().BeTrue(
            "llama.cpp allocates outside ComfyUI's memory manager; the last outpaint's Qwen-Image would not make room (#616's OOM)");
        node["inputs"]!["mode"]!.GetValue<string>().Should().Be("direct_clean", "the outpaint needs the GPU next");
    }

    // PR #623 review round 2: the describe step had no progress value, so the bar sat at the upload's 10%.
    [Fact]
    public async Task VisionRun_WhileDescribing_TheProgressBarHasMovedPastTheUpload()
    {
        var (client, _) = ClientRecordingJobs();
        var vm = VisionSut(client.Object);
        double? atDescribe = null;
        client.Setup(c => c.WaitForCompletionAsync("p1", It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => atDescribe = vm.OutpaintProgress)
            .Returns(Task.CompletedTask);

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        atDescribe.Should().Be(12);
    }

    [Fact]
    public async Task VisionRun_FailedDescription_GeneratesNothing_AndNamesTheCause()
    {
        var (client, queued) = ClientRecordingJobs("❌ Inference failed:\nCUDA out of memory\nCheck console for details.");
        var logger = new Mock<IUnifiedLogger>();
        var vm = VisionSut(client.Object, logger.Object);
        var messages = new List<string?>();
        vm.StatusMessageChanged += (_, m) => messages.Add(m);
        var resultShown = false;
        vm.ResultReady += (_, _) => resultShown = true;

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        queued.Select(j => j.Workflow).Should().Equal([DescribeWorkflow], "nothing is generated from an error text");
        resultShown.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ProgressDisplayText.Should().Be("Qwen3-VL could not describe the image – see the Unified Console");
        messages.Should().Contain("Qwen3-VL could not describe the image: CUDA out of memory");
        messages.Should().NotContain("Outpainting completed successfully.");
        logger.Verify(l => l.Error(LogCategory.General, "Outpaint",
            "Qwen3-VL could not describe the image: CUDA out of memory", It.IsAny<Exception?>()), Times.Once);
        logger.Verify(l => l.Info(It.IsAny<LogCategory>(), It.IsAny<string>(),
            It.Is<string>(m => m.StartsWith("Vision description")), It.IsAny<string?>()), Times.Never,
            "the error is not logged as if it were a description");
    }

    [Fact]
    public async Task VisionRun_DescriberNodeFails_FreesQwen3VL_NamesTheNode_AndDoesNotOutpaint()
    {
        var (client, queued) = ClientRecordingJobs();
        client.Setup(c => c.WaitForCompletionAsync("p1", It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUIExecutionException("SimpleQwenVLggufV2", "llama_decode returned -3"));
        var vm = VisionSut(client.Object);
        string? statusAtFree = null;
        client.Setup(c => c.WaitForCompletionAsync("p2", It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Callback(() => statusAtFree = vm.Status)
            .Returns(Task.CompletedTask);

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        // The crashed node may hold Qwen3-VL, and the user's next run may be an Inpaint with no describe job
        // to unload it (review round 2), so a direct_clean job frees it, under a status that says so.
        queued.Select(j => j.Workflow).Should().Equal([DescribeWorkflow, DescribeWorkflow], "no outpaint runs");
        AppliedDescribeNode(queued[1])["inputs"]!["mode"]!.GetValue<string>().Should().Be("direct_clean");
        statusAtFree.Should().Be("Freeing Qwen3-VL after the failure...");
        vm.HasError.Should().BeTrue();
        vm.ProgressDisplayText.Should().Be("Failed in the ComfyUI node SimpleQwenVLggufV2 – see the Unified Console");
    }

    [Fact]
    public async Task PromptRun_DoesNotDescribe()
    {
        var (client, queued) = ClientRecordingJobs();
        var vm = VisionSut(client.Object);
        vm.PositivePrompt = "a beach";

        await vm.ProcessOutpaintAsync(TempImage(), useVision: false, 64, 0, 64, 0);

        queued.Select(j => j.Workflow).Should().Equal(OutpaintWorkflow);
        AppliedPrompt(queued[0]).Should().Be("a beach");
    }

    [Fact]
    public async Task VisionRun_WithoutTheModelPaths_StopsBeforeQueuing_WithTheInstallHint()
    {
        var (client, queued) = ClientRecordingJobs();
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
        queued.Should().BeEmpty("nothing was queued");
    }

    // Review: Generate (Vision) is clickable before the first check finished, when the paths are not known yet.
    [Fact]
    public async Task VisionRun_BeforeTheFirstCheckFinished_ChecksForThePaths_InsteadOfReportingThemMissing()
    {
        var (client, queued) = ClientRecordingJobs();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
                [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
            }));

        await vm.ProcessOutpaintAsync(TempImage(), useVision: true, 64, 0, 64, 0);

        vm.HasError.Should().BeFalse();
        queued.Select(j => j.Workflow).Should().Equal(DescribeWorkflow, OutpaintWorkflow);
    }

    // ── Owner smoke: the scale node got largest_size 0 (the editor's ImageWidth was 0) and the result was 296x80 ──

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PadNode_ReadsTheLoadedImage_SoNoSizeFromTheEditorIsInvolved(bool useVision)
    {
        var (client, queued) = ClientRecordingJobs();
        var vm = new OutpaintingViewModel(() => true, _ => { },
            InpaintingViewModelGGUFResolutionTests.Provider(client.Object, ComfyUiServerMode.Engine),
            ReadinessWithPaths(new Dictionary<string, string>
            {
                [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
                [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
            }));
        await vm.VisionReadiness.CheckReadinessAsync();
        vm.PositivePrompt = "a beach";

        await vm.ProcessOutpaintAsync(TempImage(), useVision, 64, 0, 64, 0);

        var pad = JsonNode.Parse("""{"inputs": {"image": ["17", 0], "left": 0, "top": 0, "right": 0, "bottom": 0}}""")!;
        queued.Last().Overrides["26"](pad);
        pad["inputs"]!["image"]!.ToJsonString().Should().Be("""["16",0]""", "the pad node reads LoadImage, not the scale node");
        pad["inputs"]!["right"]!.GetValue<int>().Should().Be(64);
        queued.Last().Overrides.Should().NotContainKey("17", "nothing feeds from the scale node any more, so it does not run");
    }
}
