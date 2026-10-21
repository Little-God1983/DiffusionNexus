using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

public class ImageEditorSaveMessageTests
{
    [Theory]
    [InlineData(@"C:\data\anim.gif")]
    [InlineData(@"C:\data\ANIM.GIF")]
    public void DescribeSaveFailure_Gif_SaysWhyAndWhatToDo(string path)
    {
        var message = ImageEditorViewModel.DescribeSaveFailure(path);

        message.Should().Contain("GIF");
        message.Should().Contain("Export as PNG", "that menu entry is the editor's way to a format it can write");
    }

    [Fact]
    public void DescribeSaveFailure_OtherFormats_KeepTheGenericMessage()
        => ImageEditorViewModel.DescribeSaveFailure(@"C:\data\photo.png").Should().Be("Failed to save image.");

    [Fact]
    public async Task Export_OfAGif_SaysWhyItFailed()
    {
        // Export keeps the original's format, so a GIF original can never be exported this way.
        var sut = new ImageEditorViewModel(eventAggregator: Mock.Of<IDatasetEventAggregator>());
        sut.LoadImage(@"C:\datasets\test\anim.gif");
        sut.ShowSaveFileDialogFunc = (_, _, _) => Task.FromResult<string?>(@"C:\out\copy.gif");
        sut.SaveImageFunc = (_, _) => false;

        await sut.ExportCommand.ExecuteAsync(null);

        sut.StatusMessage.Should().Be(ImageEditorViewModel.DescribeSaveFailure(@"C:\out\copy.gif"));
    }
}
