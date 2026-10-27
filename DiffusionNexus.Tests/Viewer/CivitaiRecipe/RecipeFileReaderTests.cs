using System.Text;
using DiffusionNexus.UI.Services.CivitaiRecipe;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer.CivitaiRecipe;

/// <summary>
/// #628: the recipe reads the original file by what it is, not by its name — Civitai serves the
/// example image's PNG under a <c>….jpeg</c> URL.
/// </summary>
public sealed class RecipeFileReaderTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dn-recipe-file").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void Png_NamedJpeg_IsReadAsPng()
    {
        var path = Write("x.jpeg", RecipeTestFiles.PngWithText(("parameters", "a cat\nSteps: 20"), ("prompt", "{}")));

        var result = RecipeFileReader.Read(path);

        result.Format.Should().Be(RecipeFileFormat.Png);
        result.Texts["parameters"].Should().StartWith("a cat");
        result.Texts["prompt"].Should().Be("{}");
    }

    [Fact]
    public void Jpeg_PutsUserCommentUnderParameters()
    {
        var path = Write("x.png", RecipeTestFiles.JpegWithUserComment(false, "UNICODE\0"u8.ToArray(),
            Encoding.BigEndianUnicode.GetBytes("a dog\nSteps: 30")));

        var result = RecipeFileReader.Read(path);

        result.Format.Should().Be(RecipeFileFormat.Jpeg);
        result.Texts.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string>("parameters", "a dog\nSteps: 30"));
    }

    [Fact]
    public void Png_WithExifChunk_PutsUserCommentUnderParameters()
    {
        // Civitai image 147042061 (2026-10-27): a PNG with no text chunk; its parameters sit in an
        // eXIf chunk holding the same UNICODE UserComment the JPEGs carry.
        var path = Write("x.png", RecipeTestFiles.PngWithExif(RecipeTestFiles.TiffWithUserComment(false, "UNICODE\0"u8.ToArray(),
            Encoding.BigEndianUnicode.GetBytes("a fox\nSteps: 30"))));

        var result = RecipeFileReader.Read(path);

        result.Format.Should().Be(RecipeFileFormat.Png);
        result.Texts["parameters"].Should().Be("a fox\nSteps: 30");
    }

    [Fact]
    public void Jpeg_WithoutComment_HasNoTexts()
    {
        var result = RecipeFileReader.Read(Write("x.jpg", [0xFF, 0xD8, 0xFF, 0xD9]));

        result.Format.Should().Be(RecipeFileFormat.Jpeg);
        result.Texts.Should().BeEmpty();
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, RecipeFileFormat.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF }, RecipeFileFormat.Jpeg)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70 }, RecipeFileFormat.Video)]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }, RecipeFileFormat.Video)]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, RecipeFileFormat.WebP)]
    [InlineData(new byte[] { 0x47, 0x49, 0x46 }, RecipeFileFormat.Unknown)]
    [InlineData(new byte[] { }, RecipeFileFormat.Unknown)]
    public void DetectFormat_ByMagicBytes(byte[] head, RecipeFileFormat expected)
    {
        RecipeFileReader.DetectFormat(head).Should().Be(expected);
    }
}
