using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using SkiaSharp;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Which file the editor's hand-offs (Upscale, Add To…, Send To…) give other tools (#586): the
/// user's own file while the canvas is unchanged, otherwise a temp copy with the edits.
/// </summary>
public sealed class ImageEditorHandOffTests : IDisposable
{
    private readonly ImageEditorCore _core = new();
    private readonly DirectoryInfo _tempDir;
    private readonly string _file;

    public ImageEditorHandOffTests()
    {
        _core.SetServices(EditorServiceFactory.Create());
        _tempDir = Directory.CreateTempSubdirectory();
        _file = Path.Combine(_tempDir.FullName, "original.png");

        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Gold);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(_file, data.ToArray());
    }

    public void Dispose()
    {
        _core.Dispose();
        _tempDir.Delete(recursive: true);
    }

    /// <summary>A view model wired to the core the way the Image Edit view wires it.</summary>
    private (ImageEditorViewModel Vm, List<string> Exports, IDisposable Link) CreateWired()
    {
        var vm = new ImageEditorViewModel(eventAggregator: Mock.Of<IDatasetEventAggregator>());
        var exports = new List<string>();
        vm.SaveImageFunc = (path, fill) => { exports.Add(path); return _core.SaveImage(path, fillColor: fill?.ToSKColor()); };
        var link = vm.TrackFileMatch(_core);
        _core.LoadImage(_file).Should().BeTrue();
        vm.LoadImage(_file);
        return (vm, exports, link);
    }

    [Fact]
    public void UneditedCanvas_HandsOverTheFileItself()
    {
        var (vm, exports, link) = CreateWired();
        using var _ = link;

        var handOff = vm.PrepareHandOff("upscale");

        handOff.Should().Be(new ImageHandOff(_file, IsCopy: false));
        exports.Should().BeEmpty("nothing should be re-encoded");
        vm.MatchesOriginalFile.Should().BeTrue();
    }

    [Fact]
    public void EditedCanvas_HandsOverACopy()
    {
        var (vm, exports, link) = CreateWired();
        using var _ = link;
        _core.AddLayer("scratch");

        var handOff = vm.PrepareHandOff("upscale");

        handOff!.IsCopy.Should().BeTrue();
        handOff.Path.Should().NotBe(_file);
        exports.Should().Equal(handOff.Path);
        vm.MatchesOriginalFile.Should().BeFalse();
        File.Delete(handOff.Path);
    }

    [Fact]
    public void ExportedCanvas_StillHandsOverACopy()
    {
        // The trap from the issue: Export clears the unsaved-changes flag, but the loaded file
        // still lacks the edits.
        var (vm, exports, link) = CreateWired();
        using var _ = link;
        _core.AddLayer("scratch");
        _core.MarkClean();

        var handOff = vm.PrepareHandOff("act");

        handOff!.IsCopy.Should().BeTrue();
        File.Delete(handOff.Path);
    }

    [Fact]
    public void SendToBatchUpscale_PublishesTheOriginal_WhenUnedited()
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        var vm = new ImageEditorViewModel(eventAggregator: aggregator.Object);
        vm.SaveImageFunc = (_, _) => throw new InvalidOperationException("an unedited canvas must not be exported");
        using var link = vm.TrackFileMatch(_core);
        _core.LoadImage(_file);
        vm.LoadImage(_file);

        vm.SendToBatchUpscaleCommand.Execute(null);

        aggregator.Verify(a => a.PublishNavigateToBatchUpscale(
            It.Is<NavigateToBatchUpscaleEventArgs>(e => e.ImagePaths!.Single() == _file)), Times.Once);
    }

    [Fact]
    public void FailedLoadOfAnotherImage_IsNotTheMatchedFile()
    {
        // The view leaves the core on the previous image when a decode fails, while the view
        // model already shows the new path.
        var (vm, _, link) = CreateWired();
        using var _ = link;

        vm.LoadImage(Path.Combine(_tempDir.FullName, "corrupt.png"));

        vm.MatchesOriginalFile.Should().BeFalse();
        vm.WouldHandOverOriginal().Should().BeFalse();
    }

    [Fact]
    public void FailedLoadOfAnotherImage_HandsNothingOver()
    {
        // A copy would give out the previous image's pixels under the new image's name.
        var (vm, exports, link) = CreateWired();
        using var _ = link;
        vm.LoadImage(Path.Combine(_tempDir.FullName, "corrupt.png"));

        var handOff = vm.PrepareHandOff("act");

        handOff.Should().BeNull();
        exports.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("corrupt.png is not loaded");
    }

    [Fact]
    public void Video_IsHandedOverAsItsFile()
    {
        var (vm, exports, link) = CreateWired();
        using var _ = link;
        var video = Path.Combine(_tempDir.FullName, "clip.mp4");
        vm.LoadImage(video);

        var handOff = vm.PrepareHandOff("upscale");

        handOff.Should().Be(new ImageHandOff(video, IsCopy: false));
        exports.Should().BeEmpty("the canvas still holds the previous image, not the video");
    }

    [Fact]
    public void PendingMove_IsReportedBeforeAnyHandOff()
    {
        var (vm, exports, link) = CreateWired();
        using var _ = link;
        _core.LayerTransformTool.IsActive = true;
        _core.ArmLayerTransform();
        _core.LayerTransformTool.Nudge(3, 0);

        vm.MatchesOriginalFile.Should().BeTrue("the mirror does not follow tool state");
        vm.WouldHandOverOriginal().Should().BeFalse();
        exports.Should().BeEmpty("asking must not commit the Move");
    }

    [Fact]
    public void Unwired_FallsBackToTheCopy()
    {
        var (vm, _, link) = CreateWired();
        link.Dispose();

        var handOff = vm.PrepareHandOff("act");

        handOff!.IsCopy.Should().BeTrue("without the core link nothing proves the canvas is unchanged");
        vm.MatchesOriginalFile.Should().BeFalse();
        File.Delete(handOff.Path);
    }

    [Fact]
    public async Task SaveOverwrite_ReportsTheFile_SoTheCanvasMatchesItAgain()
    {
        var vm = new ImageEditorViewModel(eventAggregator: Mock.Of<IDatasetEventAggregator>());
        vm.LoadImage(_file);
        vm.SaveImageFunc = (_, _) => true;
        CanvasSavedEventArgs? saved = null;
        vm.CanvasSaved += (_, e) => saved = e;

        await vm.SaveOverwriteCommand.ExecuteAsync(null);

        saved!.SavedOverPath.Should().Be(_file);
    }

    [Fact]
    public async Task SaveOverwrite_WithATransparencyFill_DoesNotClaimTheFileMatches()
    {
        var jpeg = Path.ChangeExtension(_file, ".jpg");
        var vm = new ImageEditorViewModel(eventAggregator: Mock.Of<IDatasetEventAggregator>());
        vm.LoadImage(jpeg);
        vm.SaveImageFunc = (_, _) => true;
        vm.HasTransparencyFunc = () => true;
        vm.JpegTransparencyPromptRequested += () => Task.FromResult<TransparencyFill?>(TransparencyFill.White);
        CanvasSavedEventArgs? saved = null;
        vm.CanvasSaved += (_, e) => saved = e;

        await vm.SaveOverwriteCommand.ExecuteAsync(null);

        saved.Should().NotBeNull();
        saved!.SavedOverPath.Should().BeNull("the file holds white where the canvas is transparent");
    }
}
