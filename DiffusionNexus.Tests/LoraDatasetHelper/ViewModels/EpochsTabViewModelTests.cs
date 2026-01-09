using DiffusionNexus.Domain.Entities;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels.Tabs;
using FluentAssertions;
using Moq;

namespace DiffusionNexus.Tests.LoraDatasetHelper.ViewModels;

/// <summary>
/// Unit tests for <see cref="EpochsTabViewModel"/>.
/// Tests initialization, file loading, selection, and collection properties.
/// </summary>
public class EpochsTabViewModelTests : IDisposable
{
    private readonly string _testTempPath;
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;

    public EpochsTabViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"EpochsTabTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors in tests
            }
        }
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_WithNullEventAggregator_ThrowsArgumentNullException()
    {
        // Act
        var act = () => new EpochsTabViewModel(null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("eventAggregator");
    }

    [Fact]
    public void Constructor_InitializesWithEmptyCollection()
    {
        // Act
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);

        // Assert
        vm.EpochFiles.Should().BeEmpty();
        vm.HasNoEpochs.Should().BeTrue();
        vm.HasEpochs.Should().BeFalse();
        vm.SelectionCount.Should().Be(0);
        vm.HasSelection.Should().BeFalse();
    }

    #endregion

    #region Initialize Tests

    [Fact]
    public void Initialize_SetsEpochsFolderPath()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1");
        Directory.CreateDirectory(versionPath);

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.EpochsFolderPath.Should().Be(Path.Combine(versionPath, "Epochs"));
    }

    [Fact]
    public void Initialize_WhenEpochsFolderDoesNotExist_LoadsEmptyCollection()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_NoEpochs");
        Directory.CreateDirectory(versionPath);

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.EpochFiles.Should().BeEmpty();
        vm.HasNoEpochs.Should().BeTrue();
    }

    [Fact]
    public void Initialize_WhenEpochsFolderHasFiles_LoadsEpochFiles()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_WithEpochs");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        // Create test epoch files
        File.WriteAllText(Path.Combine(epochsPath, "model_epoch1.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "model_epoch2.pt"), "");
        File.WriteAllText(Path.Combine(epochsPath, "model_epoch3.pth"), "");
        File.WriteAllText(Path.Combine(epochsPath, "model_epoch4.gguf"), "");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.EpochFiles.Should().HaveCount(4);
        vm.HasEpochs.Should().BeTrue();
        vm.HasNoEpochs.Should().BeFalse();
    }

    [Fact]
    public void Initialize_IgnoresNonEpochFiles()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Mixed");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        // Create mix of epoch and non-epoch files
        File.WriteAllText(Path.Combine(epochsPath, "model.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "readme.txt"), "");
        File.WriteAllText(Path.Combine(epochsPath, "config.json"), "");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.EpochFiles.Should().HaveCount(1);
        vm.EpochFiles[0].FileName.Should().Be("model.safetensors");
    }

    #endregion

    #region Selection Tests

    [Fact]
    public void SelectAll_SelectsAllEpochFiles()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Selection");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "epoch1.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "epoch2.safetensors"), "");

        vm.Initialize(versionPath);

        // Act
        vm.SelectAllCommand.Execute(null);

        // Assert
        vm.EpochFiles.Should().AllSatisfy(e => e.IsSelected.Should().BeTrue());
        vm.SelectionCount.Should().Be(2);
        vm.HasSelection.Should().BeTrue();
    }

    [Fact]
    public void ClearSelection_DeselectsAllEpochFiles()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_ClearSelection");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "epoch1.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "epoch2.safetensors"), "");

        vm.Initialize(versionPath);
        vm.SelectAllCommand.Execute(null);

        // Act
        vm.ClearSelectionCommand.Execute(null);

        // Assert
        vm.EpochFiles.Should().AllSatisfy(e => e.IsSelected.Should().BeFalse());
        vm.SelectionCount.Should().Be(0);
        vm.HasSelection.Should().BeFalse();
    }

    [Fact]
    public void SelectionCount_UpdatesWhenIndividualSelectionChanges()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_IndividualSelection");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "epoch1.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "epoch2.safetensors"), "");

        vm.Initialize(versionPath);

        // Act
        vm.EpochFiles[0].IsSelected = true;

        // Assert
        vm.SelectionCount.Should().Be(1);
        vm.HasSelection.Should().BeTrue();
    }

    #endregion

    #region LoadEpochFiles Tests

    [Fact]
    public void LoadEpochFiles_SortsFilesByName()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Sorting");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "z_model.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "a_model.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "m_model.safetensors"), "");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.EpochFiles[0].FileName.Should().Be("a_model.safetensors");
        vm.EpochFiles[1].FileName.Should().Be("m_model.safetensors");
        vm.EpochFiles[2].FileName.Should().Be("z_model.safetensors");
    }

    [Fact]
    public void LoadEpochFiles_ClearsExistingFiles()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Clear");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "epoch1.safetensors"), "");
        vm.Initialize(versionPath);
        vm.EpochFiles.Should().HaveCount(1);

        // Delete the file
        File.Delete(Path.Combine(epochsPath, "epoch1.safetensors"));

        // Act
        vm.RefreshCommand.Execute(null);

        // Assert
        vm.EpochFiles.Should().BeEmpty();
    }

    #endregion

    #region StatusMessage Tests

    [Fact]
    public void StatusMessage_WhenFilesLoaded_ShowsCount()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_Status");
        var epochsPath = Path.Combine(versionPath, "Epochs");
        Directory.CreateDirectory(epochsPath);

        File.WriteAllText(Path.Combine(epochsPath, "epoch1.safetensors"), "");
        File.WriteAllText(Path.Combine(epochsPath, "epoch2.safetensors"), "");

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.StatusMessage.Should().Contain("2");
    }

    [Fact]
    public void StatusMessage_WhenNoFiles_IsNull()
    {
        // Arrange
        var vm = new EpochsTabViewModel(_mockEventAggregator.Object);
        var versionPath = Path.Combine(_testTempPath, "V1_NoStatus");
        Directory.CreateDirectory(versionPath);

        // Act
        vm.Initialize(versionPath);

        // Assert
        vm.StatusMessage.Should().BeNull();
    }

    #endregion

    #region Static Properties Tests

    [Fact]
    public void SupportedExtensionsText_ContainsExpectedExtensions()
    {
        EpochsTabViewModel.SupportedExtensionsText.Should().Contain(".safetensors");
        EpochsTabViewModel.SupportedExtensionsText.Should().Contain(".pt");
        EpochsTabViewModel.SupportedExtensionsText.Should().Contain(".pth");
        EpochsTabViewModel.SupportedExtensionsText.Should().Contain(".gguf");
    }

    #endregion
}

/// <summary>
/// Unit tests for <see cref="EpochFileViewModel"/>.
/// </summary>
public class EpochFileViewModelTests : IDisposable
{
    private readonly string _testTempPath;
    private readonly Mock<IDatasetEventAggregator> _mockEventAggregator;

    public EpochFileViewModelTests()
    {
        _testTempPath = Path.Combine(Path.GetTempPath(), $"EpochFileVmTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempPath);
        _mockEventAggregator = new Mock<IDatasetEventAggregator>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_testTempPath))
        {
            try
            {
                Directory.Delete(_testTempPath, recursive: true);
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }

    [Fact]
    public void Constructor_WithNullItem_ThrowsArgumentNullException()
    {
        // Arrange
        var parent = new EpochsTabViewModel(_mockEventAggregator.Object);

        // Act
        var act = () => new EpochFileViewModel(null!, parent);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("item");
    }

    [Fact]
    public void Constructor_WithNullParent_ThrowsArgumentNullException()
    {
        // Arrange
        var item = new EpochFileItem { FileName = "test.safetensors" };

        // Act
        var act = () => new EpochFileViewModel(item, null!);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("parent");
    }

    [Fact]
    public void Properties_DelegateToItem()
    {
        // Arrange
        var epochsPath = Path.Combine(_testTempPath, "Epochs");
        Directory.CreateDirectory(epochsPath);
        var filePath = Path.Combine(epochsPath, "model.safetensors");
        File.WriteAllText(filePath, "test content");

        var item = EpochFileItem.FromFile(filePath);
        var parent = new EpochsTabViewModel(_mockEventAggregator.Object);

        // Act
        var vm = new EpochFileViewModel(item, parent);

        // Assert
        vm.FileName.Should().Be(item.FileName);
        vm.DisplayName.Should().Be(item.DisplayName);
        vm.FilePath.Should().Be(item.FilePath);
        vm.FileSizeDisplay.Should().Be(item.FileSizeDisplay);
        vm.Extension.Should().Be(item.Extension);
    }

    [Fact]
    public void IsSelected_WhenChanged_NotifiesParent()
    {
        // Arrange
        var epochsPath = Path.Combine(_testTempPath, "Epochs_Selected");
        Directory.CreateDirectory(epochsPath);
        var filePath = Path.Combine(epochsPath, "model.safetensors");
        File.WriteAllText(filePath, "test");

        var parent = new EpochsTabViewModel(_mockEventAggregator.Object);
        parent.EpochsFolderPath = epochsPath;
        parent.LoadEpochFiles();

        var vm = parent.EpochFiles[0];

        // Act
        vm.IsSelected = true;

        // Assert
        parent.HasSelection.Should().BeTrue();
        parent.SelectionCount.Should().Be(1);
    }
}
