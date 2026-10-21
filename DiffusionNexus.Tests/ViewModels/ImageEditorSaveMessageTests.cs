using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// What the editor says when the canvas cannot be written in the original's format (GIF has no
/// encoder; a TIFF would be written as one flat PNG under the .tif name), and that it says it
/// before the user picks a file or confirms an overwrite.
/// </summary>
public sealed class ImageEditorSaveMessageTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory();

    public void Dispose() => _tempDir.Delete(recursive: true);

    [Theory]
    [InlineData(@"C:|data|anim.gif")]
    [InlineData(@"C:|data|ANIM.GIF")]
    public void DescribeSaveFailure_Gif_SaysWhyAndWhatToDo(string path)
    {
        var message = ImageEditorViewModel.DescribeSaveFailure(path.Replace('|', Path.DirectorySeparatorChar));

        message.Should().Contain("GIF");
        message.Should().Contain("Export as PNG", "that menu entry is the editor's way to a format it can write");
    }

    [Theory]
    [InlineData("layers.tif")]
    [InlineData("layers.TIFF")]
    public void DescribeSaveFailure_Tiff_PointsToTheLayeredTiffExport(string name)
        => ImageEditorViewModel.DescribeSaveFailure(name).Should().Contain("Export as Layered TIFF");

    [Fact]
    public void DescribeSaveFailure_OtherFormats_KeepTheGenericMessage()
        => ImageEditorViewModel.DescribeSaveFailure(Path.Combine("C:", "data", "photo.png")).Should().Be("Failed to save image.");

    [Theory]
    [InlineData("anim.gif")]
    [InlineData("layers.tif")]
    public async Task Export_OfAnUnwritableFormat_SaysWhyBeforeThePicker(string name)
    {
        // Export keeps the original's format, which the editor cannot write for these.
        var path = Path.Combine(_tempDir.FullName, name);
        var (sut, asked, saves) = CreateSut(path);

        await sut.ExportCommand.ExecuteAsync(null);

        sut.StatusMessage.Should().Be(ImageEditorViewModel.DescribeSaveFailure(path));
        asked.Should().BeEmpty("nobody should pick a place for a file that cannot be written");
        saves.Should().BeEmpty();
    }

    [Theory]
    [InlineData("anim.gif")]
    [InlineData("layers.tif")]
    public async Task SaveOverwrite_OfAnUnwritableFormat_SaysWhyBeforeTheConfirmation(string name)
    {
        var path = Path.Combine(_tempDir.FullName, name);
        var (sut, asked, saves) = CreateSut(path);

        await sut.SaveOverwriteCommand.ExecuteAsync(null);

        sut.StatusMessage.Should().Be(ImageEditorViewModel.DescribeSaveFailure(path));
        asked.Should().BeEmpty();
        saves.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveAsNew_OfATiffBesideTheOriginal_WritesNothing()
    {
        // Save As New beside the original keeps the extension: the flat PNG would be named .tif.
        var path = Path.Combine(_tempDir.FullName, "layers.tif");
        var (sut, _, saves) = CreateSut(path);
        sut.SaveAsDialogRequested += () => Task.FromResult(SaveAsResult.Success("copy", ImageRatingStatus.Unrated));

        await sut.SaveAsNewCommand.ExecuteAsync(null);

        sut.StatusMessage.Should().Be(ImageEditorViewModel.DescribeSaveFailure(path));
        saves.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveOverwrite_OfAnAnimatedWebp_IsRefusedBeforeTheConfirmation()
    {
        // The editor shows the first frame; saving over the file would throw the others away.
        var webp = Path.Combine(_tempDir.FullName, "anim.webp");
        var bytes = TestImageFiles.AnimatedWebp(4, 4);
        File.WriteAllBytes(webp, bytes);
        var (sut, asked, saves) = CreateSut(webp);

        await sut.SaveOverwriteCommand.ExecuteAsync(null);

        sut.StatusMessage.Should().Contain("animated").And.Contain("Export as PNG");
        asked.Should().BeEmpty();
        saves.Should().BeEmpty();
        File.ReadAllBytes(webp).Should().Equal(bytes);
    }

    /// <summary>An editor on <paramref name="path"/> that records every picker, confirmation and save.</summary>
    private static (ImageEditorViewModel Sut, List<string> Asked, List<string> Saves) CreateSut(string path)
    {
        var asked = new List<string>();
        var saves = new List<string>();
        var sut = new ImageEditorViewModel(eventAggregator: Mock.Of<IDatasetEventAggregator>());
        sut.LoadImage(path);
        sut.ShowSaveFileDialogFunc = (title, _, _) => { asked.Add(title); return Task.FromResult<string?>(path); };
        sut.SaveOverwriteConfirmRequested += () => { asked.Add("overwrite"); return Task.FromResult(true); };
        sut.SaveImageFunc = (target, _) => { saves.Add(target); return false; };
        return (sut, asked, saves);
    }
}
