using System.Text.Json.Nodes;
using DiffusionNexus.Domain.Models;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels.Tabs;

/// <summary>#608: Vision describes every image with Qwen3-VL kept loaded, frees it, then upscales.</summary>
public class BatchUpscaleVisionTwoStepTests : BatchUpscaleEngineRunTests
{
    private static string ModeOf(Dictionary<string, Action<JsonNode>> o)
    {
        var node = JsonNode.Parse("""{"inputs":{"mode":"","seed":0,"config_override":""}}""")!;
        o["3"](node);
        return node["inputs"]!["mode"]!.GetValue<string>();
    }

    private IEnumerable<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Describes =>
        Queued.Where(q => q.Workflow == "Qwen3-VL-Describe.json");

    private IEnumerable<(string Workflow, Dictionary<string, Action<JsonNode>> Overrides)> Upscales =>
        Queued.Where(q => q.Workflow == "Z-Image-Turbo-Upscale.json");

    [Fact]
    public async Task DescribesEveryImageFirst_KeepingQwenLoaded_ThenUpscalesEachWithItsOwnDescription()
    {
        var vm = Sut();
        var steps = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StepText)) steps.Add(vm.StepText); };

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Queued.Select(q => q.Workflow).Should().Equal(
            "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json",
            "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "keep_vram", "direct_clean");
        Upscales.Select(u => PromptOf(u.Overrides)).Should().Equal(
            "Description of up-a.png", "Description of up-b.png", "Description of up-c.png");
        Upscales.Select(u => ImageOf(u.Overrides, "50")).Should().Equal("up-a.png", "up-b.png", "up-c.png");
        steps.Should().Equal(BatchUpscaleTabViewModel.StepOneText, BatchUpscaleTabViewModel.StepTwoText, null);
        vm.UpscaleItems.Select(i => i.Description).Should().Equal(
            "Description of up-a.png", "Description of up-b.png", "Description of up-c.png");
        Logged.Should().Contain(("Info", "Description of a.png: Description of up-a.png"));
    }

    // Review: ComfyUI stores an upload under its plain file name and overwrites on a clash, so two inputs
    // named alike (gallery subfolders restart their counters) shared one server file across the steps.
    [Fact]
    public async Task SameFileNameInTwoFolders_EachUpscaleUsesTheUploadMadeRightBeforeIt()
    {
        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, Path.Combine("day1", "img.png"), Path.Combine("day2", "img.png"));

        var upscales = Calls.Select((c, i) => (c, i)).Where(x => x.c == "queue:Z-Image-Turbo-Upscale.json").Select(x => x.i).ToList();
        upscales.Should().HaveCount(2);
        Calls[upscales[0] - 1].Should().EndWith(Path.Combine("day1", "img.png"));
        Calls[upscales[1] - 1].Should().EndWith(Path.Combine("day2", "img.png"));
    }

    // Review: ComfyUI keeps the last run's models (Z-Image, ~20 GB) loaded; llama.cpp allocates outside its
    // memory manager, so the first description asks the node to unload them first.
    [Fact]
    public async Task TheFirstDescriptionFreesComfyUIsModels_TheOthersDoNot()
    {
        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Describes.Select(d =>
        {
            var node = JsonNode.Parse("""{"inputs":{"mode":"","seed":0,"config_override":"","unload_all_models":false}}""")!;
            d.Overrides["3"](node);
            return node["inputs"]!["unload_all_models"]!.GetValue<bool>();
        }).Should().Equal(true, false, false);
    }

    // Review: the node returns "❌ Inference failed: …" as its text instead of raising; it must not become a prompt.
    [Fact]
    public async Task InferenceFailedText_IsAFailedDescription_NotAPrompt()
    {
        var describes = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++describes == 1)
                {
                    var failed = new ComfyUIResult();
                    failed.Texts.Add("❌ Inference failed:\nCUDA out of memory\nCheck console for details.");
                    return failed;
                }
                return ResultFor(job);
            });
        var vm = Sut();

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Upscales.Select(u => PromptOf(u.Overrides)).Should().Equal("", "Description of up-b.png");
        vm.UpscaleItems[0].Description.Should().BeNull();
        Logged.Should().Contain(e => e.Level == "Warn" && e.Message.Contains("a.png") && e.Message.Contains("CUDA out of memory"));
    }

    // Review: a describe step failing for every image upscaled everything without a prompt and still said "Done".
    [Fact]
    public async Task ImagesUpscaledWithoutADescription_AreCountedInTheFinalStatus()
    {
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json")
                    throw new ComfyUIExecutionException("SimpleQwenVLggufV2", "node type not found");
                return ResultFor(job);
            });
        var vm = Sut();

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        vm.CurrentProcessingStatus.Should().Be("Done – 3/3 image(s) upscaled; 3 without a description (see the Unified Console).");
    }

    [Fact]
    public async Task OneImage_IsDescribedAndFreedInOneJob()
    {
        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png");

        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("direct_clean");
    }

    [Fact]
    public async Task CancelDuringStepOne_FreesQwen_AndUpscalesNothing()
    {
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 2 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);

        var vm = Sut();
        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Upscales.Should().BeEmpty();
        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "keep_vram", "direct_clean");
        ImageOf(Describes.Last().Overrides, "1").Should().Be("up-b.png", "the free-up job reuses the last upload");
        vm.StepText.Should().BeNull();
        vm.CurrentProcessingStatus.Should().StartWith("Cancelled");
    }

    [Fact]
    public async Task CancelBeforeTheFirstUpload_QueuesNothing()
    {
        Client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Queued.Should().BeEmpty("nothing was loaded, so there is nothing to free");
    }

    [Fact]
    public async Task AFailedDescription_UpscalesThatImageWithoutAPrompt_AndWarns()
    {
        var results = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++results == 2)
                    throw new ComfyUIExecutionException("SimpleQwenVLggufV2", "context overflow");
                return ResultFor(job);
            });

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Upscales.Select(u => PromptOf(u.Overrides)).Should().Equal("Description of up-a.png", "", "Description of up-c.png");
        Logged.Should().Contain(e => e.Level == "Warn" && e.Message.Contains("b.png") && e.Message.Contains("context overflow"));
    }

    [Fact]
    public async Task TheLastDescriptionFails_QwenIsFreedBeforeStepTwo()
    {
        var results = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++results == 2)
                    throw new ComfyUIExecutionException("SimpleQwenVLggufV2", "boom");
                return ResultFor(job);
            });

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Queued.Select(q => q.Workflow).Should().Equal(
            "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json", "Qwen3-VL-Describe.json",
            "Z-Image-Turbo-Upscale.json", "Z-Image-Turbo-Upscale.json");
        ModeOf(Queued[2].Overrides).Should().Be("direct_clean");
    }

    [Fact]
    public async Task WithoutTheModelPaths_ChecksOnce_ThenStopsBeforeUploading_WithTheInstallHint()
    {
        ReadinessWith(new Dictionary<string, string>());
        var vm = Sut();

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png");

        Queued.Should().BeEmpty();
        Client.Verify(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        vm.CurrentProcessingStatus.Should().Contain("Install Batch Upscale Vision");
    }

    [Fact]
    public async Task OtherPromptModes_RunOneStep_WithoutABanner()
    {
        var vm = Sut();
        vm.PositivePrompt = "x";
        var steps = new List<string?>();
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.StepText)) steps.Add(vm.StepText); };

        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "a.png");

        Describes.Should().BeEmpty();
        steps.Should().BeEmpty();
    }
}
