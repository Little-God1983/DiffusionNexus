using System.Collections.ObjectModel;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Controls;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// <see cref="ImageActionPaths.KeepInPlace"/> (#586): the Image Editor hands Add To… the file it still
/// has open, so picking Move must copy it rather than pull the file out from under the editor.
/// </summary>
public sealed class ImageActionsKeepInPlaceTests : IDisposable
{
    private readonly DirectoryInfo _tempDir = Directory.CreateTempSubdirectory();

    public void Dispose() => _tempDir.Delete(recursive: true);

    private (ImageActionsViewModel Vm, string Source, string Destination, List<IReadOnlyList<string>> Moved) Arrange(bool keepInPlace)
    {
        var source = Path.Combine(_tempDir.FullName, "open", "photo.png");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, [1, 2, 3]);

        var dataset = new DatasetCardViewModel
        {
            Name = "Target",
            FolderPath = Path.Combine(_tempDir.FullName, "dataset"),
            IsVersionedStructure = true,
            CurrentVersion = 1,
        };

        var dialogs = new Mock<IDialogService>();
        dialogs.Setup(d => d.ShowAddToDatasetDialogAsync(It.IsAny<int>(), It.IsAny<IEnumerable<DatasetCardViewModel>>()))
            .ReturnsAsync(new AddToDatasetResult
            {
                Confirmed = true,
                ImportAction = DatasetImportAction.Move,
                DestinationOption = DatasetDestinationOption.ExistingDataset,
                VersionOption = DatasetVersionOption.UseExistingVersion,
                SelectedDataset = dataset,
                SelectedVersion = 1,
            });

        var state = new Mock<IDatasetState>();
        state.Setup(s => s.Datasets).Returns(new ObservableCollection<DatasetCardViewModel> { dataset });

        var vm = new ImageActionsViewModel(state.Object, Mock.Of<IDatasetEventAggregator>(), settingsService: Mock.Of<IAppSettingsService>())
        {
            DialogService = dialogs.Object,
            CanAct = true,
            PathProvider = () => Task.FromResult(new ImageActionPaths([source], KeepInPlace: keepInPlace)),
        };
        var moved = new List<IReadOnlyList<string>>();
        vm.FilesMoved += files => moved.Add(files);

        return (vm, source, Path.Combine(dataset.GetVersionFolderPath(1), "photo.png"), moved);
    }

    [Fact]
    public async Task Move_OfAFileKeptInPlace_Copies()
    {
        var (vm, source, destination, moved) = Arrange(keepInPlace: true);

        await vm.AddToDatasetCommand.ExecuteAsync(null);

        File.Exists(source).Should().BeTrue("the editor still has it open");
        File.Exists(destination).Should().BeTrue();
        moved.Should().BeEmpty();
        vm.StatusMessage.Should().Contain("Copied rather than moved");
    }

    [Fact]
    public async Task Move_OfAnOrdinaryFile_StillMoves()
    {
        var (vm, source, destination, moved) = Arrange(keepInPlace: false);

        await vm.AddToDatasetCommand.ExecuteAsync(null);

        File.Exists(source).Should().BeFalse();
        File.Exists(destination).Should().BeTrue();
        moved.Should().ContainSingle();
    }
}
