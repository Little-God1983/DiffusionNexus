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
        ImageOf(Describes.Last().Overrides, "1").Should().Be("up-a.png", "the free-up job reuses the last described image");
        vm.StepText.Should().BeNull();
        vm.CurrentProcessingStatus.Should().StartWith("Cancelled");
    }

    /// <summary>Fails the describe job of the given image (1-based) while it waits for completion.</summary>
    private void FailDescribeWait(int image, Exception ex)
    {
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns((string id, IProgress<string>? _, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                var describeNo = Queued.Take(int.Parse(id[1..])).Count(q => q.Workflow == "Qwen3-VL-Describe.json");
                return job.Workflow == "Qwen3-VL-Describe.json" && describeNo == image ? Task.FromException(ex) : Task.CompletedTask;
            });
    }

    // Round 1: the last image failing before Qwen3-VL ran (LoadImage) made the free-up job fail on the same image,
    // leaving the model loaded by the earlier keep_vram jobs in VRAM for step 2.
    [Fact]
    public async Task TheLastImageFailsBeforeQwenRuns_TheFreeJobUsesTheLastDescribedImage()
    {
        FailDescribeWait(2, new ComfyUIExecutionException("LoadImage", "cannot identify image file"));

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Describes.Should().HaveCount(3);
        ImageOf(Describes.Last().Overrides, "1").Should().Be("up-a.png");
        ModeOf(Describes.Last().Overrides).Should().Be("direct_clean");
    }

    // Round 1: after "❌ Inference failed" the node has already unloaded everything; a free-up job only reloads the model.
    [Fact]
    public async Task TheLastInferenceFailed_NoFreeJob_TheNodeAlreadyUnloaded()
    {
        var describes = 0;
        Client.Setup(c => c.GetResultAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) =>
            {
                var job = Queued[int.Parse(id[1..]) - 1];
                if (job.Workflow == "Qwen3-VL-Describe.json" && ++describes == 2)
                {
                    var failed = new ComfyUIResult();
                    failed.Texts.Add("❌ Inference failed:\nCUDA out of memory\nCheck console for details.");
                    return failed;
                }
                return ResultFor(job);
            });

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Describes.Should().HaveCount(2);
        Upscales.Should().HaveCount(2);
    }

    // Round 1: step 1 stopping before any description finished loaded Qwen3-VL from cold just to unload it.
    [Fact]
    public async Task StepOneStopsBeforeAnythingWasDescribed_NoFreeJob()
    {
        FailDescribeWait(1, new ComfyUIWorkflowRejectedException("prompt_outputs_failed_validation"));

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Describes.Should().HaveCount(1);
        Upscales.Should().BeEmpty();
    }

    // Round 1: the log said "failed at image 1/3" for any step-1 failure and named the upscale workflow.
    [Fact]
    public async Task StepOneFailures_NameTheDescribeStepAndTheImage()
    {
        FailDescribeWait(2, new HttpRequestException("connection reset"));

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Logged.Should().Contain(e => e.Level == "Error" && e.Message.Contains("describing image 2/3"));
    }

    [Fact]
    public async Task ARejectedDescribeJob_IsNotCalledTheUpscaleWorkflow()
    {
        FailDescribeWait(1, new ComfyUIWorkflowRejectedException("prompt_outputs_failed_validation"));

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png");

        Logged.Should().Contain(e => e.Level == "Error" && e.Message.Contains("describing image 1/1")
                                     && !e.Message.Contains("upscale workflow"));
    }

    // Round 1: a dataset run in New Version mode creates the version folder and its branch record before step 1;
    // stopping before any image was upscaled left an empty version behind, one more per retry.
    [Fact]
    public async Task NewVersion_CancelledInStepOne_LeavesNoEmptyVersionBehind()
    {
        var datasetFolder = Path.GetDirectoryName(Path.GetDirectoryName(Image(Path.Combine("ds", "V1", "a.png"))))!;
        Image(Path.Combine("ds", "V1", "b.png"));
        var dataset = DiffusionNexus.UI.ViewModels.DatasetCardViewModel.FromFolder(datasetFolder);
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 2 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);
        var vm = Sut();
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        vm.CurrentProcessingStatus.Should().StartWith("Cancelled");
        Directory.Exists(Path.Combine(datasetFolder, "V2")).Should().BeFalse();
        dataset.VersionBranchedFrom.Should().NotContainKey(2);
        DiffusionNexus.UI.ViewModels.DatasetCardViewModel.FromFolder(datasetFolder).VersionBranchedFrom.Should().NotContainKey(2);
    }

    // Round 2: a cancel while the first keep_vram job ran skipped the free-up job; the server finishes that job
    // and leaves Qwen3-VL loaded.
    [Fact]
    public async Task CancelWhileTheFirstImageIsDescribed_StillFreesQwen()
    {
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 1 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "direct_clean");
        ImageOf(Describes.Last().Overrides, "1").Should().Be("up-a.png");
    }

    private DiffusionNexus.UI.ViewModels.DatasetCardViewModel TwoImageDataset(out string folder)
    {
        folder = Path.GetDirectoryName(Path.GetDirectoryName(Image(Path.Combine("ds", "V1", "a.png"))))!;
        Image(Path.Combine("ds", "V1", "b.png"));
        return DiffusionNexus.UI.ViewModels.DatasetCardViewModel.FromFolder(folder);
    }

    // Round 2: a Vision run that stopped before resetting the count read the previous run's count and turned
    // its new, empty folder into the dataset's current version.
    [Fact]
    public async Task NewVersion_AfterAnEarlierRun_AStopBeforeTheFirstImageLeavesNoEmptyVersion()
    {
        var vm = Sut();
        vm.PositivePrompt = "x";
        await RunAsync(vm, UpscalePromptMode.ManualPrompt, "x.png", "y.png");
        vm.CompletedCount.Should().Be(2);
        var dataset = TwoImageDataset(out var folder);
        ReadinessWith(new Dictionary<string, string>());
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(folder, "V2")).Should().BeFalse();
        dataset.CurrentVersion.Should().Be(1);
    }

    // Round 2: the empty version was discarded from whatever dataset was selected when the run ended; the selector
    // stays enabled during a run, and Image(s) mode leaves none selected.
    [Fact]
    public async Task NewVersion_TheSelectionChangesDuringTheRun_TheRunsOwnDatasetIsCleanedUp()
    {
        var dataset = TwoImageDataset(out var folder);
        var vm = Sut();
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits != 2) return Task.CompletedTask;
                vm.SelectedDataset = null;
                return Task.FromCanceled(new CancellationToken(true));
            });
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.Invoking(v => v.StartUpscaleCommand.ExecuteAsync(null)).Should().NotThrowAsync();

        Directory.Exists(Path.Combine(folder, "V2")).Should().BeFalse();
        dataset.VersionBranchedFrom.Should().NotContainKey(2);
    }

    // Round 2: the install hint sent own-ComfyUI users to the Engine's Features dialog.
    [Fact]
    public async Task WithoutTheModelPaths_OnYourOwnComfyUI_TheHintNamesYourComfyUI()
    {
        ReadinessWith(new Dictionary<string, string>(), DiffusionNexus.Domain.Enums.BackendKind.ComfyUI);
        var vm = Sut();

        await RunAsync(vm, UpscalePromptMode.VisionAutoPrompt, "a.png");

        vm.CurrentProcessingStatus.Should().Contain("your ComfyUI").And.NotContain("Diffusion Nexus Engine");
    }

    // Round 3: the selection can change during a run; a successful run made the selected dataset current instead
    // of its own.
    [Fact]
    public async Task NewVersion_TheSelectionChangesDuringASuccessfulRun_TheRunsOwnDatasetGetsTheVersion()
    {
        var dataset = TwoImageDataset(out var folder);
        var vm = Sut();
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits == 3) vm.SelectedDataset = null;
                return Task.CompletedTask;
            });
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        Directory.EnumerateFiles(Path.Combine(folder, "V2")).Should().HaveCount(2);
        dataset.CurrentVersion.Should().Be(2);
    }

    // Round 3: the clean-up dropped the branch record even when the folder kept a file (a write cut off by Cancel),
    // leaving a version on disk with no record.
    [Fact]
    public async Task NewVersion_AFolderThatIsNotEmpty_KeepsItsBranchRecord()
    {
        var dataset = TwoImageDataset(out var folder);
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits != 2) return Task.CompletedTask;
                File.WriteAllBytes(Path.Combine(folder, "V2", "a.png"), [1]);
                return Task.FromCanceled(new CancellationToken(true));
            });
        var vm = Sut();
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        Directory.Exists(Path.Combine(folder, "V2")).Should().BeTrue();
        dataset.VersionBranchedFrom.Should().Contain(2, 1);
        dataset.CurrentVersion.Should().Be(2, "Round 4: a version that keeps a file is finalized, not left hidden");
    }

    // Round 4: Save mode stays editable during step 1 (minutes); switching to Overwrite made step 2 replace the
    // originals with no confirmation and no compare backup.
    [Fact]
    public async Task TheSaveModeChangesDuringStepOne_TheRunKeepsTheOneItStartedWith()
    {
        var dataset = TwoImageDataset(out var folder);
        var vm = Sut();
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (++waits == 1) vm.SaveMode = UpscaleSaveMode.OverwriteInPlace;
                return Task.CompletedTask;
            });
        vm.SelectedDataset = dataset;
        vm.SelectedDatasetVersion = vm.AvailableDatasetVersions.Single(v => v.Version == 1);
        vm.PromptMode = UpscalePromptMode.VisionAutoPrompt;
        await vm.VisionReadiness.CheckReadinessAsync();

        await vm.StartUpscaleCommand.ExecuteAsync(null);

        File.ReadAllBytes(Path.Combine(folder, "V1", "a.png")).Should().Equal(0x89, 0x50, 0x4E, 0x47);
        Directory.EnumerateFiles(Path.Combine(folder, "V2")).Should().HaveCount(2);
    }

    // Round 3: a failure in a node after Qwen3-VL (ShowText) was taken for one before it, so a later cancel left
    // the model a keep_vram job had loaded.
    [Fact]
    public async Task AFailureAfterTheDescriber_StillCountsTheModelAsLoaded()
    {
        FailDescribeWait(1, new ComfyUIExecutionException("ShowText|pysssss", "bad text"));
        var uploads = 0;
        Client.Setup(c => c.UploadImageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns((string path, CancellationToken _) => ++uploads == 2
                ? Task.FromCanceled<string>(new CancellationToken(true))
                : Task.FromResult("up-" + Path.GetFileName(path)));

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png", "c.png");

        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "direct_clean");
    }

    // Round 3: a cancel during the last (direct_clean) job sent a free-up job, though the server runs that job to
    // the end and unloads the model itself; the extra job only reloaded it.
    [Fact]
    public async Task CancelDuringTheLastDescription_NoFreeJob()
    {
        var waits = 0;
        Client.Setup(c => c.WaitForCompletionAsync(It.IsAny<string>(), It.IsAny<IProgress<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++waits == 2 ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);

        await RunAsync(Sut(), UpscalePromptMode.VisionAutoPrompt, "a.png", "b.png");

        Describes.Select(d => ModeOf(d.Overrides)).Should().Equal("keep_vram", "direct_clean");
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
