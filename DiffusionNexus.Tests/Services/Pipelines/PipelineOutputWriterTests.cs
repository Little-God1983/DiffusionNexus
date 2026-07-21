using System.Text;
using DiffusionNexus.UI.Models.Pipelines;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Services.Pipelines;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.Services.Pipelines;

/// <summary>
/// Unit tests for <see cref="PipelineOutputWriter"/>: the three output modes,
/// the <c>_real</c> stem rule that stops in-place runs clobbering their source,
/// the never-overwrite <c>-2</c>/<c>-3</c> fallback, and the sibling-caption copy
/// that keeps a new dataset version complete.
/// </summary>
public class PipelineOutputWriterTests : IDisposable
{
    private readonly Mock<IDialogService> _dialogs = new(MockBehavior.Loose);
    private readonly Mock<IDatasetEventAggregator> _events = new(MockBehavior.Loose);
    private readonly DirectoryInfo _tempRoot;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47];

    public PipelineOutputWriterTests()
    {
        _tempRoot = Directory.CreateTempSubdirectory();
    }

    public void Dispose()
    {
        try { _tempRoot.Delete(recursive: true); }
        catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    private PipelineOutputWriter CreateSut() => new(_dialogs.Object, _events.Object);

    private string Dir(string name)
    {
        var path = Path.Combine(_tempRoot.FullName, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static PipelineOutputOption Option(PipelineOutputMode mode) => new(mode, mode.ToString());

    #region Construction

    [Fact]
    public void WhenDialogServiceIsNullThenConstructorThrows()
    {
        var act = () => new PipelineOutputWriter(null!, _events.Object);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void WhenEventAggregatorIsNullThenConstructorThrows()
    {
        var act = () => new PipelineOutputWriter(_dialogs.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region PrepareAsync

    [Fact]
    public async Task WhenNewVersionRequestedWithoutADatasetThenPrepareThrows()
    {
        var act = async () => await CreateSut()
            .PrepareAsync(Option(PipelineOutputMode.NewDatasetVersion), dataset: null);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*requires a selected dataset*");
    }

    [Fact]
    public async Task WhenFolderPickedThenPrepareReturnsThatFolderAsTheFixedDirectory()
    {
        var picked = Dir("picked");
        _dialogs.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(picked);

        var target = await CreateSut().PrepareAsync(Option(PipelineOutputMode.PickFolder), null);

        target.Should().NotBeNull();
        target!.Mode.Should().Be(PipelineOutputMode.PickFolder);
        target.FixedDirectory.Should().Be(picked);
        target.Dataset.Should().BeNull();
        target.NewVersion.Should().BeNull();
        _dialogs.Verify(d => d.ShowOpenFolderDialogAsync("Select an output folder"), Times.Once);
    }

    [Fact]
    public async Task WhenFolderPickerCancelledThenPrepareReturnsNull()
    {
        _dialogs.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var target = await CreateSut().PrepareAsync(Option(PipelineOutputMode.PickFolder), null);

        target.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WhenFolderPickerReturnsBlankThenPrepareReturnsNull(string picked)
    {
        _dialogs.Setup(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>())).ReturnsAsync(picked);

        var target = await CreateSut().PrepareAsync(Option(PipelineOutputMode.PickFolder), null);

        target.Should().BeNull();
    }

    [Fact]
    public async Task WhenInPlaceModeThenPrepareReturnsATargetWithNoFixedDirectory()
    {
        var target = await CreateSut().PrepareAsync(Option(PipelineOutputMode.InputFolderInPlace), null);

        target.Should().NotBeNull();
        target!.Mode.Should().Be(PipelineOutputMode.InputFolderInPlace);
        target.FixedDirectory.Should().BeNull();
        _dialogs.Verify(d => d.ShowOpenFolderDialogAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task WhenModeIsUnrecognisedThenPrepareThrowsArgumentOutOfRange()
    {
        var act = async () => await CreateSut()
            .PrepareAsync(Option((PipelineOutputMode)99), null);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    #endregion

    #region WriteAsync — destination and naming

    [Fact]
    public async Task WhenWritingToAFixedDirectoryThenTheSourceNameIsPreserved()
    {
        var outDir = Path.Combine(_tempRoot.FullName, "created-on-demand");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);

        var written = await CreateSut().WriteAsync(target, input, Png);

        written.Should().Be(Path.Combine(outDir, "cat.png"));
        File.ReadAllBytes(written).Should().Equal(Png);
    }

    [Fact]
    public async Task WhenOutputDirectoryDoesNotExistThenItIsCreated()
    {
        var outDir = Path.Combine(_tempRoot.FullName, "deep", "nested", "out");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);

        await CreateSut().WriteAsync(target, input, Png);

        Directory.Exists(outDir).Should().BeTrue();
    }

    [Fact]
    public async Task WhenWritingInPlaceThenTheOutputLandsNextToTheSourceWithARealSuffix()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        File.WriteAllText(input, "source");
        var target = new PipelineOutputTarget(PipelineOutputMode.InputFolderInPlace, null, null, null);

        var written = await CreateSut().WriteAsync(target, input, Png);

        written.Should().Be(Path.Combine(inDir, "cat_real.png"));
        // The source must survive untouched.
        File.ReadAllText(input).Should().Be("source");
    }

    [Fact]
    public async Task WhenWritingANewDatasetVersionThenTheSourceNameIsPreserved()
    {
        var outDir = Dir("v2");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.NewDatasetVersion, outDir, null, 2);

        var written = await CreateSut().WriteAsync(target, input, Png);

        written.Should().Be(Path.Combine(outDir, "cat.png"));
    }

    #endregion

    #region WriteAsync — collision avoidance

    [Fact]
    public async Task WhenTheOutputNameIsTakenThenTheNextWriteGetsADashTwoSuffix()
    {
        var outDir = Dir("out");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);
        var sut = CreateSut();

        var first = await sut.WriteAsync(target, input, Png);
        var second = await sut.WriteAsync(target, input, Png);

        first.Should().Be(Path.Combine(outDir, "cat.png"));
        second.Should().Be(Path.Combine(outDir, "cat-2.png"));
    }

    [Fact]
    public async Task WhenTheOutputNameIsTakenRepeatedlyThenSuffixesKeepIncrementing()
    {
        var outDir = Dir("out");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);
        var sut = CreateSut();

        await sut.WriteAsync(target, input, Png);
        await sut.WriteAsync(target, input, Png);
        var third = await sut.WriteAsync(target, input, Png);
        var fourth = await sut.WriteAsync(target, input, Png);

        third.Should().Be(Path.Combine(outDir, "cat-3.png"));
        fourth.Should().Be(Path.Combine(outDir, "cat-4.png"));
    }

    [Fact]
    public async Task WhenRerunningInPlaceThenTheSuffixIsAppliedAfterTheRealMarker()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        var target = new PipelineOutputTarget(PipelineOutputMode.InputFolderInPlace, null, null, null);
        var sut = CreateSut();

        var first = await sut.WriteAsync(target, input, Png);
        var second = await sut.WriteAsync(target, input, Png);

        first.Should().Be(Path.Combine(inDir, "cat_real.png"));
        second.Should().Be(Path.Combine(inDir, "cat_real-2.png"));
    }

    [Fact]
    public async Task WhenAnUnrelatedFileOccupiesTheNameThenTheWriteStillDoesNotOverwriteIt()
    {
        var outDir = Dir("out");
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var existing = Path.Combine(outDir, "cat.png");
        File.WriteAllText(existing, "do not clobber");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);

        var written = await CreateSut().WriteAsync(target, input, Png);

        written.Should().Be(Path.Combine(outDir, "cat-2.png"));
        File.ReadAllText(existing).Should().Be("do not clobber");
    }

    #endregion

    #region WriteAsync — caption carry-over

    [Fact]
    public async Task WhenWritingANewVersionThenTheSiblingCaptionIsCopied()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        File.WriteAllText(Path.ChangeExtension(input, ".txt"), "a cat, sitting", Encoding.UTF8);
        var outDir = Dir("v2");
        var target = new PipelineOutputTarget(PipelineOutputMode.NewDatasetVersion, outDir, null, 2);

        await CreateSut().WriteAsync(target, input, Png);

        var copied = Path.Combine(outDir, "cat.txt");
        File.Exists(copied).Should().BeTrue();
        File.ReadAllText(copied).Should().Be("a cat, sitting");
    }

    [Fact]
    public async Task WhenTheOutputGotASuffixThenTheCopiedCaptionMatchesTheOutputName()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        File.WriteAllText(Path.ChangeExtension(input, ".txt"), "a cat, sitting");
        var outDir = Dir("v2");
        var target = new PipelineOutputTarget(PipelineOutputMode.NewDatasetVersion, outDir, null, 2);
        var sut = CreateSut();

        await sut.WriteAsync(target, input, Png);
        await sut.WriteAsync(target, input, Png);

        File.Exists(Path.Combine(outDir, "cat.txt")).Should().BeTrue();
        File.Exists(Path.Combine(outDir, "cat-2.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task WhenThereIsNoSiblingCaptionThenTheWriteStillSucceeds()
    {
        var input = Path.Combine(Dir("in"), "cat.jpg");
        var outDir = Dir("v2");
        var target = new PipelineOutputTarget(PipelineOutputMode.NewDatasetVersion, outDir, null, 2);

        var written = await CreateSut().WriteAsync(target, input, Png);

        File.Exists(written).Should().BeTrue();
        Directory.EnumerateFiles(outDir, "*.txt").Should().BeEmpty();
    }

    [Fact]
    public async Task WhenWritingToAPickedFolderThenTheCaptionIsNotCopied()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        File.WriteAllText(Path.ChangeExtension(input, ".txt"), "a cat, sitting");
        var outDir = Dir("picked");
        var target = new PipelineOutputTarget(PipelineOutputMode.PickFolder, outDir, null, null);

        await CreateSut().WriteAsync(target, input, Png);

        Directory.EnumerateFiles(outDir, "*.txt").Should().BeEmpty();
    }

    [Fact]
    public async Task WhenWritingInPlaceThenTheCaptionIsNotDuplicated()
    {
        var inDir = Dir("in");
        var input = Path.Combine(inDir, "cat.jpg");
        File.WriteAllText(Path.ChangeExtension(input, ".txt"), "a cat, sitting");
        var target = new PipelineOutputTarget(PipelineOutputMode.InputFolderInPlace, null, null, null);

        await CreateSut().WriteAsync(target, input, Png);

        Directory.EnumerateFiles(inDir, "*.txt").Should().ContainSingle();
    }

    #endregion
}
