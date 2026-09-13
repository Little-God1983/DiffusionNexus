using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;
using SkiaSharp;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The core→view-model unsaved-changes link (#567). <see cref="ImageEditorViewModel.HasUnsavedChanges"/>
/// is what every discard prompt consults, so the mirror itself, and which saves reset it, are
/// pinned here rather than assumed.
/// </summary>
public sealed class ImageEditorViewModelUnsavedChangesTests : IDisposable
{
    private readonly Mock<IDatasetEventAggregator> _aggregator = new();
    private readonly ImageEditorCore _core = new();
    private readonly byte[] _png;

    public ImageEditorViewModelUnsavedChangesTests()
    {
        _core.SetServices(EditorServiceFactory.Create());
        using var bitmap = new SKBitmap(16, 16, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Gold);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _png = data.ToArray();
    }

    public void Dispose() => _core.Dispose();

    private ImageEditorViewModel CreateViewModel()
    {
        var vm = new ImageEditorViewModel(eventAggregator: _aggregator.Object);
        vm.LoadImage(@"C:\datasets\test\original.png");
        return vm;
    }

    [Fact]
    public void TrackUnsavedChanges_MirrorsTheCore_UntilDisposed()
    {
        var vm = CreateViewModel();
        _core.LoadImage(_png);

        using (vm.TrackUnsavedChanges(_core))
        {
            vm.HasUnsavedChanges.Should().BeFalse();

            _core.AddLayer("scratch");
            vm.HasUnsavedChanges.Should().BeTrue();

            _core.MarkClean();
            vm.HasUnsavedChanges.Should().BeFalse();
        }

        _core.AddLayer("after unwire");
        vm.HasUnsavedChanges.Should().BeFalse("an unwired mirror must not keep following the core");
    }

    [Fact]
    public void TrackUnsavedChanges_PushesTheCurrentStateImmediately()
    {
        var vm = CreateViewModel();
        _core.LoadImage(_png);
        _core.AddLayer("scratch");

        using var _ = vm.TrackUnsavedChanges(_core);

        vm.HasUnsavedChanges.Should().BeTrue("re-wiring after a tab switch must not lose an edited canvas");
    }

    [Fact]
    public async Task SaveOverwrite_MarksTheCanvasClean()
    {
        var vm = CreateViewModel();
        vm.SaveImageFunc = _ => true;
        vm.HasUnsavedChanges = true;
        var cleaned = 0;
        vm.CanvasSaved += (_, _) => cleaned++;

        await vm.SaveOverwriteCommand.ExecuteAsync(null);

        vm.HasUnsavedChanges.Should().BeFalse();
        cleaned.Should().Be(1);
    }

    [Fact]
    public async Task FailedSave_LeavesTheCanvasDirty()
    {
        var vm = CreateViewModel();
        vm.SaveImageFunc = _ => false;
        vm.HasUnsavedChanges = true;

        await vm.SaveOverwriteCommand.ExecuteAsync(null);

        vm.HasUnsavedChanges.Should().BeTrue();
    }

    [Fact]
    public void SendToCaptioning_TempExport_KeepsTheCanvasDirty()
    {
        var vm = CreateViewModel();
        var exported = new List<string>();
        vm.SaveImageFunc = path => { exported.Add(path); return true; };
        vm.HasUnsavedChanges = true;

        vm.SendToCaptioningCommand.Execute(null);

        exported.Should().ContainSingle("the send path exports a temp copy through the same save callback");
        vm.HasUnsavedChanges.Should().BeTrue("a throwaway export is not the user saving their work");
    }

    [Fact]
    public async Task ClearImage_AsksBeforeDiscarding_AndKeepsTheCanvasWhenDeclined()
    {
        var vm = CreateViewModel();
        vm.HasUnsavedChanges = true;
        vm.ClearConfirmRequested += () => Task.FromResult(false);
        var cleared = 0;
        vm.ClearRequested += (_, _) => cleared++;

        await vm.ClearImageCommand.ExecuteAsync(null);

        cleared.Should().Be(0);
        vm.HasImage.Should().BeTrue();
    }

    [Fact]
    public async Task ClearImage_ClearsWhenConfirmed()
    {
        var vm = CreateViewModel();
        vm.HasUnsavedChanges = true;
        vm.ClearConfirmRequested += () => Task.FromResult(true);
        var cleared = 0;
        vm.ClearRequested += (_, _) => cleared++;

        await vm.ClearImageCommand.ExecuteAsync(null);

        cleared.Should().Be(1);
        vm.HasImage.Should().BeFalse();
    }
}
