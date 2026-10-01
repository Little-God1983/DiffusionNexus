using DiffusionNexus.UI.ImageEditor;
using DiffusionNexus.UI.ImageEditor.Services;
using FluentAssertions;
using SkiaSharp;

namespace DiffusionNexus.Tests.ImageEditor;

/// <summary>
/// <see cref="ImageEditorCore.MatchedFilePath"/> / <see cref="ImageEditorCore.GetUnchangedFilePath"/>
/// decide whether a hand-off gives other tools the user's file or a re-encoded copy (#586). A false
/// positive hands over the file without the user's edits, so every way out of "unchanged" is pinned.
/// </summary>
public sealed class ImageEditorCoreFileMatchTests : IDisposable
{
    private readonly ImageEditorCore _sut;
    private readonly DirectoryInfo _tempDir;
    private readonly string _file;
    private readonly byte[] _png;

    public ImageEditorCoreFileMatchTests()
    {
        _tempDir = Directory.CreateTempSubdirectory();
        _sut = new ImageEditorCore();
        _sut.SetServices(EditorServiceFactory.Create());

        using var bitmap = new SKBitmap(32, 24, SKColorType.Rgba8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Coral);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        _png = data.ToArray();

        _file = Path.Combine(_tempDir.FullName, "original.png");
        File.WriteAllBytes(_file, _png);
    }

    public void Dispose()
    {
        _sut.Dispose();
        _tempDir.Delete(recursive: true);
    }

    [Fact]
    public void LoadedFromFile_MatchesThatFile()
    {
        _sut.LoadImage(_file).Should().BeTrue();

        _sut.MatchedFilePath.Should().Be(_file);
        _sut.GetUnchangedFilePath().Should().Be(_file);
    }

    [Fact]
    public void Edit_LeavesTheFileBehind_AndRaisesTheEventOnce()
    {
        _sut.LoadImage(_file);
        var raised = 0;
        _sut.MatchesFileChanged += (_, _) => raised++;

        _sut.AddLayer("scratch");
        _sut.AddLayer("scratch 2");

        _sut.MatchedFilePath.Should().BeNull();
        _sut.GetUnchangedFilePath().Should().BeNull();
        raised.Should().Be(1);
    }

    [Fact]
    public void MarkClean_DoesNotMakeAnEditedCanvasMatchTheFile()
    {
        // Export and Save as New mark the canvas clean, but the loaded file is not what they wrote.
        _sut.LoadImage(_file);
        _sut.AddLayer("scratch");

        _sut.MarkClean();

        _sut.IsDirty.Should().BeFalse();
        _sut.MatchedFilePath.Should().BeNull();
        _sut.GetUnchangedFilePath().Should().BeNull();
    }

    [Fact]
    public void ResetToOriginal_MatchesTheLoadedFileAgain()
    {
        _sut.LoadImage(_file);
        _sut.AddLayer("scratch");

        _sut.ResetToOriginal();

        _sut.GetUnchangedFilePath().Should().Be(_file);
    }

    [Fact]
    public void SaveOverTheFile_MatchesIt_ButResetThenDoesNot()
    {
        _sut.LoadImage(_file);
        _sut.AddLayer("scratch");
        _sut.SaveImage(_file).Should().BeTrue();

        _sut.MarkClean();
        _sut.MarkSavedOverFile(_file);

        _sut.GetUnchangedFilePath().Should().Be(_file);

        // Reset restores the pixels loaded before the save, which the file no longer holds.
        _sut.ResetToOriginal();
        _sut.MatchedFilePath.Should().BeNull();
    }

    [Fact]
    public void MarkSavedOverFile_IgnoresAnotherPath()
    {
        _sut.LoadImage(_file);
        _sut.AddLayer("scratch");
        var other = Path.Combine(_tempDir.FullName, "other.png");
        _sut.SaveImage(other).Should().BeTrue();

        _sut.MarkSavedOverFile(other);

        _sut.MatchedFilePath.Should().BeNull();
    }

    [Fact]
    public void FileRewrittenOnDisk_IsNotHandedOver()
    {
        _sut.LoadImage(_file);

        File.WriteAllBytes(_file, [.. _png, 0, 0, 0]);
        File.SetLastWriteTimeUtc(_file, DateTime.UtcNow.AddMinutes(1));

        _sut.MatchedFilePath.Should().Be(_file, "the canvas itself was not edited");
        _sut.GetUnchangedFilePath().Should().BeNull("the file is no longer what the canvas shows");
    }

    [Fact]
    public void FileDeleted_IsNotHandedOver()
    {
        _sut.LoadImage(_file);

        File.Delete(_file);

        _sut.GetUnchangedFilePath().Should().BeNull();
    }

    [Fact]
    public void PendingMove_IsNotInTheFile()
    {
        _sut.LoadImage(_file);
        _sut.LayerTransformTool.IsActive = true;
        _sut.ArmLayerTransform().Should().Be(LayerTransformEligibility.Ok);
        _sut.GetUnchangedFilePath().Should().Be(_file, "an open Move without a transform changes nothing");

        _sut.LayerTransformTool.Nudge(4, 0);

        _sut.HasPendingOperations.Should().BeTrue();
        _sut.GetUnchangedFilePath().Should().BeNull("a save would commit the open Move first");
    }

    [Fact]
    public void SaveOverAnUnknownExtension_DoesNotMatch()
    {
        // A save to ".tif" writes PNG bytes under that name, which the tools a hand-off reaches
        // cannot rely on; LoadLayeredTiff refuses the match for the same reason.
        var tif = Path.Combine(_tempDir.FullName, "layered.tif");
        File.WriteAllBytes(tif, _png);
        _sut.LoadImage(tif).Should().BeTrue("Skia sniffs the PNG bytes");
        _sut.AddLayer("scratch");
        _sut.SaveImage(tif).Should().BeTrue();

        _sut.MarkSavedOverFile(tif);

        _sut.MatchedFilePath.Should().BeNull();
    }

    [Fact]
    public void MatchedFilePath_NamesTheLoadedFile()
    {
        _sut.LoadImage(_file);

        _sut.MatchedFilePath.Should().Be(_file);
    }

    [Fact]
    public void LoadFromBytes_MatchesNoFile()
    {
        _sut.LoadImage(_file);

        _sut.LoadImage(_png).Should().BeTrue();

        _sut.MatchedFilePath.Should().BeNull();
        _sut.GetUnchangedFilePath().Should().BeNull();
    }

    [Fact]
    public void Clear_MatchesNoFile()
    {
        _sut.LoadImage(_file);

        _sut.Clear();

        _sut.MatchedFilePath.Should().BeNull();
        _sut.GetUnchangedFilePath().Should().BeNull();
    }
}
