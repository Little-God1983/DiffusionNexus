using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// JPEG cannot store transparency (#584): the encoder silently turns transparent areas black.
/// Every user-initiated save that writes a JPEG therefore asks what should fill them — when, and
/// only when, the image really has transparent areas. Cancelling writes nothing.
/// </summary>
public class ImageEditorViewModelJpegTransparencyTests
{
    private readonly Mock<IDatasetEventAggregator> _mockAggregator = new();

    private sealed record Write(string Path, TransparencyFill? Fill);

    private sealed class Harness
    {
        public required ImageEditorViewModel Sut { get; init; }
        public List<Write> Writes { get; } = [];
        public int Prompts { get; set; }
    }

    /// <param name="originalPath">The image open in the editor.</param>
    /// <param name="chosenPath">What the save picker hands back.</param>
    /// <param name="hasTransparency">What the canvas reports.</param>
    /// <param name="answer">The prompt's answer; null is Cancel.</param>
    private Harness CreateHarness(
        string originalPath,
        string chosenPath,
        bool hasTransparency,
        TransparencyFill? answer)
    {
        var sut = new ImageEditorViewModel(eventAggregator: _mockAggregator.Object);
        var harness = new Harness { Sut = sut };

        sut.LoadImage(originalPath);
        sut.ShowSaveFileDialogFunc = (_, _, _) => Task.FromResult<string?>(chosenPath);
        sut.SaveImageFunc = path => { harness.Writes.Add(new Write(path, null)); return true; };
        sut.SaveJpegFunc = (path, fill) => { harness.Writes.Add(new Write(path, fill)); return true; };
        sut.HasTransparencyFunc = () => hasTransparency;
        sut.JpegTransparencyPromptRequested += () =>
        {
            harness.Prompts++;
            return Task.FromResult(answer);
        };
        sut.SaveOverwriteConfirmRequested += () => Task.FromResult(true);
        sut.SaveAsDialogRequested += () => Task.FromResult(SaveAsResult.Success("copy", ImageRatingStatus.Unrated));
        return harness;
    }

    [Theory]
    [InlineData(TransparencyFill.White)]
    [InlineData(TransparencyFill.Black)]
    public async Task WhenExportingATransparentImageAsJpegThenTheChosenFillIsUsed(TransparencyFill fill)
    {
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\photo.jpg", hasTransparency: true, answer: fill);

        await h.Sut.ExportAsJpegCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(1);
        h.Writes.Should().Equal(new Write(@"C:\out\photo.jpg", fill));
    }

    [Fact]
    public async Task WhenTheTransparencyPromptIsCancelledThenNothingIsWritten()
    {
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\photo.jpg", hasTransparency: true, answer: null);

        await h.Sut.ExportAsJpegCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(1);
        h.Writes.Should().BeEmpty();
        h.Sut.StatusMessage.Should().BeNull("a cancel is not a failed export");
    }

    [Fact]
    public async Task WhenAnOpaqueImageIsExportedAsJpegThenNobodyIsAsked()
    {
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\photo.jpg", hasTransparency: false, answer: TransparencyFill.White);

        await h.Sut.ExportAsJpegCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(0);
        h.Writes.Should().Equal(new Write(@"C:\out\photo.jpg", null));
    }

    [Fact]
    public async Task WhenATransparentImageIsExportedAsPngThenNobodyIsAsked()
    {
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\photo.png", hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.ExportAsPngCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(0);
        h.Writes.Should().Equal(new Write(@"C:\out\photo.png", null));
    }

    [Theory]
    [InlineData(@"C:\in\original.jpg", @"C:\out\copy.jpg")]
    [InlineData(@"C:\in\original.JPEG", @"C:\out\copy.JPEG")]
    public async Task WhenExportingATransparentJpegInItsOwnFormatThenTheChosenFillIsUsed(string original, string chosen)
    {
        var h = CreateHarness(original, chosen, hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.ExportCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(1);
        h.Writes.Should().Equal(new Write(chosen, TransparencyFill.White));
    }

    [Fact]
    public async Task WhenExportingATransparentPngInItsOwnFormatThenNobodyIsAsked()
    {
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\copy.png", hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.ExportCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(0);
        h.Writes.Should().Equal(new Write(@"C:\out\copy.png", null));
    }

    [Fact]
    public async Task WhenOverwritingATransparentJpegThenTheChosenFillIsUsed()
    {
        var h = CreateHarness(@"C:\in\original.jpg", chosenPath: string.Empty, hasTransparency: true, answer: TransparencyFill.Black);

        await h.Sut.SaveOverwriteCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(1);
        h.Writes.Should().Equal(new Write(@"C:\in\original.jpg", TransparencyFill.Black));
    }

    [Fact]
    public async Task WhenOverwritingATransparentJpegIsCancelledAtThePromptThenTheOriginalIsKept()
    {
        var h = CreateHarness(@"C:\in\original.jpg", chosenPath: string.Empty, hasTransparency: true, answer: null);

        await h.Sut.SaveOverwriteCommand.ExecuteAsync(null);

        h.Writes.Should().BeEmpty();
        h.Sut.StatusMessage.Should().BeNull("a cancel is not a failed save");
    }

    [Fact]
    public async Task WhenSavingATransparentJpegAsANewFileThenTheChosenFillIsUsed()
    {
        var h = CreateHarness(@"C:\in\original.jpg", chosenPath: string.Empty, hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.SaveAsNewCommand.ExecuteAsync(null);

        h.Prompts.Should().Be(1);
        h.Writes.Should().Equal(new Write(@"C:\in\copy.jpg", TransparencyFill.White));
    }
}
