using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="PresentationTabViewModel"/> add-files flows.
/// </summary>
public class PresentationTabViewModelTests : IDisposable
{
    private readonly string _testTempPath;
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator = new();

    public PresentationTabViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"PresentationTabTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_testTempPath, recursive: true); } catch { }
    }

    [Fact]
    public async Task AddMediaFilesCommand_DeletesZipExtractionDirectories_AfterCopyingFiles()
    {
        var (vm, extractDir) = Arrange("cover.png");

        await vm.AddMediaFilesCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(vm.PresentationFolderPath, "cover.png")).Should().BeTrue();
        Directory.Exists(extractDir).Should().BeFalse("the extraction folder is deleted once the files are in place");
    }

    [Fact]
    public async Task AddDocumentFilesCommand_DeletesZipExtractionDirectories_AfterCopyingFiles()
    {
        var (vm, extractDir) = Arrange("readme.pdf");

        await vm.AddDocumentFilesCommand.ExecuteAsync(null);

        File.Exists(Path.Combine(vm.PresentationFolderPath, "readme.pdf")).Should().BeTrue();
        Directory.Exists(extractDir).Should().BeFalse("the extraction folder is deleted once the files are in place");
    }

    /// <summary>
    /// Simulates the file-drop dialog having expanded a ZIP into a temp folder and returning
    /// a single file that lives inside it.
    /// </summary>
    private (PresentationTabViewModel Vm, string ExtractDir) Arrange(string fileName)
    {
        var extractDir = Path.Combine(_testTempPath, "DiffusionNexus_ZipExtract_test");
        Directory.CreateDirectory(extractDir);
        var extracted = Path.Combine(extractDir, fileName);
        File.WriteAllText(extracted, "content");

        var dialog = new Mock<IDialogService>();
        dialog.Setup(d => d.ShowFileDropDialogAsync(It.IsAny<string>(), It.IsAny<string[]>()))
            .ReturnsAsync(new FileDropResult
            {
                Files = [extracted],
                TemporaryDirectories = [extractDir]
            });

        var vm = new PresentationTabViewModel(_mockEventAggregator.Object)
        {
            PresentationFolderPath = Path.Combine(_testTempPath, "Presentation"),
            DialogService = dialog.Object
        };
        return (vm, extractDir);
    }
}
