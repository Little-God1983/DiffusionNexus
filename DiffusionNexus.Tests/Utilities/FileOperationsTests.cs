using DiffusionNexus.UI.Utilities;
using FluentAssertions;

namespace DiffusionNexus.Tests.Utilities;

/// <summary>
/// Unit tests for <see cref="FileOperations"/> against a real temporary directory.
/// <para>
/// Everything here stays on one volume — the cross-volume copy+delete fallback in
/// <see cref="FileOperations.MoveFile"/> cannot be provoked portably, but the tests
/// below do exercise the fallback path itself, because every <see cref="IOException"/>
/// (including <see cref="FileNotFoundException"/> and <see cref="DirectoryNotFoundException"/>)
/// routes through the same catch block.
/// </para>
/// </summary>
public sealed class FileOperationsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("dn-fileops-");
    private readonly FileOperations _sut = new();

    public void Dispose()
    {
        try
        {
            _root.Delete(recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup — a locked handle must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string PathIn(string name) => Path.Combine(_root.FullName, name);

    private string WriteFile(string name, string content)
    {
        var path = PathIn(name);
        File.WriteAllText(path, content);
        return path;
    }

    // ---------------------------------------------------------------
    //  MoveFile — happy path
    // ---------------------------------------------------------------

    [Fact]
    public void WhenMovingAFileThenTheSourceDisappearsAndTheDestinationHoldsTheContent()
    {
        var source = WriteFile("source.txt", "payload");
        var destination = PathIn("destination.txt");

        _sut.MoveFile(source, destination, overwrite: false);

        File.Exists(source).Should().BeFalse();
        File.Exists(destination).Should().BeTrue();
        File.ReadAllText(destination).Should().Be("payload");
    }

    [Fact]
    public void WhenMovingIntoASubdirectoryThenTheFileLandsThere()
    {
        var source = WriteFile("model.safetensors", "weights");
        var subdirectory = Directory.CreateDirectory(PathIn("nested")).FullName;
        var destination = Path.Combine(subdirectory, "model.safetensors");

        _sut.MoveFile(source, destination, overwrite: false);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(destination).Should().Be("weights");
    }

    [Fact]
    public void WhenMovingAnEmptyFileThenItIsStillMoved()
    {
        var source = WriteFile("empty.bin", string.Empty);
        var destination = PathIn("moved-empty.bin");

        _sut.MoveFile(source, destination, overwrite: false);

        File.Exists(source).Should().BeFalse();
        new FileInfo(destination).Length.Should().Be(0);
    }

    // ---------------------------------------------------------------
    //  MoveFile — overwrite semantics
    // ---------------------------------------------------------------

    [Fact]
    public void WhenMovingOntoAnExistingFileWithOverwriteThenTheDestinationIsReplaced()
    {
        var source = WriteFile("source.txt", "new content");
        var destination = WriteFile("destination.txt", "old content");

        _sut.MoveFile(source, destination, overwrite: true);

        File.Exists(source).Should().BeFalse();
        File.ReadAllText(destination).Should().Be("new content");
    }

    [Fact]
    public void WhenMovingOntoAnExistingFileWithoutOverwriteThenItThrowsAndNothingIsLost()
    {
        var source = WriteFile("source.txt", "new content");
        var destination = WriteFile("destination.txt", "old content");

        var act = () => _sut.MoveFile(source, destination, overwrite: false);

        // File.Move throws, the copy fallback throws for the same reason, and it surfaces.
        act.Should().Throw<IOException>();
        File.Exists(source).Should().BeTrue("the failed move must not consume the source");
        File.ReadAllText(source).Should().Be("new content");
        File.ReadAllText(destination).Should().Be("old content");
    }

    // ---------------------------------------------------------------
    //  MoveFile — failure paths through the catch block
    // ---------------------------------------------------------------

    [Fact]
    public void WhenMovingAMissingSourceThenTheFallbackRethrowsFileNotFound()
    {
        var source = PathIn("does-not-exist.txt");
        var destination = PathIn("destination.txt");

        var act = () => _sut.MoveFile(source, destination, overwrite: true);

        act.Should().Throw<FileNotFoundException>();
        File.Exists(destination).Should().BeFalse();
    }

    [Fact]
    public void WhenMovingIntoAMissingDirectoryThenTheFallbackRethrowsDirectoryNotFound()
    {
        var source = WriteFile("source.txt", "payload");
        var destination = Path.Combine(_root.FullName, "no-such-dir", "destination.txt");

        var act = () => _sut.MoveFile(source, destination, overwrite: true);

        act.Should().Throw<DirectoryNotFoundException>();
        File.Exists(source).Should().BeTrue("a failed move must leave the source in place");
    }

    // ---------------------------------------------------------------
    //  DeleteFile
    // ---------------------------------------------------------------

    [Fact]
    public void WhenDeletingAMissingFileThenNothingHappens()
    {
        var missing = PathIn("never-created.txt");

        var act = () => _sut.DeleteFile(missing);

        act.Should().NotThrow();
        File.Exists(missing).Should().BeFalse();
    }

    [Fact]
    public void WhenDeletingAnExistingFileThenItIsRemoved()
    {
        var path = WriteFile("doomed.txt", "bye");

        _sut.DeleteFile(path);

        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void WhenDeletingTheSameFileTwiceThenTheSecondCallIsANoOp()
    {
        var path = WriteFile("doomed.txt", "bye");
        _sut.DeleteFile(path);

        var act = () => _sut.DeleteFile(path);

        act.Should().NotThrow();
    }

    [Fact]
    public void WhenDeletingAPathThatIsADirectoryThenTheExistenceGuardSkipsIt()
    {
        // File.Exists is false for directories, so the guard short-circuits
        // instead of letting File.Delete throw UnauthorizedAccessException.
        var directory = Directory.CreateDirectory(PathIn("a-directory")).FullName;

        var act = () => _sut.DeleteFile(directory);

        act.Should().NotThrow();
        Directory.Exists(directory).Should().BeTrue();
    }

    // ---------------------------------------------------------------
    //  Remaining IFileOperations surface
    // ---------------------------------------------------------------

    [Fact]
    public void WhenAskedAboutFileExistenceThenItReflectsTheFileSystem()
    {
        var path = WriteFile("present.txt", "x");

        _sut.FileExists(path).Should().BeTrue();
        _sut.FileExists(PathIn("absent.txt")).Should().BeFalse();
    }

    [Fact]
    public void WhenCopyingAFileThenBothCopiesExistWithTheSameContent()
    {
        var source = WriteFile("source.txt", "payload");
        var destination = PathIn("copy.txt");

        _sut.CopyFile(source, destination, overwrite: false);

        File.ReadAllText(source).Should().Be("payload");
        File.ReadAllText(destination).Should().Be("payload");
    }

    [Fact]
    public void WhenCopyingOntoAnExistingFileWithoutOverwriteThenItThrows()
    {
        var source = WriteFile("source.txt", "new");
        var destination = WriteFile("destination.txt", "old");

        var act = () => _sut.CopyFile(source, destination, overwrite: false);

        act.Should().Throw<IOException>();
        File.ReadAllText(destination).Should().Be("old");
    }

    [Fact]
    public void WhenCopyingOntoAnExistingFileWithOverwriteThenTheDestinationIsReplaced()
    {
        var source = WriteFile("source.txt", "new");
        var destination = WriteFile("destination.txt", "old");

        _sut.CopyFile(source, destination, overwrite: true);

        File.ReadAllText(destination).Should().Be("new");
    }

    [Fact]
    public void WhenListingFilesThenOnlyFilesInThatDirectoryAreReturned()
    {
        WriteFile("a.txt", "1");
        WriteFile("b.txt", "2");
        var nested = Directory.CreateDirectory(PathIn("nested")).FullName;
        File.WriteAllText(Path.Combine(nested, "c.txt"), "3");

        var files = _sut.GetFiles(_root.FullName);

        files.Select(f => Path.GetFileName(f)).Should().BeEquivalentTo(new[] { "a.txt", "b.txt" });
    }

    [Fact]
    public void WhenListingAnEmptyDirectoryThenAnEmptyArrayIsReturned()
    {
        var empty = Directory.CreateDirectory(PathIn("empty")).FullName;

        _sut.GetFiles(empty).Should().BeEmpty();
    }

    [Fact]
    public void WhenCreatingADirectoryThatAlreadyExistsThenItIsANoOp()
    {
        var path = PathIn("created");
        _sut.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "keep.txt"), "content");

        var act = () => _sut.CreateDirectory(path);

        act.Should().NotThrow();
        Directory.Exists(path).Should().BeTrue();
        File.Exists(Path.Combine(path, "keep.txt")).Should().BeTrue("an existing directory must not be recreated");
    }

    [Fact]
    public void WhenCreatingNestedDirectoriesThenAllLevelsAreCreated()
    {
        var path = Path.Combine(_root.FullName, "one", "two", "three");

        _sut.CreateDirectory(path);

        Directory.Exists(path).Should().BeTrue();
    }
}
