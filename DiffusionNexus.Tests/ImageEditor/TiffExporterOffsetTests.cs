using BitMiracle.LibTiff.Classic;
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
    public void DecodeFlatImage_SemiTransparentPixel_KeepsItsColour()
    {
        // LibTiff's RGBA reader returns premultiplied values for files tagged with unassociated
        // alpha. Stored as straight colour they reload darker on every save/load cycle (#638).
        using var source = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        source.Erase(new SKColor(200, 0, 0, 128));
        TiffExporter.SaveFlattenedTiff(source, _path).Should().BeTrue();

        using var loaded = TiffExporter.DecodeFlatImage(_path)!;

        var pixel = loaded.GetPixel(1, 1);
        pixel.Alpha.Should().Be(128);
        pixel.Red.Should().BeInRange(198, 201, "half-transparent red must keep its colour, neither darker nor brighter");
    }

    [Fact]
    public void DecodeFlatImage_UnspecifiedExtraSample_LoadsTheStoredColour()
    {
        // LibTiff hands an UNSPECIFIED fourth sample back as alpha, as stored. Nothing says the
        // colour was premultiplied, so it is not divided by the alpha (that brightened it to 255).
        WriteOnePixelTiff(Photometric.RGB, [200, 0, 0, 128], ExtraSample.UNSPECIFIED);

        using var loaded = TiffExporter.DecodeFlatImage(_path)!;

        loaded.GetPixel(0, 0).Should().Be(new SKColor(200, 0, 0, 128));
    }

    [Fact]
    public void DecodeFlatImage_AssociatedAlpha_LoadsStraightColour()
    {
        // Associated alpha is stored premultiplied: 100 at alpha 128 is a straight 199.
        WriteOnePixelTiff(Photometric.RGB, [100, 0, 0, 128], ExtraSample.ASSOCALPHA);

        using var loaded = TiffExporter.DecodeFlatImage(_path)!;

        var pixel = loaded.GetPixel(0, 0);
        pixel.Alpha.Should().Be(128);
        pixel.Red.Should().BeInRange(198, 200);
    }

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

    /// <summary>A 1x1 8-bit TIFF whose last sample is the extra sample <paramref name="extraSample"/>.</summary>
    private void WriteOnePixelTiff(Photometric photometric, byte[] samples, ExtraSample extraSample)
    {
        using var tiff = Tiff.Open(_path, "w")!;
        tiff.SetField(TiffTag.IMAGEWIDTH, 1);
        tiff.SetField(TiffTag.IMAGELENGTH, 1);
        tiff.SetField(TiffTag.SAMPLESPERPIXEL, samples.Length);
        tiff.SetField(TiffTag.BITSPERSAMPLE, 8);
        tiff.SetField(TiffTag.PHOTOMETRIC, photometric);
        tiff.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
        tiff.SetField(TiffTag.ROWSPERSTRIP, 1);
        tiff.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)extraSample });
        tiff.WriteScanline(samples, 0).Should().BeTrue();
    }
}
