using System.Text;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: more than half of the Civitai originals that carry A1111 parameters are JPEGs, where the
/// text sits in the EXIF UserComment. Every sampled one (2026-10-27) was <c>UNICODE\0</c> + UTF-16
/// big-endian inside a little-endian TIFF, so the text's byte order is not the TIFF's.
/// </summary>
public sealed class JpegExifReaderTests : IDisposable
{
    private const string Text = "masterpiece\nSteps: 20, Sampler: Euler a, Seed: 1";
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-exif").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".jpg");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void UserComment_Utf16BigEndian_InLittleEndianTiff_IsRead()
    {
        var path = Write(RecipeTestFiles.JpegWithUserComment(false, "UNICODE\0"u8.ToArray(), Encoding.BigEndianUnicode.GetBytes(Text)));

        JpegExifReader.ReadUserComment(path).Should().Be(Text);
    }

    [Fact]
    public void UserComment_Utf16LittleEndian_IsRead()
    {
        var path = Write(RecipeTestFiles.JpegWithUserComment(false, "UNICODE\0"u8.ToArray(), Encoding.Unicode.GetBytes(Text)));

        JpegExifReader.ReadUserComment(path).Should().Be(Text);
    }

    [Fact]
    public void UserComment_InBigEndianTiff_IsRead()
    {
        var path = Write(RecipeTestFiles.JpegWithUserComment(true, "UNICODE\0"u8.ToArray(), Encoding.BigEndianUnicode.GetBytes(Text)));

        JpegExifReader.ReadUserComment(path).Should().Be(Text);
    }

    [Fact]
    public void UserComment_AsciiPrefix_IsRead()
    {
        var path = Write(RecipeTestFiles.JpegWithUserComment(false, "ASCII\0\0\0"u8.ToArray(), Encoding.UTF8.GetBytes(Text + "\0")));

        JpegExifReader.ReadUserComment(path).Should().Be(Text);
    }

    [Fact]
    public void Jpeg_WithoutExif_ReturnsNull()
    {
        JpegExifReader.ReadUserComment(Write([0xFF, 0xD8, 0xFF, 0xD9])).Should().BeNull();
    }

    [Fact]
    public void Jpeg_WithTruncatedExif_ReturnsNullAndDoesNotThrow()
    {
        var path = Write(RecipeTestFiles.JpegWithUserComment(false, "UNICODE\0"u8.ToArray(),
            Encoding.BigEndianUnicode.GetBytes(Text), truncateAfter: 10));

        JpegExifReader.ReadUserComment(path).Should().BeNull();
    }
}
