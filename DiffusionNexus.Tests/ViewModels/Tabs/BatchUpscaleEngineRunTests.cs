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
        var state = new Mock<IDatasetState>();
        state.SetupGet(s => s.Datasets).Returns(new System.Collections.ObjectModel.ObservableCollection<DiffusionNexus.UI.ViewModels.DatasetCardViewModel>());
        return Current = new BatchUpscaleTabViewModel(Events, state.Object,
            clientProvider: provider ?? InpaintingViewModelGGUFResolutionTests.Provider(Client.Object, ComfyUiServerMode.Engine),
            readinessService: Readiness.Object, uiScheduler: uiScheduler ?? new ImmediateUiScheduler(),
            thumbnailDecoder: (_, _) => null, unifiedLogger: logger.Object);
    }

    /// <summary>The view model the last <see cref="Sut"/> call made.</summary>
    protected BatchUpscaleTabViewModel? Current;

    /// <summary>Presses Cancel and answers like a client call the cancelled token stopped.</summary>
    protected Task UserCancels()
    {
        Current!.CancelUpscaleCommand.Execute(null);
        return Task.FromCanceled(new CancellationToken(true));
    }

    protected Task<T> UserCancels<T>()
    {
        Current!.CancelUpscaleCommand.Execute(null);
        return Task.FromCanceled<T>(new CancellationToken(true));
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

    // ── Round 5 ──

    private static string UpscaleFactorOf(Dictionary<string, Action<JsonNode>> o)
    {
        var node = JsonNode.Parse("""{"inputs":{"upscale_by":0,"denoise":0,"seed":0}}""")!;
        o["39"](node);
        return node["inputs"]!["upscale_by"]!.ToJsonString();
    }

    private Mock<DiffusionNexus.UI.Services.IDialogService> Confirm(bool answer)
    {
        var dialogs = new Mock<DiffusionNexus.UI.Services.IDialogService>();
        dialogs.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(answer);
        return dialogs;
    }

    // Round 5: New Version on a gallery selection fell back to processing the images in place when no dataset could be
    // created (no storage path), and the save rule then wrote each upscale over its original.
    [Fact]
    public async Task GallerySelection_NewVersionWithoutADataset_KeepsTheOriginals()
    {
        var a = Image(Path.Combine("day1", "a.png"));
        var b = Image(Path.Combine("day2", "b.png"));
        var vm = Sut();
        vm.PositivePrompt = "x";
        vm.LoadTemporaryImages([a, b]);
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        vm.SaveMode = UpscaleSaveMode.NewVersion;
        await vm.Readiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        File.ReadAllBytes(a).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        File.ReadAllBytes(b).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(a)!, "a_upscaled.png")).Should().Equal(1, 2, 3);
    }

    // Round 5: Overwrite on a gallery selection skipped the "cannot be undone" confirmation.
    [Fact]
    public async Task GallerySelection_Overwrite_AsksFirst_AndANoChangesNothing()
    {
        var a = Image(Path.Combine("day1", "a.png"));
        var dialogs = Confirm(false);
        var vm = Sut();
        vm.DialogService = dialogs.Object;
        vm.PositivePrompt = "x";
        vm.LoadTemporaryImages([a]);
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        vm.SaveMode = UpscaleSaveMode.OverwriteInPlace;
        await vm.Readiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        dialogs.Verify(d => d.ShowConfirmAsync("Overwrite Original Images?", It.IsAny<string>()), Times.Once);
        Queued.Should().BeEmpty();
        File.ReadAllBytes(a).Should().Equal(0x89, 0x50, 0x4E, 0x47);
    }

    // Round 5: the compare backups were named by file name only; same-named gallery images shared one backup.
    [Fact]
    public async Task GallerySelection_Overwrite_SameNamedImagesKeepTheirOwnBeforeCopy()
    {
        var a = Image(Path.Combine("day1", "img.png"));
        var b = Image(Path.Combine("day2", "img.png"));
        File.WriteAllBytes(a, [0xA]);
        File.WriteAllBytes(b, [0xB]);
        var vm = Sut();
        vm.DialogService = Confirm(true).Object;
        vm.PositivePrompt = "x";
        vm.LoadTemporaryImages([a, b]);
        vm.PromptMode = UpscalePromptMode.ManualPrompt;
        vm.SaveMode = UpscaleSaveMode.OverwriteInPlace;
        await vm.Readiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        File.ReadAllBytes(vm.UpscaleItems[0].OriginalPath).Should().Equal(0xA);
        File.ReadAllBytes(vm.UpscaleItems[1].OriginalPath).Should().Equal(0xB);
    }

    // Round 5: a failed write or move left the ".upscaling" partial behind.
    [Fact]
    public async Task AFailedSave_LeavesNoPartialFile()
    {
        var vm = Sut();
        vm.PositivePrompt = "x";
        var a = Image("a.png");
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(a)!, "a_upscaled.png"));

        await RunAsync(vm, UpscalePromptMode.ManualPrompt);
        vm.SingleImagePaths.Add(a);
        await vm.StartUpscaleCommand.ExecuteAsync(null);

        vm.CurrentProcessingStatus.Should().StartWith("Error:");
        Directory.EnumerateFiles(Path.GetDirectoryName(a)!, "*.upscaling").Should().BeEmpty();
    }

    // Round 5: upscale factor, denoise and prompts were read per image, so a change during the run split the batch.
    [Fact]
    public async Task SettingsChangedDuringTheRun_TheRunKeepsTheOnesItStartedWith()
    {
        BatchUpscaleTabViewModel? vm = null;
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits == 1) { vm!.UpscaleFactor = 3.0; vm.PositivePrompt = "changed"; }
                return Task.CompletedTask;
            });
        vm = Sut();
        vm.PositivePrompt = "a sharp photo";
        vm.UpscaleFactor = 1.5;

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        Queued.Select(q => UpscaleFactorOf(q.Overrides)).Should().Equal("1.5", "1.5");
        Queued.Select(q => PromptOf(q.Overrides)).Should().Equal("a sharp photo", "a sharp photo");
    }

    // Round 5: an HttpClient timeout (TaskCanceledException without Cancel) was reported as "Cancelled".
    [Fact]
    public async Task ATimeout_IsAnError_NotACancel()
    {
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 600 seconds elapsing."));
        var vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().StartWith("Error:");
        Logged.Should().Contain(e => e.Level == "Error");
    }

    // Round 4: the result was written with the run's token; a Cancel during the write truncated the target, which in
    // Overwrite mode is the original. A downloaded result is now always written whole, and the run stops after it.
    [Fact]
    public async Task ACancelAfterTheDownload_StillWritesThatImageWhole()
    {
        BatchUpscaleTabViewModel? vm = null;
        Client.Setup(c => c.DownloadImageAsync(It.IsAny<ComfyUIImage>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { vm!.CancelUpscaleCommand.Execute(null); return [1, 2, 3]; });
        vm = Sut();
        vm.PositivePrompt = "x";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        var written = Directory.EnumerateFiles(Path.GetDirectoryName(vm.SingleImagePaths[0])!).Select(Path.GetFileName).ToList();
        written.Should().BeEquivalentTo(["a.png", "b.png", "a_upscaled.png"]);
        File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(vm.SingleImagePaths[0])!, "a_upscaled.png")).Should().Equal(1, 2, 3);
        vm.CurrentProcessingStatus.Should().Be("Cancelled after 1/2 images.");
    }

    // Round 4: the prompt mode stays editable during a run, and step 2 read it live.
    [Fact]
    public async Task ThePromptModeChangesDuringTheRun_TheRunKeepsTheOneItStartedWith()
    {
        BatchUpscaleTabViewModel? vm = null;
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits == 1) vm!.PromptMode = UpscalePromptMode.VisionAutoPrompt;
                return Task.CompletedTask;
            });
        vm = Sut();
        vm.PositivePrompt = "a sharp photo";

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png", "b.png");

        Queued.Select(q => PromptOf(q.Overrides)).Should().Equal("a sharp photo", "a sharp photo");
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
