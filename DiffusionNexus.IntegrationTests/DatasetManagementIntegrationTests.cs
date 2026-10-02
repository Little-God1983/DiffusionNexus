using System.Collections.ObjectModel;
using DiffusionNexus.Civitai;
using DiffusionNexus.Domain.Entities;
using DiffusionNexus.Domain.Enums;
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

    [Fact]
    public async Task CreateVersion_CarriesRatingsIntoTheNewVersionsRatingsFile()
    {
        var viewModel = CreateViewModel();
        var datasetPath = _host.CreateDatasetFolder($"RatingsVersion_{Guid.NewGuid():N}");
        var v1 = Path.Combine(datasetPath, "V1");
        File.Copy(CreateTempPng(_host.RootPath), Path.Combine(v1, "a.png"));
        File.Copy(CreateTempPng(_host.RootPath), Path.Combine(v1, "b.png"));
        File.Copy(CreateTempPng(_host.RootPath), Path.Combine(v1, "c.png"));
        ImageRatingStore.Shared.Set(Path.Combine(v1, "b.png"), ImageRatingStatus.Rejected);
        // Written after the set, so it stays a legacy file next to the new ratings file
        File.WriteAllText(Path.Combine(v1, "a.rating"), "Approved");

        var datasetCard = DatasetCardViewModel.FromFolder(datasetPath);
        await viewModel.OpenDatasetCommand.ExecuteAsync(datasetCard);
        viewModel.DialogService = new StubDialogService
        {
            CreateVersionResult = new CreateVersionResult
            {
                Confirmed = true,
                SourceOption = VersionSourceOption.CopyFromVersion,
                SourceVersion = 1,
                CopyImages = true,
                CopyRatings = true,
                IncludeProductionReady = true,
                IncludeUnrated = true,
                IncludeTrash = true,
            }
        };

        await viewModel.IncrementVersionCommand.ExecuteAsync(null);

        var v2 = Path.Combine(datasetPath, "V2");
        var store = new ImageRatingStore();
        store.Get(Path.Combine(v2, "a.png")).Should().Be(ImageRatingStatus.Approved);
        store.Get(Path.Combine(v2, "b.png")).Should().Be(ImageRatingStatus.Rejected);
        store.Get(Path.Combine(v2, "c.png")).Should().Be(ImageRatingStatus.Unrated);
        File.Exists(Path.Combine(v2, ImageRatingStore.RatingsFileName)).Should().BeTrue();
        Directory.GetFiles(v2, "*.rating").Should().BeEmpty("the new version starts in the new format");
    }

    [Fact]
    public async Task ReplaceImage_WithAnotherFileName_KeepsTheRating()
    {
        var viewModel = CreateViewModel();
        var datasetPath = _host.CreateDatasetFolder($"RatingsReplace_{Guid.NewGuid():N}");
        var v1 = Path.Combine(datasetPath, "V1");
        var original = Path.Combine(v1, "a.png");
        var other = Path.Combine(v1, "other.png");
        File.Copy(CreateTempPng(_host.RootPath), original);
        File.Copy(CreateTempPng(_host.RootPath), other);
        ImageRatingStore.Shared.Set(original, ImageRatingStatus.Rejected);
        ImageRatingStore.Shared.Set(other, ImageRatingStatus.Approved);

        var datasetCard = DatasetCardViewModel.FromFolder(datasetPath);
        await viewModel.OpenDatasetCommand.ExecuteAsync(datasetCard);
        var image = viewModel.DatasetImages.Single(i => Path.GetFileName(i.ImagePath) == "a.png");
        var replacement = Path.Combine(_host.RootPath, $"replacement-{Guid.NewGuid():N}.png");
        File.Copy(CreateTempPng(_host.RootPath), replacement);
        viewModel.DialogService = new StubDialogService
        {
            ReplaceImageResult = new ReplaceImageResult
            {
                Confirmed = true,
                Action = ReplaceAction.Replace,
                NewFilePath = replacement,
            }
        };

        await viewModel.ReplaceImageCommand.ExecuteAsync(image);

        var replaced = Path.Combine(v1, Path.GetFileName(replacement));
        var store = new ImageRatingStore();
        store.Get(replaced).Should().Be(ImageRatingStatus.Rejected);
        store.Get(original).Should().Be(ImageRatingStatus.Unrated, "the old name must not keep a stale rating");
        store.Get(other).Should().Be(ImageRatingStatus.Approved);
    }

    private DatasetManagementViewModel CreateViewModel()
    {
        return new DatasetManagementViewModel(
            _host.Services.GetRequiredService<IAppSettingsService>(),
            _host.Services.GetRequiredService<IDatasetStorageService>(),
            _host.Services.GetRequiredService<IDatasetEventAggregator>(),
            _host.Services.GetRequiredService<IDatasetState>())
        {
            DialogService = new StubDialogService()
        };
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

        public ReplaceImageResult ReplaceImageResult { get; init; } = ReplaceImageResult.Cancelled();

        public CreateVersionResult CreateVersionResult { get; init; } = CreateVersionResult.Cancelled();

        public Task<string?> ShowOpenFileDialogAsync(string title, string? filter = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowOpenFileDialogAsync(string title, string startFolder, string? filter) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowSaveFileDialogAsync(string title, string? defaultFileName = null, string? filter = null) =>
            Task.FromResult<string?>(null);

        public Task<string?> ShowOpenFolderDialogAsync(string title) =>
            Task.FromResult<string?>(null);

        public Task ShowMessageAsync(string title, string message) => Task.CompletedTask;

        public Task ShowFeedbackDialogAsync() => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message) => Task.FromResult(false);

        public Task<string?> ShowInputAsync(string title, string message, string? defaultValue = null) =>
            Task.FromResult<string?>(null);

        public Task<FileDropResult?> ShowFileDropDialogAsync(string title) =>
            Task.FromResult<FileDropResult?>(null);

        public Task<FileDropResult?> ShowFileDropDialogAsync(string title, params string[] allowedExtensions) =>
            Task.FromResult<FileDropResult?>(null);

        public Task<FileDropResult?> ShowFileDropDialogAsync(string title, IEnumerable<string> initialFiles) =>
            Task.FromResult<FileDropResult?>(null);

        public Task<int> ShowOptionsAsync(string title, string message, params string[] options) =>
            Task.FromResult(-1);

        public Task<ExportDatasetResult> ShowExportDialogAsync(string datasetName, IEnumerable<DatasetImageViewModel> mediaFiles, IEnumerable<InstallerPackage>? aiToolkitInstances = null) =>
            Task.FromResult(new ExportDatasetResult { Confirmed = false });

        public Task<CreateDatasetResult> ShowCreateDatasetDialogAsync(IEnumerable<DatasetCategoryViewModel> availableCategories) =>
            Task.FromResult(CreateDatasetResult.Cancelled());

        public Task<CreateTrainingRunResult> ShowCreateTrainingRunDialogAsync(
            ICivitaiBaseModelCatalog? baseModelCatalog,
            CivitaiCategory defaultCategory,
            IEnumerable<string>? existingRunNames = null) =>
            Task.FromResult(CreateTrainingRunResult.Cancelled());

        public Task ShowImageViewerDialogAsync(
            ObservableCollection<DatasetImageViewModel> images,
            int startIndex,
            IDatasetEventAggregator? eventAggregator = null,
            Action<DatasetImageViewModel>? onSendToImageEditor = null,
            Action<DatasetImageViewModel>? onSendToCaptioning = null,
            Action<DatasetImageViewModel>? onDeleteRequested = null,
            bool showRatingControls = true,
            Func<string, Task<bool>>? onToggleFavorite = null,
            Func<string, bool>? isFavoriteCheck = null,
            IVideoThumbnailService? videoThumbnailService = null,
            ITagIndexService? tagIndexService = null,
            Action<string, bool>? onNsfwRatingChanged = null) =>
            Task.CompletedTask;

        public Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath, IEnumerable<DatasetCardViewModel> availableDatasets) =>
            Task.FromResult(SaveAsResult.Cancelled());

        public Task<SaveAsResult> ShowSaveAsDialogAsync(string originalFilePath, IEnumerable<DatasetCardViewModel> availableDatasets,
            string? preselectedDatasetName, int? preselectedVersion, bool hasLayers = false) =>
            Task.FromResult(SaveAsResult.Cancelled());




        public Task<ReplaceImageResult> ShowReplaceImageDialogAsync(DatasetImageViewModel originalImage) =>
            Task.FromResult(ReplaceImageResult);

        public Task<LoraDeleteResult?> ShowSelectLoraVersionsToDeleteDialogAsync(
            string displayName,
            IEnumerable<ModelVersion> versions,
            IReadOnlyList<Model> allGroupedModels) =>
            Task.FromResult<LoraDeleteResult?>(null);

        public Task<CivitaiTokenDialogResult> ShowCivitaiTokenDialogAsync() =>
            Task.FromResult(new CivitaiTokenDialogResult(false, string.Empty));

        public Task<AssignCivitaiIdsDialogResult> ShowAssignCivitaiIdsDialogAsync() =>
            Task.FromResult(new AssignCivitaiIdsDialogResult(false, null, null));

        public Task<SyncPlanDialogResult> ShowSyncPlanDialogAsync(SyncPlanDialogViewModel viewModel) =>
            Task.FromResult(SyncPlanDialogResult.Cancelled());

        public Task ShowSyncReportDialogAsync(SyncReportDialogViewModel viewModel) => Task.CompletedTask;

        public Task<bool> ShowBackupCompareDialogAsync(BackupCompareData currentStats, BackupCompareData backupStats) =>
            Task.FromResult(false);

        public Task<CreateVersionResult> ShowCreateVersionDialogAsync(
            int currentVersion,
            IReadOnlyList<int> availableVersions,
            IEnumerable<DatasetImageViewModel> mediaFiles) =>
            Task.FromResult(CreateVersionResult);

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

        public Task<AddToTrainingRunResult> ShowAddToTrainingRunDialogAsync(
            int selectedFileCount,
            IEnumerable<DatasetCardViewModel> availableDatasets) =>
            Task.FromResult(AddToTrainingRunResult.Cancelled());

        public Task<CaptionCompareResult> ShowCaptionCompareDialogAsync(string imagePath, string currentCaption, string newCaption) =>
            Task.FromResult(CaptionCompareResult.Cancelled());

        public Task<AddExistingInstallationResult> ShowAddExistingInstallationDialogAsync(string initialPath) =>
            Task.FromResult(AddExistingInstallationResult.Cancelled());

        public Task<RemoveInstallationResult> ShowRemoveInstallationDialogAsync(RemoveInstallationPrompt prompt) =>
            Task.FromResult(RemoveInstallationResult.Cancelled());

        public Task<AddExistingInstallationResult> ShowEditInstallationDialogAsync(
            string name, string installationPath, DiffusionNexus.Domain.Enums.InstallerType type,
            string executablePath, string outputFolderPath) =>
            Task.FromResult(AddExistingInstallationResult.Cancelled());

        public Task<DownloadLoraVersionResult> ShowDownloadLoraVersionDialogAsync(
            string modelName, DiffusionNexus.Civitai.Models.CivitaiModelVersion civitaiVersion,
            IReadOnlyList<string> sourceFolders, string? category = null) =>
            Task.FromResult(DownloadLoraVersionResult.Cancelled());

        public Task<DownloadLoraResult> ShowDownloadLoraDialogAsync(IReadOnlyList<string> sourceFolders) =>
            Task.FromResult(DownloadLoraResult.Cancelled());

        public Task<ExportTrainingRunsResult> ShowExportTrainingRunsDialogAsync(
            string datasetName,
            int datasetVersion,
            IEnumerable<TrainingRunCardViewModel> trainingRuns) =>
            Task.FromResult(ExportTrainingRunsResult.Cancelled());

        public Task<int> ShowDuplicateFixerAsync(IEnumerable<DiffusionNexus.UI.ViewModels.Tabs.DuplicateClusterItemViewModel> clusters) =>
            Task.FromResult(0);

        public Task<int> ShowLoraDuplicateFixerAsync(IEnumerable<DiffusionNexus.Service.Services.LoraDuplicateGroup> groups) =>
            Task.FromResult(0);

        public Task<int> ShowColorFixerAsync(IEnumerable<DiffusionNexus.UI.ViewModels.Tabs.ColorDistributionItemViewModel> images) =>
            Task.FromResult(0);

        public Task ShowImageQualityFixerAsync(DiffusionNexus.UI.ViewModels.Dialogs.ImageQualityFixerViewModel viewModel) =>
            Task.CompletedTask;
    }
}
