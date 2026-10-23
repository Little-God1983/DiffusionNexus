using DiffusionNexus.Service.Services.Sync.Thumbnails;
using FluentAssertions;

namespace DiffusionNexus.Tests.Sync.Service.Thumbnails;

/// <summary>
/// Covers <see cref="LocalPreviewFiles"/> — sibling preview-file discovery (the extension
/// ladder shared by <c>SidecarMetadataApplier</c> and <c>ModelTileViewModel</c>) plus the
/// string-wise <c>file://</c> path stripper. The <c>file://C:\x</c> shape is malformed by
/// construction (the drive letter parses as a URI authority), so <c>new Uri(url).LocalPath</c>
/// must never be used here.
/// </summary>
public class LocalPreviewFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "dn-lpf-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void FindSibling_PrefersThePreviewLadderOrder()
    {
        var model = Path.Combine(_dir, "mylora.safetensors");
        File.WriteAllBytes(Path.Combine(_dir, "mylora.png"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "mylora.preview.jpg"), [1]);
        LocalPreviewFiles.FindSibling(model).Should().Be(Path.Combine(_dir, "mylora.preview.jpg"));
    }

    [Fact]
    public void FindSibling_ReturnsNullWhenNothingMatchesOrDirectoryMissing()
    {
        LocalPreviewFiles.FindSibling(Path.Combine(_dir, "none.safetensors")).Should().BeNull();
        LocalPreviewFiles.FindSibling(Path.Combine(_dir, "gone", "x.safetensors")).Should().BeNull();
    }

    [Theory]
    [InlineData(@"file://C:\loras\a.png", @"C:\loras\a.png", true)]   // the malformed-by-construction shape must work
    [InlineData("file:///tmp/a.png", "/tmp/a.png", true)]
    [InlineData("https://x/a.png", "", false)]
    [InlineData("user-thumbnail://abc", "", false)]
    [InlineData(null, "", false)]
    public void TryGetLocalPath_StripsThePrefixStringWise(string? url, string expected, bool ok)
    {
        LocalPreviewFiles.TryGetLocalPath(url, out var path).Should().Be(ok);
        path.Should().Be(expected);
    }
}
