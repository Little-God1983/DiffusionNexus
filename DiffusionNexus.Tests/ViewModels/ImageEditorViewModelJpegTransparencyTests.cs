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
        public int TransparencyChecks { get; set; }

        /// <summary>The dialogs in the order they opened: "warning", "picker", "save as".</summary>
        public List<string> Dialogs { get; } = [];
    }

    /// <param name="originalPath">The image open in the editor.</param>
    /// <param name="chosenPath">What the save picker hands back.</param>
    /// <param name="hasTransparency">What the canvas reports.</param>
    /// <param name="answer">The prompt's answer; null is Cancel.</param>
    /// <param name="withPrompt">Whether anything answers the transparency prompt.</param>
    /// <param name="overwriteConfirmed">The answer to "overwrite your original?".</param>
    private Harness CreateHarness(
        string originalPath,
        string chosenPath,
        bool hasTransparency,
        TransparencyFill? answer,
        bool withPrompt = true,
        bool overwriteConfirmed = true)
    {
        var sut = new ImageEditorViewModel(eventAggregator: _mockAggregator.Object);
        var harness = new Harness { Sut = sut };

        sut.LoadImage(originalPath);
        sut.ShowSaveFileDialogFunc = (_, _, _) =>
        {
            harness.Dialogs.Add("picker");
            return Task.FromResult<string?>(chosenPath);
        };
        sut.SaveImageFunc = (path, fill) => { harness.Writes.Add(new Write(path, fill)); return true; };
        sut.HasTransparencyFunc = () =>
        {
            harness.TransparencyChecks++;
            return hasTransparency;
        };
        if (withPrompt)
        {
            sut.JpegTransparencyPromptRequested += () =>
            {
                harness.Prompts++;
                harness.Dialogs.Add("warning");
                return Task.FromResult(answer);
            };
        }
        sut.SaveOverwriteConfirmRequested += () => Task.FromResult(overwriteConfirmed);
        sut.SaveAsDialogRequested += () =>
        {
            harness.Dialogs.Add("save as");
            return Task.FromResult(SaveAsResult.Success("copy", ImageRatingStatus.Unrated));
        };
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

    // ── Order: the warning is about what gets saved, so it comes before choosing where ──

    private static Task Export(ImageEditorViewModel sut, bool asJpeg)
        => asJpeg ? sut.ExportAsJpegCommand.ExecuteAsync(null) : sut.ExportCommand.ExecuteAsync(null);

    [Theory]
    [InlineData(true, @"C:\in\original.png", @"C:\out\photo.jpg")]
    [InlineData(false, @"C:\in\original.jpg", @"C:\out\copy.jpg")]
    public async Task WhenExportingATransparentImageAsJpegThenTheWarningComesBeforeTheFolderPicker(
        bool asJpeg, string original, string chosen)
    {
        var h = CreateHarness(original, chosen, hasTransparency: true, answer: TransparencyFill.White);

        await Export(h.Sut, asJpeg);

        h.Dialogs.Should().Equal("warning", "picker");
        h.Writes.Should().Equal(new Write(chosen, TransparencyFill.White));
    }

    [Theory]
    [InlineData(true, @"C:\in\original.png")]
    [InlineData(false, @"C:\in\original.jpg")]
    public async Task WhenTheWarningIsCancelledThenTheFolderPickerNeverOpens(bool asJpeg, string original)
    {
        var h = CreateHarness(original, @"C:\out\photo.jpg", hasTransparency: true, answer: null);

        await Export(h.Sut, asJpeg);

        h.Dialogs.Should().Equal("warning");
        h.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenTheFolderPickerIsCancelledAfterTheWarningThenNothingIsWritten()
    {
        var h = CreateHarness(@"C:\in\original.png", chosenPath: string.Empty, hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.ExportAsJpegCommand.ExecuteAsync(null);

        h.Dialogs.Should().Equal("warning", "picker");
        h.Writes.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenSavingAsNewThenTheWarningComesAfterTheSaveAsDialog()
    {
        // The Save As dialog is where the user can still pick Layered TIFF, which keeps the
        // transparency. Until then it is not known that a JPEG will be written at all.
        var h = CreateHarness(@"C:\in\original.jpg", chosenPath: string.Empty, hasTransparency: true, answer: TransparencyFill.White);

        await h.Sut.SaveAsNewCommand.ExecuteAsync(null);

        h.Dialogs.Should().Equal("save as", "warning");
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

    [Fact]
    public async Task WhenNobodyAnswersThePromptThenTheJpegIsWrittenUnfilledWithoutCheckingTransparency()
    {
        // The check flattens and scans the whole canvas; with no one to ask, that work is wasted.
        var h = CreateHarness(@"C:\in\original.png", @"C:\out\photo.jpg", hasTransparency: true,
            answer: TransparencyFill.White, withPrompt: false);

        await h.Sut.ExportAsJpegCommand.ExecuteAsync(null);

        h.TransparencyChecks.Should().Be(0);
        h.Writes.Should().Equal(new Write(@"C:\out\photo.jpg", null));
    }

    [Fact]
    public async Task WhenTheOverwriteIsDeclinedThenTransparencyIsNeitherCheckedNorAsked()
    {
        var h = CreateHarness(@"C:\in\original.jpg", chosenPath: string.Empty, hasTransparency: true,
            answer: TransparencyFill.White, overwriteConfirmed: false);

        await h.Sut.SaveOverwriteCommand.ExecuteAsync(null);

        h.TransparencyChecks.Should().Be(0);
        h.Prompts.Should().Be(0);
        h.Writes.Should().BeEmpty();
    }

    // ── Hand-off to other tools (Upscale, Add To…, Send To…) ─────────────────────────
    // The canvas goes out as a temp copy in the original's format. A JPEG cannot carry the
    // transparency an edit added, so that one case goes out as PNG instead. Nobody is asked.

    private string SendToUpscale(Harness h)
    {
        NavigateToBatchUpscaleEventArgs? published = null;
        _mockAggregator
            .Setup(a => a.PublishNavigateToBatchUpscale(It.IsAny<NavigateToBatchUpscaleEventArgs>()))
            .Callback<NavigateToBatchUpscaleEventArgs>(args => published = args);

        h.Sut.SendToBatchUpscaleCommand.Execute(null);

        published.Should().NotBeNull();
        var handedOver = published!.ImagePaths.Should().ContainSingle().Subject;
        h.Writes.Should().ContainSingle().Which.Path.Should().Be(handedOver, "the temp copy is what goes out");
        return handedOver;
    }

    [Theory]
    [InlineData(@"C:\in\photo.jpg", true, ".png")]
    [InlineData(@"C:\in\photo.JPEG", true, ".png")]
    [InlineData(@"C:\in\photo.jpg", false, ".jpg")]
    [InlineData(@"C:\in\art.png", true, ".png")]
    [InlineData(@"C:\in\art.webp", true, ".webp")]
    public void WhenTheCanvasIsHandedOverThenOnlyAJpegThatWouldLoseTransparencyBecomesAPng(
        string original, bool hasTransparency, string expectedExtension)
    {
        var h = CreateHarness(original, chosenPath: string.Empty, hasTransparency, answer: TransparencyFill.White);

        var handedOver = SendToUpscale(h);

        Path.GetExtension(handedOver).Should().Be(expectedExtension);
        h.Prompts.Should().Be(0);
    }

    [Fact]
    public void WhenAPngIsHandedOverThenTransparencyIsNotChecked()
    {
        var h = CreateHarness(@"C:\in\art.png", chosenPath: string.Empty, hasTransparency: true, answer: null);

        SendToUpscale(h);

        h.TransparencyChecks.Should().Be(0, "a PNG keeps its transparency whatever the canvas holds");
    }
}
