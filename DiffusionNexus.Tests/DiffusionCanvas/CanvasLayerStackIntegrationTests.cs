using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using DiffusionNexus.UI.ViewModels.DiffusionCanvas;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.DiffusionCanvas;

/// <summary>The layer stack inside the canvas: what the model sees follows what the user shows (#594).</summary>
public class CanvasLayerStackIntegrationTests
{
    private static DiffusionCanvasViewModel Canvas(FakeDiffusionBackend backend)
    {
        var vm = new DiffusionCanvasViewModel(backend) { PromptText = "a lighthouse at dusk" };
        vm.BitmapDecoder = _ =>
        {
            var sentinel = (Bitmap)RuntimeHelpers.GetUninitializedObject(typeof(Bitmap));
            GC.SuppressFinalize(sentinel);
            return sentinel;
        };
        vm.OutputsWriter = (bytes, seed) => $"C:\\fake-outputs\\{seed}-{bytes.Length}.png";
        return vm;
    }

    [Fact]
    public async Task AcceptingACandidate_AddsANamedSelectedTopLayer()
    {
        var vm = Canvas(new FakeDiffusionBackend());
        vm.BatchCount = 2;
        await vm.GenerateCommand.ExecuteAsync(null);

        vm.Staging.AcceptAllCommand.Execute(null);

        vm.Layers.DisplayLayers.Select(l => l.Name).Should().Equal("Layer 2", "Layer 1");
        vm.Layers.SelectedLayer.Should().BeSameAs(vm.Frames[^1]);
    }

    [Fact]
    public void HidingTheLayerUnderTheBox_TurnsTheReadoutBackToTextToImage()
    {
        var vm = new DiffusionCanvasViewModel();
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        var frame = new GenerationFrameViewModel { Width = 512, Height = 512, ImagePath = "x.png" };
        vm.Frames.Add(frame);
        vm.IsRegionOccupied.Should().BeTrue();

        frame.IsVisible = false;
        vm.IsRegionOccupied.Should().BeFalse("a hidden layer is not what the model sees");
        vm.RegionModeText.Should().Contain("Text to image");

        frame.IsVisible = true;
        frame.Opacity = 0;
        vm.IsRegionOccupied.Should().BeFalse("a fully transparent layer is not either");

        frame.Opacity = 0.4;
        vm.IsRegionOccupied.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_OverAHiddenLayerSendsNoInitImage()
    {
        using var canvas = new TempCanvasFile(512, 512, SKColors.White);
        var backend = new FakeDiffusionBackend();
        var vm = Canvas(backend);
        vm.Box.SetSize(512, 512);
        vm.Box.SetPosition(0, 0);
        var frame = canvas.AsFrame(0, 0, 512, 512);
        vm.Frames.Add(frame);
        frame.IsVisible = false;

        await vm.GenerateCommand.ExecuteAsync(null);

        backend.LastRequest!.InitImage.Should().BeNull("the only layer under the box is hidden");
    }

    [Fact]
    public void ClearCanvas_KeepsLockedLayersAndIsDisabledWhenEverythingIsLocked()
    {
        var vm = new DiffusionCanvasViewModel();
        var keep = new GenerationFrameViewModel { Name = "keep" };
        vm.Frames.Add(keep);
        vm.Frames.Add(new GenerationFrameViewModel { Name = "gone" });
        keep.IsLocked = true;

        vm.ClearCanvasCommand.CanExecute(null).Should().BeTrue();
        vm.ClearCanvasCommand.Execute(null);

        vm.Frames.Should().Equal(keep);
        vm.ClearCanvasCommand.CanExecute(null).Should().BeFalse("only a locked layer is left");
    }

    [Fact]
    public void DeleteFrameCommand_CannotExecuteForALockedLayer()
    {
        var vm = new DiffusionCanvasViewModel();
        var frame = new GenerationFrameViewModel { Name = "keep", IsLocked = true };
        vm.Frames.Add(frame);

        vm.DeleteFrameCommand!.CanExecute(frame).Should().BeFalse();
        vm.DeleteFrameCommand.Execute(frame);

        vm.Frames.Should().Contain(frame);
    }

    [Fact]
    public void TheLayerPanelIsShownByDefault()
    {
        new DiffusionCanvasViewModel().IsLayerPanelVisible.Should().BeTrue();
    }
}
