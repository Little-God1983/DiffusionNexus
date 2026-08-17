using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The export commands name a format ("Export as JPEG"), so the file they write must carry a
/// matching extension even when the user clears the suggested one in the save dialog — the
/// picker sets no default extension, so it hands back exactly what was typed.
/// <para>
/// The extension is appended rather than swapped, so names that merely contain a dot
/// ("render v1.2") survive intact.
/// </para>
/// </summary>
public class ImageEditorViewModelExportExtensionTests
{
    private readonly Mock<IDatasetEventAggregator> _mockAggregator = new();

    private ImageEditorViewModel CreateSut(string chosenPath, out List<string> savedPaths)
    {
        var paths = new List<string>();
        savedPaths = paths;

        var sut = new ImageEditorViewModel(eventAggregator: _mockAggregator.Object);
        sut.LoadImage(@"C:\datasets\test\original.png");
        sut.ShowSaveFileDialogFunc = (_, _, _) => Task.FromResult<string?>(chosenPath);
        sut.SaveImageFunc = path => { paths.Add(path); return true; };
        sut.SaveJpegFunc = path => { paths.Add(path); return true; };
        sut.SaveLayeredTiffFunc = path => { paths.Add(path); return true; };
        return sut;
    }

    [Theory]
    [InlineData(@"C:\out\photo", @"C:\out\photo.jpg")]
    [InlineData(@"C:\out\photo.jpg", @"C:\out\photo.jpg")]
    [InlineData(@"C:\out\photo.JPG", @"C:\out\photo.JPG")]
    [InlineData(@"C:\out\photo.jpeg", @"C:\out\photo.jpeg")]
    [InlineData(@"C:\out\render v1.2", @"C:\out\render v1.2.jpg")]
    public async Task WhenExportingAsJpegThenTheFileGetsAJpegExtension(string chosen, string expected)
    {
        var sut = CreateSut(chosen, out var savedPaths);

        await sut.ExportAsJpegCommand.ExecuteAsync(null);

        savedPaths.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\out\photo", @"C:\out\photo.png")]
    [InlineData(@"C:\out\photo.png", @"C:\out\photo.png")]
    public async Task WhenExportingAsPngThenTheFileGetsAPngExtension(string chosen, string expected)
    {
        var sut = CreateSut(chosen, out var savedPaths);

        await sut.ExportAsPngCommand.ExecuteAsync(null);

        savedPaths.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Theory]
    [InlineData(@"C:\out\layers", @"C:\out\layers.tif")]
    [InlineData(@"C:\out\layers.tiff", @"C:\out\layers.tiff")]
    public async Task WhenExportingAsLayeredTiffThenTheFileGetsATiffExtension(string chosen, string expected)
    {
        var sut = CreateSut(chosen, out var savedPaths);

        await sut.ExportAsLayeredTiffCommand.ExecuteAsync(null);

        savedPaths.Should().ContainSingle().Which.Should().Be(expected);
    }

    [Fact]
    public async Task WhenExportingWithTheSourceFormatThenTheSourceExtensionIsRestored()
    {
        var sut = CreateSut(@"C:\out\copy", out var savedPaths);

        await sut.ExportCommand.ExecuteAsync(null);

        savedPaths.Should().ContainSingle().Which.Should().Be(@"C:\out\copy.png");
    }

    [Fact]
    public async Task WhenTheSaveDialogIsCancelledThenNothingIsWritten()
    {
        var sut = CreateSut(chosenPath: string.Empty, out var savedPaths);

        await sut.ExportAsJpegCommand.ExecuteAsync(null);

        savedPaths.Should().BeEmpty();
    }
}
