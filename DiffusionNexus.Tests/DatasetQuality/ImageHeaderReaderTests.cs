using DiffusionNexus.Domain.Models;
using DiffusionNexus.Service.Services;
using FluentAssertions;

namespace DiffusionNexus.Tests.DatasetQuality;

/// <summary>
/// Unit tests for <see cref="ImageHeaderReader"/>.
/// Creates minimal binary image files programmatically for each format.
/// </summary>
public class ImageHeaderReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ImageHeaderReader _sut = new();

    public ImageHeaderReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ImageHeaderTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* best effort cleanup */ }
    }

    #region Test File Generators

    private string CreateTestPng(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.png");
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // PNG signature
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });

        // IHDR chunk
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x0D }); // length = 13
        writer.Write(new byte[] { 0x49, 0x48, 0x44, 0x52 }); // "IHDR"
        WriteInt32BigEndian(writer, width);
        WriteInt32BigEndian(writer, height);
        writer.Write(new byte[] { 0x08, 0x02, 0x00, 0x00, 0x00 }); // bit depth, color type, etc.
        writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00 }); // CRC (dummy)

        return path;
    }

    private string CreateTestJpeg(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.jpg");
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // SOI marker
        writer.Write(new byte[] { 0xFF, 0xD8 });

        // SOF0 marker
        writer.Write(new byte[] { 0xFF, 0xC0 });
        WriteUInt16BigEndian(writer, 17); // segment length
        writer.Write((byte)8); // precision
        WriteUInt16BigEndian(writer, height);
        WriteUInt16BigEndian(writer, width);
        writer.Write(new byte[] { 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01 }); // components

        // EOI marker
        writer.Write(new byte[] { 0xFF, 0xD9 });

        return path;
    }

    private string CreateTestBmp(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.bmp");
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // BM signature
        writer.Write((byte)'B');
        writer.Write((byte)'M');
        writer.Write(0); // file size (dummy)
        writer.Write(0); // reserved
        writer.Write(54); // pixel data offset

        // DIB header (BITMAPINFOHEADER)
        writer.Write(40); // header size
        writer.Write(width);
        writer.Write(height);
        writer.Write((short)1); // planes
        writer.Write((short)24); // bpp

        return path;
    }

    private string CreateTestWebPVP8X(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.webp");
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // RIFF header
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0); // file size (dummy)
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WEBP"));

        // VP8X chunk
        writer.Write(System.Text.Encoding.ASCII.GetBytes("VP8X"));
        writer.Write(10); // chunk size
        writer.Write(0); // flags

        // Width-1 and Height-1 as 24-bit LE
        WriteUInt24LittleEndian(writer, width - 1);
        WriteUInt24LittleEndian(writer, height - 1);

        return path;
    }

    private string CreateTestGif(int width, int height)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.gif");
        using var fs = File.Create(path);
        using var writer = new BinaryWriter(fs);

        // GIF89a signature
        writer.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));

        // Logical Screen Descriptor
        writer.Write((ushort)width);
        writer.Write((ushort)height);
        writer.Write(new byte[] { 0x00, 0x00, 0x00 }); // flags, bg, pixel aspect

        return path;
    }

    #endregion

    #region Binary Helpers

    private static void WriteInt32BigEndian(BinaryWriter writer, int value)
    {
        var bytes = BitConverter.GetBytes(value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        writer.Write(bytes);
    }

    private static void WriteUInt16BigEndian(BinaryWriter writer, int value)
    {
        var bytes = BitConverter.GetBytes((ushort)value);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        writer.Write(bytes);
    }

    private static void WriteUInt24LittleEndian(BinaryWriter writer, int value)
    {
        writer.Write((byte)(value & 0xFF));
        writer.Write((byte)((value >> 8) & 0xFF));
        writer.Write((byte)((value >> 16) & 0xFF));
    }

    #endregion

    #region PNG Tests

    [Fact]
    public void WhenValidPngThenReadsDimensions()
    {
        var path = CreateTestPng(1920, 1080);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(1920);
        dims.Height.Should().Be(1080);
    }

    [Fact]
    public void WhenInvalidPngThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "invalid.png");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    #endregion

    #region JPEG Tests

    [Fact]
    public void WhenValidJpegThenReadsDimensions()
    {
        var path = CreateTestJpeg(1280, 720);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(1280);
        dims.Height.Should().Be(720);
    }

    [Fact]
    public void WhenInvalidJpegThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "invalid.jpg");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02]);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    #endregion

    #region BMP Tests

    [Fact]
    public void WhenValidBmpThenReadsDimensions()
    {
        var path = CreateTestBmp(800, 600);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(800);
        dims.Height.Should().Be(600);
    }

    [Fact]
    public void WhenBmpNegativeHeightThenReadsAbsoluteValue()
    {
        var path = CreateTestBmp(640, -480);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(640);
        dims.Height.Should().Be(480);
    }

    [Fact]
    public void WhenInvalidBmpThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "invalid.bmp");
        File.WriteAllBytes(path, [0x00, 0x01]);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    #endregion

    #region WebP Tests

    [Fact]
    public void WhenValidWebPVP8XThenReadsDimensions()
    {
        var path = CreateTestWebPVP8X(2560, 1440);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(2560);
        dims.Height.Should().Be(1440);
    }

    [Fact]
    public void WhenInvalidWebPThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "invalid.webp");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    #endregion

    #region GIF Tests

    [Fact]
    public void WhenValidGifThenReadsDimensions()
    {
        var path = CreateTestGif(320, 240);
        var dims = _sut.ReadDimensions(path);

        dims.Width.Should().Be(320);
        dims.Height.Should().Be(240);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void WhenFileMissingThenReturnsInvalid()
    {
        var dims = _sut.ReadDimensions(@"C:\nonexistent\image.png");
        dims.IsValid.Should().BeFalse();
    }

    [Fact]
    public void WhenEmptyFileThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "empty.png");
        File.WriteAllBytes(path, []);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    [Fact]
    public void WhenUnsupportedExtensionThenReturnsInvalid()
    {
        var path = Path.Combine(_tempDir, "image.tiff");
        File.WriteAllBytes(path, [0x49, 0x49, 0x2A, 0x00]);

        var dims = _sut.ReadDimensions(path);
        dims.IsValid.Should().BeFalse();
    }

    [Fact]
    public void WhenNullPathThenThrows()
    {
        var act = () => _sut.ReadDimensions(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion
}
