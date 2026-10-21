using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

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
        message.Should().Contain("PNG");
    }

    [Fact]
    public void DescribeSaveFailure_OtherFormats_KeepTheGenericMessage()
        => ImageEditorViewModel.DescribeSaveFailure(@"C:\data\photo.png").Should().Be("Failed to save image.");
}
