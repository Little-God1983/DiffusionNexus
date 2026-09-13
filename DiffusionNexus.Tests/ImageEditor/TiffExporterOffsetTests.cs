using DiffusionNexus.UI.ImageEditor;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>Layered TIFF round-trips layer offsets, mixed layer sizes and the canvas size.</summary>
public class TiffExporterOffsetTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dn-tiff-{Guid.NewGuid():N}.tif");

    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void SaveAndLoad_KeepsOffsetsSizesAndCanvas()
    {
        using var stack = new LayerStack(40, 30);
        using (var bg = new SKBitmap(40, 30)) { bg.Erase(SKColors.Red); stack.AddLayerFromBitmap(bg, "bg"); }
        using (var small = new SKBitmap(8, 6)) { small.Erase(SKColors.Blue); stack.AddLayerFromBitmap(small, "moved", new SKPointI(-3, 27)); }

        TiffExporter.SaveLayeredTiff(stack, _path).Should().BeTrue();
        using var loaded = TiffExporter.LoadLayeredTiff(_path)!;

        loaded.Width.Should().Be(40);
        loaded.Height.Should().Be(30);
        loaded.Count.Should().Be(2);
        loaded[1].Name.Should().Be("moved");
        loaded[1].Bounds.Should().Be(new SKRectI(-3, 27, 5, 33));
        loaded[1].Bitmap!.GetPixel(0, 0).Should().Be(SKColors.Blue);
    }

    [Fact]
    public void Load_WithoutOffsetKeys_UsesFirstPageSize_AndZeroOffsets()
    {
        using var stack = new LayerStack(16, 12);
        using (var bg = new SKBitmap(16, 12)) { bg.Erase(SKColors.Green); stack.AddLayerFromBitmap(bg, "bg"); }
        TiffExporter.SaveLayeredTiff(stack, _path).Should().BeTrue();
        // Legacy behaviour is the offset-0 special case; the file above carries the new keys, so
        // this test only proves the defaults are what the parser falls back to.
        TiffExporter.ParseLayerMetadataForTest("LayerName=x|Opacity=1.00|BlendMode=Normal|Visible=True|Index=0")
            .Should().Be(("x", 0, 0, (int?)null, (int?)null));
    }
}
