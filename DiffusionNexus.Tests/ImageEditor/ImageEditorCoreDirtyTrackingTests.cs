using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// <see cref="ImageEditorCore.IsDirty"/> is what the Image Edit tab consults before it replaces
/// the canvas (thumbnail click, drop "Replace", Open Image). A false positive nags the user with
/// a discard prompt on a clean canvas; a false negative silently throws work away — so both
/// edges are pinned here.
/// </summary>
public class ImageEditorCoreDirtyTrackingTests : IDisposable
{
    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir;
    private readonly byte[] _png;

    public ImageEditorCoreDirtyTrackingTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _png = data.ToArray();
    }

    public void Dispose()
    {
        _sut.Dispose();
        _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void FreshlyLoadedImage_IsNotDirty()
    {
        _sut.LoadImage(_png).Should().BeTrue();

        _sut.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Edit_MarksDirty_AndRaisesIsDirtyChanged()
    {
        _sut.LoadImage(_png);
        var raised = 0;
        _sut.IsDirtyChanged += (_, _) => raised++;

        _sut.AddLayer("scratch");

        _sut.IsDirty.Should().BeTrue();
        raised.Should().Be(1, "the flag flips once; further edits must not spam the event");
        _sut.AddLayer("scratch 2");
        raised.Should().Be(1);
    }

    [Fact]
    public void Save_ClearsDirty()
    {
        _sut.LoadImage(_png);
        _sut.AddLayer("scratch");
        var path = Path.Combine(_tempDir.FullName, "out.png");

        _sut.SaveImage(path).Should().BeTrue();

        _sut.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void FailedSave_KeepsDirty()
    {
        _sut.LoadImage(_png);
        _sut.AddLayer("scratch");
        // The document service creates missing directories, so block it with a file where the
        // parent directory would have to go.
        var blocker = Path.Combine(_tempDir.FullName, "blocker");
        File.WriteAllText(blocker, string.Empty);
        var path = Path.Combine(blocker, "out.png");

        _sut.SaveImage(path).Should().BeFalse();

        _sut.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void ReloadingAnotherImage_ClearsDirty()
    {
        _sut.LoadImage(_png);
        _sut.AddLayer("scratch");

        _sut.LoadImage(_png).Should().BeTrue();

        _sut.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void Clear_ClearsDirty()
    {
        _sut.LoadImage(_png);
        _sut.AddLayer("scratch");

        _sut.Clear();

        _sut.IsDirty.Should().BeFalse();
    }

    [Fact]
    public void ClearingAPreview_IsNotAnEdit()
    {
        _sut.LoadImage(_png);

        _sut.ClearPreview();

        _sut.IsDirty.Should().BeFalse("closing a tool panel without applying must not trigger a discard prompt");
    }

    [Fact]
    public void PreviewingThenCancelling_IsNotAnEdit()
    {
        _sut.LoadImage(_png);

        _sut.SetColorBalancePreview(new ColorBalanceSettings { MidtonesCyanRed = 40 }).Should().BeTrue();
        _sut.SetBrightnessContrastPreview(new BrightnessContrastSettings { Brightness = 20 }).Should().BeTrue();
        _sut.SetBackgroundFillPreview(new BackgroundFillSettings()).Should().BeTrue();
        _sut.ClearPreview();

        _sut.IsDirty.Should().BeFalse("nudging a slider and pressing Cancel changes no pixels");
    }

    [Fact]
    public void ApplyingAPreview_IsAnEdit()
    {
        _sut.LoadImage(_png);

        _sut.ApplyColorBalance(new ColorBalanceSettings { MidtonesCyanRed = 40 }).Should().BeTrue();

        _sut.IsDirty.Should().BeTrue();
    }

    [Fact]
    public void ReloadingACleanCanvas_RaisesNoDirtyTransition()
    {
        _sut.LoadImage(_png);
        var raised = 0;
        _sut.IsDirtyChanged += (_, _) => raised++;

        _sut.LoadImage(_png);
        _sut.Clear();

        raised.Should().Be(0, "loads and Clear must not flicker the flag true then false");
    }
}
