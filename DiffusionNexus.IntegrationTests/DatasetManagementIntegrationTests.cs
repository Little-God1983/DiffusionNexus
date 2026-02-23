using System.Collections.ObjectModel;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using DiffusionNexus.UI.ViewModels.Tabs;
using DiffusionNexus.UI.Views.Dialogs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DiffusionNexus.IntegrationTests;

public class DatasetManagementIntegrationTests : IClassFixture<TestAppHost>
{
    private readonly TestAppHost _host;

    public DatasetManagementIntegrationTests(TestAppHost host)
    {
        _host = host;
    }

    [Fact]
    public async Task AddImagesCommand_AddsImageToDatasetAndCopiesFile()
    {
        var settingsService = _host.Services.GetRequiredService<IAppSettingsService>();
        var storageService = _host.Services.GetRequiredService<IDatasetStorageService>();
        var eventAggregator = _host.Services.GetRequiredService<IDatasetEventAggregator>();
        var datasetState = _host.Services.GetRequiredService<IDatasetState>();

        var viewModel = new DatasetManagementViewModel(settingsService, storageService, eventAggregator, datasetState)
        {
            DialogService = new StubDialogService()
        };

        var datasetPath = _host.CreateDatasetFolder("GoldenPathDataset");
        var datasetCard = DatasetCardViewModel.FromFolder(datasetPath);
        await viewModel.OpenDatasetCommand.ExecuteAsync(datasetCard);

        var sourceImagePath = CreateTempPng(_host.RootPath);
        viewModel.DialogService = new StubDialogService(sourceImagePath);

        await viewModel.AddImagesCommand.ExecuteAsync(null);

        // Check for any reported errors
        viewModel.StatusMessage.Should().NotContain("Error", because: viewModel.StatusMessage);

        var expectedPath = Path.Combine(datasetCard.CurrentVersionFolderPath, Path.GetFileName(sourceImagePath));
        File.Exists(expectedPath).Should().BeTrue("the file should be copied into the managed dataset folder");

        // Ensure the view model has processed the file system changes
        await viewModel.RefreshActiveDatasetAsync();

        viewModel.DatasetImages.Should().ContainSingle(image =>
            string.Equals(Path.GetFileName(image.ImagePath), Path.GetFileName(sourceImagePath), StringComparison.OrdinalIgnoreCase));
    }

    private static string CreateTempPng(string rootPath)
    {
        var imagePath = Path.Combine(rootPath, $"dataset-test-{Guid.NewGuid():N}.png");
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGMAAQAABQABDQottAAAAABJRU5ErkJggg==");
        File.WriteAllBytes(imagePath, imageBytes);
        return imagePath;
    }

    private sealed class StubDialogService : IDialogService
    {
        private readonly string? _filePath;

        public StubDialogService(string? filePath = null)
        {
            _filePath = filePath;
        }

        public Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowOpenFileDialogAsync(string title, string startFolder, string? filter) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowOpenFolderDialogAsync(string title) =>
            Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<string?> ShowInputAsync(string title, string message, string? defaultValue = null) =>
            Task.FromResult<string?>(null);

        public Task<List<string>?> ShowFileDropDialogAsync(string title) =>
            Task.FromResult<List<string>?>(null);

        public Task<List<string>?> ShowFileDropDialogAsync(string title, params string[] allowedExtensions) =>
            Task.FromResult<List<string>?>(null);

        public Task<List<string>?> ShowFileDropDialogAsync(string title, IEnumerable<string> initialFiles) =>
            Task.FromResult<List<string>?>(null);

        public Task<int> ShowOptionsAsync(string title, string message, params string[] options) =>
            Task.FromResult(-1);

        public Task<ExportDatasetResult> ShowExportDialogAsync(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles, IEnumerable<InstallerPackage>? aiToolkitInstances = null) =>
            Task.FromResult(new ExportDatasetResult { Confirmed = false });

        public Task<CreateDatasetResult> ShowCreateDatasetDialogAsync(IEnumerable<DatasetCategoryViewModel> availableCategories) =>
            Task.FromResult(CreateDatasetResult.Cancelled());

        public Task ShowImageViewerDialogAsync(
            ObservableCollection<DatasetImageViewModel> images,
            int startIndex,
            IDatasetEventAggregator? eventAggregator = null,
            Action<DatasetImageViewModel>? onSendToImageEditor = null,
            Action<DatasetImageViewModel>? onSendToCaptioning = null,
            Action<DatasetImageViewModel>? onDeleteRequested = null,
            bool showRatingControls = true,
            Func<string, Task<bool>>? onToggleFavorite = null,
            Func<string, bool>? isFavoriteCheck = null) =>
            Task.CompletedTask;

        public Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath, IEnumerable<DatasetCardViewModel> availableDatasets) =>
            Task.FromResult(SaveAsResult.Cancelled());

        public Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath, IEnumerable<DatasetCardViewModel> availableDatasets,
            string? preselectedDatasetName, int? preselectedVersion, bool hasLayers = false) =>
            Task.FromResult(SaveAsResult.Cancelled());




        public Task<ReplaceImageResult> ShowReplaceImageDialogAsync(DatasetImageViewModel originalImage) =>
            Task.FromResult(ReplaceImageResult.Cancelled());

        public Task<bool> ShowBackupCompareDialogAsync(BackupCompareData currentStats, BackupCompareData backupStats) =>
            Task.FromResult(false);

        public Task<CreateVersionResult> ShowCreateVersionDialogAsync(
            int currentVersion,
            IReadOnlyList<int> availableVersions,
            IEnumerable<DatasetImageViewModel> mediaFiles) =>
            Task.FromResult(CreateVersionResult.Cancelled());

        public Task ShowCaptioningDialogAsync(
            ICaptioningService captioningService,
            IEnumerable<DatasetCardViewModel> availableDatasets,
            IDatasetEventAggregator? eventAggregator = null,
            DatasetCardViewModel? initialDataset = null,
            int? initialVersion = null) =>
            Task.CompletedTask;

        public Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(IEnumerable<FileConflictItem> conflicts) =>
            Task.FromResult(new FileConflictResolutionResult { Confirmed = false });

        public Task<FileConflictResolutionResult> ShowFileConflictDialogAsync(
            IEnumerable<FileConflictItem> conflicts,
            IEnumerable<string> nonConflictingFilePaths) =>
            Task.FromResult(new FileConflictResolutionResult { Confirmed = false });

        public Task<FileDropWithConflictResult?> ShowFileDropDialogWithConflictDetectionAsync(
            string title,
            IEnumerable<string> existingFileNames,
            string destinationFolder)
        {
            if (string.IsNullOrWhiteSpace(_filePath))
            {
                return Task.FromResult<FileDropWithConflictResult?>(new FileDropWithConflictResult
                {
                    Cancelled = true
                });
            }

            return Task.FromResult<FileDropWithConflictResult?>(new FileDropWithConflictResult
            {
                NonConflictingFiles = [_filePath]
            });
        }

        public Task<SelectVersionsToDeleteResult> ShowSelectVersionsToDeleteDialogAsync(DatasetCardViewModel dataset) =>
            Task.FromResult(SelectVersionsToDeleteResult.Cancelled());

        public Task<AddToDatasetResult> ShowAddToDatasetDialogAsync(
            int selectedFileCount,
            IEnumerable<DatasetCardViewModel> availableDatasets) =>
            Task.FromResult(AddToDatasetResult.Cancelled());

        public Task<CaptionCompareResult> ShowCaptionCompareDialogAsync(string imagePath, string currentCaption, string newCaption) =>
            Task.FromResult(CaptionCompareResult.Cancelled());

        public Task<AddExistingInstallationResult> ShowAddExistingInstallationDialogAsync(string initialPath) =>
            Task.FromResult(AddExistingInstallationResult.Cancelled());

        public Task<AddExistingInstallationResult> ShowEditInstallationDialogAsync(
            string name, string installationPath, DiffusionNexus.Domain.Enums.InstallerType type,
            string executablePath, string outputFolderPath) =>
            Task.FromResult(AddExistingInstallationResult.Cancelled());
    }
}
