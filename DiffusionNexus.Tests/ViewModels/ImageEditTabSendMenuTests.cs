using System.Collections.ObjectModel;
using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;
using SkiaSharp;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The Image Edit tab's Add To… / Send To… bar (#586). While the canvas is the file on disk it hands
/// over that file; otherwise a Skia re-encoded temp copy, which carries none of the original PNG
/// text chunks, so the Batch Metadata Distiller (which exists to read exactly those chunks) is only
/// offered while the original would be handed over.
/// </summary>
public sealed class ImageEditTabSendMenuTests : IDisposable
{
    private readonly ImageEditorCore _core = new();
    private readonly DirectoryInfo _tempDir;
    private readonly string _file;

    public ImageEditTabSendMenuTests()
    {
        _core.SetServices(EditorServiceFactory.Create());
        _tempDir = Directory.CreateTempSubdirectory();
        _file = Path.Combine(_tempDir.FullName, "original.png");

        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(_file, data.ToArray());
    }

    public void Dispose()
    {
        _core.Dispose();
        _tempDir.Delete(recursive: true);
    }

    private static ImageEditTabViewModel CreateTab()
    {
        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        return new ImageEditTabViewModel(Mock.Of<IDatasetEventAggregator>(), state.Object);
    }

    /// <summary>Wires the tab's editor to the core the way the view does, and loads the file.</summary>
    private IDisposable LoadWired(ImageEditTabViewModel vm)
    {
        var editor = vm.ImageEditor;
        editor.SaveImageFunc = (path, fill) => _core.SaveImage(path, fillColor: fill?.ToSKColor());
        var link = editor.TrackFileMatch(_core);
        _core.LoadImage(_file).Should().BeTrue();
        editor.LoadImage(_file);
        return link;
    }

    [Fact]
    public void SendMenu_HidesBatchMetadataDistiller_WithoutAnImage()
    {
        var vm = CreateTab();

        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeFalse();
        vm.ImageActions.ShowWorkflowsMenu.Should().BeTrue("the generation workflows still apply to an edited image");
    }

    [Fact]
    public void SendMenu_OffersBatchMetadataDistiller_OnlyWhileTheCanvasIsTheFile()
    {
        var vm = CreateTab();
        using var _ = LoadWired(vm);

        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeTrue();

        _core.AddLayer("scratch");
        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeFalse();

        _core.ResetToOriginal();
        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeTrue();
    }

    [Fact]
    public void SendMenu_HidesBatchMetadataDistiller_ForAVideo()
    {
        var vm = CreateTab();
        using var _ = LoadWired(vm);

        vm.ImageEditor.LoadImage(Path.Combine(_tempDir.FullName, "clip.mp4"));

        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeFalse(
            "the canvas still matches the image shown before the video");
    }

    [Fact]
    public async Task SendToMetadataDistiller_RefusesWhileAToolHasUnfinishedWork()
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        var vm = new ImageEditTabViewModel(aggregator.Object, state.Object);
        using var _ = LoadWired(vm);
        _core.LayerTransformTool.IsActive = true;
        _core.ArmLayerTransform();
        _core.LayerTransformTool.Nudge(3, 0);

        vm.ImageActions.ShowSendToMetadataDistiller.Should().BeTrue("the entry follows the edit state only");
        await vm.ImageActions.SendToWorkflowCommand.ExecuteAsync("batch-metadata-distiller");

        aggregator.Verify(a => a.PublishNavigateToWorkflow(It.IsAny<NavigateToWorkflowEventArgs>()), Times.Never);
        vm.ImageActions.StatusMessage.Should().Contain("no longer matches");
        _core.LayerTransformTool.HasTransform.Should().BeTrue("refusing must not commit the Move");
    }

    [Fact]
    public async Task SendToMetadataDistiller_SendsTheOriginal_WhenUnchanged()
    {
        var aggregator = new Mock<IDatasetEventAggregator>();
        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        var vm = new ImageEditTabViewModel(aggregator.Object, state.Object);
        using var _ = LoadWired(vm);

        await vm.ImageActions.SendToWorkflowCommand.ExecuteAsync("batch-metadata-distiller");

        aggregator.Verify(a => a.PublishNavigateToWorkflow(
            It.Is<NavigateToWorkflowEventArgs>(e => e.ImagePaths!.Single() == _file)), Times.Once);
    }

    [Fact]
    public async Task AddTo_UneditedCanvas_GivesTheOriginal_AndNeverDeletesIt()
    {
        var vm = CreateTab();
        using var _ = LoadWired(vm);

        var paths = await vm.ImageActions.PathProvider!();

        paths.Paths.Should().Equal(_file);
        paths.Cleanup.Should().BeNull("the cleanup deletes the file it is given");
        paths.KeepInPlace.Should().BeTrue("Add must not move the file the editor has open");
        File.Exists(_file).Should().BeTrue();
    }

    [Fact]
    public async Task AddTo_EditedCanvas_GivesACopy_ThatTheCleanupDeletes()
    {
        var vm = CreateTab();
        using var _ = LoadWired(vm);
        _core.AddLayer("scratch");

        var paths = await vm.ImageActions.PathProvider!();

        var copy = paths.Paths.Should().ContainSingle().Subject;
        copy.Should().NotBe(_file);
        paths.Cleanup.Should().NotBeNull();
        paths.KeepInPlace.Should().BeFalse();

        paths.Cleanup!();

        File.Exists(copy).Should().BeFalse();
        File.Exists(_file).Should().BeTrue();
    }
}
