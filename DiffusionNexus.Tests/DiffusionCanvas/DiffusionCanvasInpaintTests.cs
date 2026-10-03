using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Media.Imaging;
using DiffusionNexus.Inference.Abstractions;
using DiffusionNexus.Inference.StableDiffusionCpp;
using DiffusionNexus.UI.DiffusionCanvas;
using DiffusionNexus.UI.Services.Diffusion;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>
/// The canvas side of the inpaint mask (#595): when the mask takes part, what the readout says, what
/// Generate sends, and when the brush can be used.
/// </summary>
public class DiffusionCanvasInpaintTests : IDisposable
{
    private readonly TempCanvasFile _image = new(512, 512, SKColors.White);

    public void Dispose() => _image.Dispose();

    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend)
        {
            PromptText = "a lighthouse at dusk",
            ScratchDirectory = CanvasScratch.NewDirectory(),
        };
        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        return vm;
    }

    /// <summary>A canvas whose box sits exactly over one white 512×512 result.</summary>
    private DiffusionCanvasViewModel CanvasOverAResult(FakeDiffusionBackend backend)
    {
        var vm = Canvas(backend);
        vm.Frames.Add(_image.AsFrame(0, 0, 512, 512));
        return vm;
    }

    private static CanvasMaskStroke Dot(double x, double y, double size = 100, bool erase = false) =>
        new([new Point(x, y)], size, erase);

    // ────────────────────────────── The readout ──────────────────────────────

    [Fact]
    public void WithoutAMaskTheReadoutIsUnchanged()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());

        vm.IsInpaintRun.Should().BeFalse();
        vm.RegionModeBadge.Should().Be("Image to image");
        vm.RegionModeText.Should().Be("Image to image — the box is over 1 result");
    }

    [Fact]
    public void APaintedMaskOverAResultMakesItAnInpaintRun()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());

        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        vm.IsInpaintRun.Should().BeTrue("the readout follows the stroke");
        vm.RegionModeBadge.Should().Be("Inpaint");
        vm.RegionModeText.Should().Contain("only the painting is repainted");
    }

    [Fact]
    public void AHiddenMaskIsLeftOutAndTheReadoutSaysWhy()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        vm.Layers.Mask.IsVisible = false;

        vm.IsInpaintRun.Should().BeFalse();
        vm.RegionModeBadge.Should().Be("Image to image");
        vm.RegionModeText.Should().Contain("the mask is hidden");
    }

    [Fact]
    public void AnEmptyMaskIsLeftOutAndTheReadoutSaysWhy()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());

        vm.AddMaskCommand.Execute(null);

        vm.IsInpaintRun.Should().BeFalse();
        vm.RegionModeText.Should().Contain("nothing is painted on the mask");
    }

    [Fact]
    public void AMaskOutsideTheBoxIsLeftOutAndTheReadoutSaysWhy()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);

        vm.Layers.Mask!.AddStroke(Dot(2000, 2000));

        vm.IsInpaintRun.Should().BeFalse();
        vm.RegionModeText.Should().Contain("the mask is outside the box");
    }

    [Fact]
    public void MovingTheBoxOntoTheMaskMakesItAnInpaintRun()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.Frames.Add(_image.AsFrame(1024, 0, 512, 512));
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(1280, 256));
        vm.IsInpaintRun.Should().BeFalse();

        vm.Box.SetPosition(1024, 0);

        vm.IsInpaintRun.Should().BeTrue();
    }

    [Fact]
    public void AStrokeJustOutsideTheBoxCountsOnceItsFeatherReachesIn()
    {
        // The rasteriser feathers a stroke this close into the box; the readout must agree, or Generate
        // drops the mask and repaints the whole box at the panel's denoise.
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(-20, 256, size: 20));
        vm.IsInpaintRun.Should().BeFalse("without feather the stroke ends 10 px left of the box");

        vm.Layers.Mask.Feather = 16;

        vm.IsInpaintRun.Should().BeTrue();
    }

    [Fact]
    public async Task AStrokeJustOutsideTheBoxThatFeathersInIsSentNotRefused()
    {
        // The readout and Generate share one rule: what the readout counts, Generate runs.
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(-20, 256, size: 20));
        vm.Layers.Mask.Feather = 16;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1);
        backend.LastRequest!.MaskImage.Should().NotBeNull();
        using var mask = SKBitmap.Decode(backend.MaskImageBytesAtCallTime);
        mask.GetPixel(0, 256).Red.Should().BeGreaterThanOrEqualTo(CanvasMaskRaster.MinimumFeatheredValue,
            "the feathered edge reaches into the box with a strength that changes something");
    }

    [Fact]
    public async Task AStrokeAtTheFarEdgeOfTheFeathersReachIsRefusedAsTooFaint()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(-55, 256, size: 20));
        vm.Layers.Mask.Feather = 16;
        vm.IsInpaintRun.Should().BeTrue("the readout counts the stroke's reach");

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("too faint").And.Contain("lower the feather");
    }

    [Fact]
    public async Task AStrokeOutOfReachIsRefusedAsSuchEvenWithAnEraserElsewhere()
    {
        // Its bounds clip the box's corner (so the readout counts it), but its path never enters the box.
        // An eraser used far away must not turn that into "everything there was erased".
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(new CanvasMaskStroke([new Point(-300, 100), new Point(100, -300)], 10, isErase: false));
        vm.Layers.Mask.AddStroke(Dot(3000, 3000, size: 50, erase: true));
        vm.IsInpaintRun.Should().BeTrue();

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("does not reach the box").And.NotContain("erased");
    }

    [Fact]
    public async Task APasteThatThrowsKeepsTheBackendsResultAndTheCandidate()
    {
        using var black = new SKBitmap(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(black))
            canvas.Clear(SKColors.Black);
        using var image = SKImage.FromBitmap(black);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var backendBytes = data.ToArray();

        var backend = new FakeDiffusionBackend { ResultPng = backendBytes };
        var vm = CanvasOverAResult(backend);
        vm.PasteKeptPixels = (_, _) => throw new OutOfMemoryException("simulated");
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        var candidate = vm.Staging.Candidates.Single();
        candidate.State.Should().Be(StagedCandidateState.Ready, "the GPU result was already paid for");
        candidate.PngBytes.Should().Equal(backendBytes);
    }

    [Fact]
    public async Task ASmallDabUnderTheLargestFeatherIsSentNotRefused()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256, size: 64));
        vm.Layers.Mask.Feather = 64;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(1);
    }

    [Fact]
    public void OverEmptyCanvasTheMaskHasNothingToKeepAndTheReadoutSaysSo()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);

        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        vm.IsInpaintRun.Should().BeFalse();
        vm.RegionModeBadge.Should().Be("Text to image");
        vm.RegionModeText.Should().Contain("needs an image under the box");
    }

    [Fact]
    public void AnInvertedMaskAppliesEvenWhenItsPaintingIsOutsideTheBox()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(2000, 2000));

        vm.Layers.Mask.Invert = true;

        vm.IsInpaintRun.Should().BeTrue("everything in the box except the painting is repainted");
        vm.RegionModeText.Should().Contain("everything but the painting is repainted");
    }

    // ────────────────────────────── Generate ──────────────────────────────

    [Fact]
    public async Task GenerateSendsTheMaskForTheBoxWithTheMasksDenoise()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.DenoiseStrength = 0.4;
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.Denoise = 0.8;
        vm.Layers.Mask.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        var request = backend.LastRequest!;
        request.MaskImage.Should().NotBeNull();
        request.InitImage!.Strength.Should().BeApproximately(0.8f, 0.0001f, "the mask's denoise replaces the panel's");

        using var mask = SKBitmap.Decode(backend.MaskImageBytesAtCallTime);
        mask.Width.Should().Be(512);
        mask.GetPixel(256, 256).Red.Should().Be(255, "painted: repaint");
        mask.GetPixel(10, 10).Red.Should().Be(0, "unpainted: keep");
    }

    [Fact]
    public async Task EveryCandidateInABatchGetsTheSameMask()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.BatchCount = 3;
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.Requests.Should().HaveCount(3);
        backend.Requests.Select(r => r.MaskImage?.FilePath).Distinct().Should().ContainSingle()
            .Which.Should().NotBeNull();
        // The fake's result is not a PNG, so the kept pixels cannot be put back. That must cost a
        // warning, not the candidate.
        vm.Staging.Candidates.Should().OnlyContain(c => c.State == StagedCandidateState.Ready);
    }

    [Fact]
    public async Task TheMaskScratchFileIsDeletedAfterTheBatch()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        File.Exists(backend.LastRequest!.MaskImage!.FilePath).Should().BeFalse();
    }

    [Fact]
    public async Task AHiddenMaskIsNotSentAndThePanelsDenoiseApplies()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.DenoiseStrength = 0.4;
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));
        vm.Layers.Mask.IsVisible = false;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.MaskImage.Should().BeNull();
        backend.LastRequest.InitImage!.Strength.Should().BeApproximately(0.4f, 0.0001f);
    }

    [Fact]
    public async Task OverEmptyCanvasNoMaskIsSent()
    {
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.InitImage.Should().BeNull();
        backend.LastRequest.MaskImage.Should().BeNull();
    }

    [Fact]
    public async Task AMaskErasedInsideTheBoxRefusesToRun()
    {
        // The strokes' bounds still meet the box, so the readout promised inpaint; erasing left nothing.
        // Running unmasked would repaint the whole box the user meant to protect.
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256, size: 50));
        vm.Layers.Mask.AddStroke(Dot(256, 256, size: 200, erase: true));
        vm.IsInpaintRun.Should().BeTrue();

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("everything there was erased");
        vm.Staging.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task AnInvertedMaskCoveringTheBoxRefusesAndSaysToPaintLess()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256, size: 2000));
        vm.Layers.Mask.Invert = true;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.RunCount.Should().Be(0);
        vm.StatusText.Should().Contain("turn Invert off").And.NotContain("erased");
    }

    [Fact]
    public async Task ThePixelsOutsideTheMaskAreTheOriginalsNotTheBackends()
    {
        // Both backends VAE-decode the whole latent, so what they return outside the mask is a slightly
        // altered copy. Here the "backend" returns solid black: only the painted area may keep it.
        using var black = new SKBitmap(512, 512, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(black))
            canvas.Clear(SKColors.Black);
        using var image = SKImage.FromBitmap(black);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);

        var backend = new FakeDiffusionBackend { ResultPng = data.ToArray() };
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));

        await vm.GenerateCommand.ExecuteAsync(null);

        using var result = SKBitmap.Decode(vm.Staging.Candidates.Single().PngBytes);
        result.GetPixel(256, 256).Red.Should().Be(0, "painted: the generated pixel");
        result.GetPixel(10, 10).Red.Should().Be(255, "unpainted: the original white, exactly");
    }

    [Fact]
    public async Task AnInvertedMaskKeepsThePaintingAndRepaintsTheRest()
    {
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(256, 256));
        vm.Layers.Mask.Invert = true;

        await vm.GenerateCommand.ExecuteAsync(null);

        using var mask = SKBitmap.Decode(backend.MaskImageBytesAtCallTime);
        mask.GetPixel(256, 256).Red.Should().Be(0);
        mask.GetPixel(10, 10).Red.Should().Be(255);
    }

    [Fact]
    public async Task TheMaskIsBuiltOnceForTheWholeBatch()
    {
        // A stroke painted while the batch runs belongs to the next Generate, like a prompt edit.
        var backend = new FakeDiffusionBackend();
        var vm = CanvasOverAResult(backend);
        vm.BatchCount = 2;
        vm.AddMaskCommand.Execute(null);
        vm.Layers.Mask!.AddStroke(Dot(100, 100, size: 20));
        backend.BeforeRun = run =>
        {
            if (run == 1)
                vm.Layers.Mask.AddStroke(Dot(400, 400, size: 20));
        };

        await vm.GenerateCommand.ExecuteAsync(null);

        using var mask = SKBitmap.Decode(backend.MaskImageBytesAtCallTime);
        mask.GetPixel(400, 400).Red.Should().Be(0, "the batch's mask was frozen when Generate started");
    }

    // ────────────────────────────── The tools ──────────────────────────────

    [Fact]
    public void TheBrushNeedsTheSelectedVisibleMask()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.CanPaint.Should().BeFalse();
        vm.BrushTooltip.Should().Contain("+ Mask creates it");

        vm.IsBrushActive = true;
        vm.PaintTool.Should().Be(CanvasPaintTool.None, "there is no mask to paint");

        vm.AddMaskCommand.Execute(null);
        vm.CanPaint.Should().BeTrue();
        vm.IsBrushActive = true;

        vm.PaintTool.Should().Be(CanvasPaintTool.Brush);
        vm.IsEraserActive.Should().BeFalse();
    }

    [Fact]
    public void BrushAndEraserAreOneChoice()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.IsBrushActive = true;

        vm.IsEraserActive = true;

        vm.PaintTool.Should().Be(CanvasPaintTool.Eraser);
        vm.IsBrushActive.Should().BeFalse();

        vm.IsEraserActive = false;
        vm.PaintTool.Should().Be(CanvasPaintTool.None);
    }

    [Fact]
    public void SelectingARasterPutsTheBrushDown()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.IsBrushActive = true;

        vm.Layers.SelectedLayer = vm.Frames[0];

        vm.PaintTool.Should().Be(CanvasPaintTool.None);
        vm.CanPaint.Should().BeFalse();
    }

    [Fact]
    public void HidingTheMaskPutsTheBrushDownAndSaysWhy()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.IsBrushActive = true;

        vm.Layers.Mask!.IsVisible = false;

        vm.PaintTool.Should().Be(CanvasPaintTool.None);
        vm.BrushTooltip.Should().Contain("hidden");
    }

    [Fact]
    public void DeletingTheMaskPutsTheEraserDown()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.AddMaskCommand.Execute(null);
        vm.IsEraserActive = true;

        vm.Layers.DeleteMask();

        vm.PaintTool.Should().Be(CanvasPaintTool.None);
    }

    [Fact]
    public void AddMaskShowsTheLayerPanel()
    {
        var vm = CanvasOverAResult(new FakeDiffusionBackend());
        vm.IsLayerPanelVisible = false;

        vm.AddMaskCommand.Execute(null);

        vm.IsLayerPanelVisible.Should().BeTrue();
    }

    [Theory]
    [InlineData(64, true, 80)]
    [InlineData(64, false, 51)]
    [InlineData(4, false, 4)]
    [InlineData(5, true, 6)]
    [InlineData(500, true, 512)]
    [InlineData(512, true, 512)]
    public void StepBrushSizeMovesByAQuarterWithinTheLimits(double start, bool grow, double expected)
    {
        var vm = Canvas(new FakeDiffusionBackend());
        vm.BrushSize = start;

        vm.StepBrushSize(grow);

        vm.BrushSize.Should().Be(expected);
    }

    // ────────────────────────────── Capabilities ──────────────────────────────

    [Fact]
    public void BothShippedBackendsHonourTheMask()
    {
        StableDiffusionCppBackend.LocalCapabilities.Supports(BackendFeature.Inpainting).Should().BeTrue();
        ManagedComfyUiBackend.EngineCapabilities.Supports(BackendFeature.Inpainting).Should().BeTrue();
    }

    [Fact]
    public void TheMaskButtonSaysWhatItDoes()
    {
        var vm = Canvas(new FakeDiffusionBackend());

        vm.MaskTooltip.Should().StartWith("Add the inpaint mask layer");
        vm.AddMaskCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void ABackendThatCannotInpaintSaysSoOnTheMaskButton()
    {
        var limited = new BackendCapabilities(new Dictionary<BackendFeature, string>
        {
            [BackendFeature.Inpainting] = "This backend cannot inpaint.",
        });
        var vm = Canvas(new FakeDiffusionBackend { Capabilities = limited });
        vm.SelectedBackend = vm.AvailableBackends.First(b => b.Key == CanvasBackendKeys.Engine);

        vm.MaskTooltip.Should().EndWith("This backend cannot inpaint.");
    }
}
