using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// Regression tests for the SkiaSharp use-after-dispose crash that killed the app with
/// <c>System.ExecutionEngineException</c> inside <c>SKBitmap.Info</c> when the user switched
/// to another image right after saving.
/// <para>
/// Avalonia runs <c>ICustomDrawOperation.Render</c> — and therefore
/// <see cref="ImageEditorCore.RenderWithZoom"/> — on the compositor's <b>render thread</b>, while
/// image switching, clearing and every layer edit happen on the UI thread. Every bitmap the render
/// thread can reach must therefore only be freed while <see cref="ImageEditorCore.RenderLock"/> is
/// held; otherwise Skia dereferences a freed <c>sk_bitmap_t</c> and the process dies with an access
/// violation that no <c>catch</c> can intercept.
/// </para>
/// </summary>
public class ImageEditorCoreRenderRaceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir;
    private readonly string _imageA;
    private readonly string _imageB;

    public ImageEditorCoreRenderRaceTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
        _imageA = WritePng("a.png", 320, 240, SKColors.Red);
        _imageB = WritePng("b.png", 200, 400, SKColors.Blue);
    }

    public void Dispose()
    {
        try { _tempDir.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private string WritePng(string name, int width, int height, SKColor fill)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(fill);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    private static ImageEditorCore CreateCore()
    {
        var core = new ImageEditorCore();
        core.SetServices(EditorServiceFactory.Create());
        return core;
    }

    /// <summary>
    /// Reproduces the reported crash: a repaint is in flight on the render thread while the UI
    /// thread switches to another image. Before the fix this tore down the layer stack and the
    /// working bitmap out from under <c>LayerCompositor.CompositeToCanvas</c> and took the whole
    /// process down with an access violation rather than failing this assertion.
    /// </summary>
    [Fact]
    public void WhenTheImageIsSwitchedWhileARepaintIsInFlightThenNothingIsUsedAfterDisposal()
    {
        using var core = CreateCore();
        core.LoadImage(_imageA).Should().BeTrue();

        RenderWhile(core, () =>
        {
            for (var i = 0; i < 200; i++)
            {
                core.LoadImage(i % 2 == 0 ? _imageB : _imageA).Should().BeTrue();
            }
        });
    }

    /// <summary>
    /// Same race through the other teardown paths a user reaches from the toolbar while the
    /// canvas keeps repainting.
    /// </summary>
    [Fact]
    public void WhenTheEditorIsResetOrClearedWhileARepaintIsInFlightThenNothingIsUsedAfterDisposal()
    {
        using var core = CreateCore();
        core.LoadImage(_imageA).Should().BeTrue();

        RenderWhile(core, () =>
        {
            for (var i = 0; i < 100; i++)
            {
                core.ResetToOriginal();
                core.Clear();
                core.LoadImage(_imageA).Should().BeTrue();
            }
        });
    }

    /// <summary>
    /// Layer edits free layer bitmaps the compositor is drawing from, so they need the same
    /// protection as a full image switch.
    /// </summary>
    [Fact]
    public void WhenLayersAreEditedWhileARepaintIsInFlightThenNothingIsUsedAfterDisposal()
    {
        using var core = CreateCore();
        core.LoadImage(_imageA).Should().BeTrue();

        RenderWhile(core, () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var added = core.AddLayer($"Layer {i}");
                core.RotateRight();
                core.FlattenAllLayers();
                if (added is not null)
                {
                    core.RemoveLayer(added);
                }
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="mutate"/> on this thread (standing in for the UI thread) while a
    /// second thread hammers <see cref="ImageEditorCore.RenderWithZoom"/> the way Avalonia's
    /// render thread does. Any exception on either side fails the test; a use-after-dispose
    /// takes the process down, which is exactly the reported symptom.
    /// </summary>
    private static void RenderWhile(ImageEditorCore core, Action mutate)
    {
        using var stop = new ManualResetEventSlim(false);
        Exception? renderFailure = null;

        var renderThread = new Thread(() =>
        {
            try
            {
                using var target = new SKBitmap(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(target);

                while (!stop.IsSet)
                {
                    core.RenderWithZoom(canvas, 400, 400, SKColors.Black);
                }
            }
            catch (Exception ex)
            {
                renderFailure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "test-render-thread"
        };

        renderThread.Start();
        try
        {
            mutate();
        }
        finally
        {
            stop.Set();
            renderThread.Join(TimeSpan.FromSeconds(30)).Should().BeTrue("the render thread must not deadlock");
        }

        renderFailure.Should().BeNull("rendering must never observe a disposed bitmap or a torn layer list");
    }
}
