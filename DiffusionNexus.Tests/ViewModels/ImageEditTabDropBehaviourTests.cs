using System.Collections.ObjectModel;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;
using SkiaSharp;
using Xunit;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// Issue #567: dropping images onto an editor that already shows one must ask
/// Replace / Add as Layer / Add to Selection instead of silently replacing the canvas, and
/// anything that would replace an edited canvas asks to discard first.
/// </summary>
public sealed class ImageEditTabDropBehaviourTests : IDisposable
{
    private const string Replace = "Replace";
    private const string AddAsLayer = "Add as Layer";
    private const string AddToSelection = "Add to Selection";

    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory();
    private readonly Mock<IDialogService> _dialogs = new(MockBehavior.Strict);
    private readonly ImageEditTabViewModel _vm;
    private readonly string _first;
    private readonly string _second;
    private readonly string _third;

    public ImageEditTabDropBehaviourTests()
    {
        _first = WritePng("first.png");
        _second = WritePng("second.png");
        _third = WritePng("third.png");

        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel>());
        _vm = new ImageEditTabViewModel(Mock.Of<IDatasetEventAggregator>(), state.Object)
        {
            DialogService = _dialogs.Object,
        };
    }

    public void Dispose()
    {
        _vm.Dispose();
        _tempDir.Delete(recursive: true);
    }

    // ---- drop onto an empty editor ------------------------------------------------------------

    [Fact]
    public async Task Drop_OnEmptyEditor_LoadsWithoutAsking()
    {
        await _vm.HandleDroppedImagesAsync([_first, _second]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _vm.FilteredEditorImages.Select(i => i.ImagePath).Should().Equal(_first, _second);
        _dialogs.VerifyNoOtherCalls();
    }

    // ---- drop onto an editor that already shows an image --------------------------------------

    [Fact]
    public async Task Drop_OnLoadedEditor_OffersReplaceLayerAndSelection()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        string[]? offered = null;
        ExpectOptions(options => { offered = options; return -1; });

        await _vm.HandleDroppedImagesAsync([_second]);

        offered.Should().Contain([Replace, AddAsLayer, AddToSelection]);
    }

    [Fact]
    public async Task Drop_Cancelled_LeavesEverythingAlone()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        ExpectOptions(_ => -1);

        await _vm.HandleDroppedImagesAsync([_second]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _vm.FilteredEditorImages.Select(i => i.ImagePath).Should().Equal(_first);
    }

    [Fact]
    public async Task Drop_Replace_OnCleanCanvas_LoadsWithoutDiscardPrompt()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        ExpectOptions(o => Array.IndexOf(o, Replace));

        await _vm.HandleDroppedImagesAsync([_second, _third]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_second);
        _vm.FilteredEditorImages.Select(i => i.ImagePath).Should().Equal(_second, _third);
    }

    [Fact]
    public async Task Drop_Replace_OnEditedCanvas_AsksToDiscard_AndKeepsCanvasWhenDeclined()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        _vm.ImageEditor.HasUnsavedChanges = true;
        ExpectOptions(o => Array.IndexOf(o, Replace));
        _dialogs.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);

        await _vm.HandleDroppedImagesAsync([_second]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _dialogs.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task Drop_Replace_OnEditedCanvas_LoadsWhenDiscardConfirmed()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        _vm.ImageEditor.HasUnsavedChanges = true;
        ExpectOptions(o => Array.IndexOf(o, Replace));
        _dialogs.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(true);

        await _vm.HandleDroppedImagesAsync([_second]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_second);
    }

    [Fact]
    public async Task Drop_AddAsLayer_RequestsOneLayerPerFile_AndKeepsCanvas()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        _vm.ImageEditor.HasUnsavedChanges = true;
        ExpectOptions(o => Array.IndexOf(o, AddAsLayer));
        IReadOnlyList<string>? requested = null;
        _vm.ImageEditor.AddLayersFromFilesRequested += (_, paths) => requested = paths;

        await _vm.HandleDroppedImagesAsync([_second, _third]);

        requested.Should().Equal(_second, _third);
        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _vm.FilteredEditorImages.Select(i => i.ImagePath).Should().Equal(_first);
        _dialogs.Verify(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Drop_AddToSelection_AppendsThumbnails_AndKeepsCanvas()
    {
        await _vm.HandleDroppedImagesAsync([_first]);
        _vm.ImageEditor.HasUnsavedChanges = true;
        ExpectOptions(o => Array.IndexOf(o, AddToSelection));

        await _vm.HandleDroppedImagesAsync([_second, _first, _third]);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _vm.FilteredEditorImages.Select(i => i.ImagePath).Should().Equal(_first, _second, _third);
        _vm.SelectedEditorImage!.ImagePath.Should().Be(_first);
        _vm.SelectedEditorDataset!.IsTemporary.Should().BeTrue();
        _vm.SelectedEditorDataset.ImageCount.Should().Be(3);
    }

    // ---- thumbnail click ----------------------------------------------------------------------

    [Fact]
    public async Task ThumbnailClick_OnEditedCanvas_KeepsCanvasWhenDiscardDeclined()
    {
        await _vm.HandleDroppedImagesAsync([_first, _second]);
        _vm.ImageEditor.HasUnsavedChanges = true;
        _dialogs.Setup(d => d.ShowConfirmAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(false);
        var second = _vm.FilteredEditorImages.Single(i => i.ImagePath == _second);

        await _vm.LoadEditorImageCommand.ExecuteAsync(second);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
        _vm.SelectedEditorImage!.ImagePath.Should().Be(_first);
    }

    [Fact]
    public async Task ThumbnailClick_OnCleanCanvas_LoadsWithoutAsking()
    {
        await _vm.HandleDroppedImagesAsync([_first, _second]);
        var second = _vm.FilteredEditorImages.Single(i => i.ImagePath == _second);

        await _vm.LoadEditorImageCommand.ExecuteAsync(second);

        _vm.ImageEditor.CurrentImagePath.Should().Be(_second);
        _dialogs.VerifyNoOtherCalls();
    }

    // ---- context menu: Add as Layer to Canvas -------------------------------------------------

    [Fact]
    public void AddAsLayerCommand_IsDisabled_WithoutACanvas()
    {
        var item = DatasetImageViewModel.FromFile(_first);

        _vm.AddAsLayerCommand.CanExecute(item).Should().BeFalse();
    }

    [Fact]
    public async Task AddAsLayerCommand_RequestsTheClickedImageAsALayer()
    {
        await _vm.HandleDroppedImagesAsync([_first, _second]);
        var second = _vm.FilteredEditorImages.Single(i => i.ImagePath == _second);
        IReadOnlyList<string>? requested = null;
        _vm.ImageEditor.AddLayersFromFilesRequested += (_, paths) => requested = paths;

        _vm.AddAsLayerCommand.CanExecute(second).Should().BeTrue();
        _vm.AddAsLayerCommand.Execute(second);

        requested.Should().Equal(_second);
        _vm.ImageEditor.CurrentImagePath.Should().Be(_first);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private void ExpectOptions(Func<string[], int> choose)
    {
        _dialogs
            .Setup(d => d.ShowOptionsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string _, string _, string[] options) => Task.FromResult(choose(options)));
    }

    private string WritePng(string name)
    {
        using var bitmap = new SKBitmap(8, 8, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Olive);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_tempDir.FullName, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
