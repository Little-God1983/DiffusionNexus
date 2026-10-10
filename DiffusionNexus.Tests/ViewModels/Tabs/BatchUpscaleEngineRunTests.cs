using System.Net;
using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Enums;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.Domain.Services.UnifiedLogging;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Vision;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>#608: Batch Upscale runs on the server chosen in Settings, through IComfyUiClientProvider.</summary>
public class BatchUpscaleEngineRunTests : IDisposable
{
    protected readonly Mock<IComfyUIWrapperService> Client = new();
    protected readonly List<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Queued = [];
    protected readonly List<(string Level, string Message)> Logged = [];
    /// <summary>Uploads and queued workflows in call order: "upload:{path}", "queue:{workflow file}".</summary>
    protected readonly List<string> Calls = [];
    protected readonly Mock<IFeatureReadinessService> Readiness = new();
    protected readonly DatasetEventAggregator Events = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-upscale-").FullName;

    public BatchUpscaleEngineRunTests()
    {
        Client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string path, CancellationToken _) => { Calls.Add("upload:" + path); return "up-" + Path.GetFileName(path); });
        Client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string wf, Dictionary<string, Action<JsonNode>> o, CancellationToken _) =>
            {
                Queued.Add((Path.GetFileName(wf), o));
                Calls.Add("queue:" + Path.GetFileName(wf));
                return $"p{Queued.Count}";
            });
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => ResultFor(Queued[int.Parse(id[1..]) - 1]));
        Client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>())).ReturnsAsync([1, 2, 3]);
        ReadinessWith(Paths());
    }

    /// <summary>A describe job answers "Description of {uploaded image}"; an upscale job returns one image.</summary>
    protected virtual ComfyUIResult ResultFor((string Workflow, Dictionary<string, Action<JsonNode>> Overrides) job)
    {
        var result = new ComfyUIResult();
        if (job.Workflow == "Qwen3-VL-Describe.json")
            result.Texts.Add($"Description of {ImageOf(job.Overrides, "1")}");
        else
            result.Images.Add(new ComfyUIImage("out.png", "", "output", "http://test/view"));
        return result;
    }

    protected static string ImageOf(Dictionary<string, Action<JsonNode>> o, string nodeId)
    {
        var node = JsonNode.Parse("""{"inputs":{"image":""}}""")!;
        o[nodeId](node);
        return node["inputs"]!["image"]!.GetValue<string>();
    }

    protected static string PromptOf(Dictionary<string, Action<JsonNode>> o)
    {
        var node = JsonNode.Parse("""{"inputs":{"text":"untouched"}}""")!;
        o["17"](node);
        return node["inputs"]!["text"]!.GetValue<string>();
    }

    protected static Dictionary<string, string> Paths() => new()
    {
        [QwenVlGguf.ModelName] = @"D:\m\model.gguf",
        [QwenVlGguf.ProjectorName] = @"D:\m\mmproj.gguf",
    };

    protected void ReadinessWith(IReadOnlyDictionary<string, string> paths, BackendKind backend = BackendKind.Engine) =>
        Readiness.Setup(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Feature f, CancellationToken _) => new FeatureReadinessResult
            {
                Feature = f, Backend = backend, ActiveBackendName = backend == BackendKind.Engine ? "Diffusion Nexus Engine" : "ComfyUI",
                IsBackendOnline = true, IsReady = true, MissingRequirements = [], Warnings = [], ModelPaths = paths
            });

    protected BatchUpscaleTabViewModel Sut(IComfyUiClientProvider? provider = null, IUiScheduler? uiScheduler = null)
    {
        var logger = new Mock<IUnifiedLogger>();
        logger.Setup(l => l.Info(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => Logged.Add(("Info", m)));
        logger.Setup(l => l.Warn(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<string?>()))
            .Callback((LogCategory _, string _, string m, string? _) => Logged.Add(("Warn", m)));
        logger.Setup(l => l.Error(It.IsAny<LogCategory>(), "Batch Upscale", It.IsAny<string>(), It.IsAny<Exception?>()))
            .Callback((LogCategory _, string _, string m, Exception? _) => Logged.Add(("Error", m)));
        return new BatchUpscaleTabViewModel(Events, new Mock<IDatasetState>().Object,
            clientProvider: provider ?? InpaintingViewModelGGUFResolutionTests.Provider(Client.Object, ComfyUiServerMode.Engine),
            readinessService: Readiness.Object, uiScheduler: uiScheduler ?? new ImmediateUiScheduler(),
            thumbnailDecoder: (_, _) => null, unifiedLogger: logger.Object);
    }

    protected string Image(string name)
    {
        var path = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x89, 0x50, 0x4E, 0x47]);
        return path;
    }

    protected async Task RunAsync(BatchUpscaleTabViewModel vm, UpscalePromptMode mode, params string[] names)
    {
        vm.IsSingleImageMode = true;
        foreach (var name in names) vm.SingleImagePaths.Add(Image(name));
        vm.PromptMode = mode;
        await vm.Readiness.CheckReadinessAsync();
        await vm.VisionReadiness.CheckReadinessAsync();
        await vm.StartUpscaleCommand.ExecuteAsync(null);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ManualPrompt_RunsOnTheLeasedClient_OneUpscalePerImage_WithTheTypedPrompt()
    {
        var vm = Sut();
        vm.PositivePrompt = "a sharp photo";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        Queued.Select(q => q.Workflow).Should().Equal("Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        Queued.Select(q => PromptOf(q.Overrides)).Should().Equal("a sharp photo", "a sharp photo");
        Logged.Should().Contain(("Info", "Running on the Diffusion Nexus Engine at http://test."));
        vm.CurrentProcessingStatus.Should().StartWith("Done – 2/2");
    }

    [Fact]
    public async Task EngineUnavailable_ShowsTheProvidersMessage()
    {
        var provider = new Mock<IComfyUiClientProvider>();
        provider.Setup(p => p.AcquireAsync(It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUiUnavailableException("The Diffusion Nexus Engine is not installed."));
        var vm = Sut(provider.Object);
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("The Diffusion Nexus Engine is not installed.");
        Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task NodeFailure_NamesTheNode()
    {
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ComfyUIExecutionException("UltimateSDUpscale", "CUDA out of memory"));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("Failed in the ComfyUI node UltimateSDUpscale – see the Unified Console");
        Logged.Should().Contain(e => e.Level == "Error" && e.Message.Contains("CUDA out of memory"));
    }

    [Fact]
    public async Task ServerAnswered400_SaysSo()
    {
        Client.Setup(c => c.QueueWorkflowAsync(It.IsAny<string>(), It.IsAny<Dictionary<string, Action<JsonNode>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("bad", null, HttpStatusCode.BadRequest));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("ComfyUI answered 400 – see the Unified Console");
    }

    // Round 1: the generic failure line said "failed at image N" without the step.
    [Fact]
    public async Task AnUpscaleFailure_NamesTheStepAndTheImage()
    {
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 2 ? Task.FromException(new HttpRequestException("connection reset")) : Task.CompletedTask);
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        Logged.Should().Contain(e => e.Level == "Error" && e.Message.Contains("upscaling image 2/2"));
    }

    // Round 2: every unexpected error in Engine mode asked "is the Diffusion Nexus Engine running?", also a full disk.
    [Fact]
    public async Task ALocalFileError_DoesNotBlameTheEngine()
    {
        Client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("There is not enough space on the disk."));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("Error: There is not enough space on the disk.");
    }

    [Fact]
    public async Task AConnectionError_AsksWhetherTheEngineIsRunning()
    {
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No connection could be made"));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Be("Error: No connection could be made – is the Diffusion Nexus Engine running?");
    }

    // Round 1: Settings/Engine events reached the tab through Dispatcher.UIThread instead of the injected scheduler.
    [Fact]
    public void ReadinessInputEvents_AreMarshalledThroughTheInjectedScheduler()
    {
        var scheduler = new Mock<IUiScheduler>();
        scheduler.SetupGet(s => s.IsOnUiThread).Returns(false);
        scheduler.Setup(s => s.Post(It.IsAny<Action>())).Callback((Action a) => a());
        var vm = Sut(uiScheduler: scheduler.Object);
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        vm.OnTabActivated();

        Events.PublishEngineChanged(new EngineChangedEventArgs());

        scheduler.Verify(s => s.Post(It.IsAny<Action>()), Times.Once);
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscale, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ── Readiness follows the tab (the tab never checked on its own; Start stayed greyed until "Check") ──

    [Fact]
    public void Activating_ChecksTheActiveMode_SwitchingModeAndEngineChangesCheckAgain_InactiveDoesNot()
    {
        var vm = Sut();
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        Events.PublishEngineChanged(new EngineChangedEventArgs());
        Readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Never, "the tab is not active");

        vm.OnTabActivated();
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscale, It.IsAny<CancellationToken>()), Times.Once);

        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscaleVision, It.IsAny<CancellationToken>()), Times.Once);

        Events.PublishEngineChanged(new EngineChangedEventArgs());
        Readiness.Verify(r => r.CheckAsync(Feature.BatchUpscaleVision, It.IsAny<CancellationToken>()), Times.Exactly(2));

        vm.OnTabDeactivated();
        Events.PublishSettingsSaved(new SettingsSavedEventArgs());
        Readiness.Verify(r => r.CheckAsync(It.IsAny<Feature>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
    }
}
