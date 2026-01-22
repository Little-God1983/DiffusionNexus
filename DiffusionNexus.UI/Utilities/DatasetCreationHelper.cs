using DiffusionNexus.Domain.Services;
using DiffusionNexus.UI.ViewModels;

namespace DiffusionNexus.UI.Utilities;

public sealed class DatasetCreationOutcome
{
    public bool Success { get; init; }
    public bool StorageConfigured { get; init; }
    public string? ErrorMessage { get; init; }
    public DatasetCardViewModel? Dataset { get; init; }

    public static DatasetCreationOutcome Failed(string message, bool storageConfigured)
    {
        return new DatasetCreationOutcome
        {
            Success = false,
            StorageConfigured = storageConfigured,
            ErrorMessage = message
        };
    }

    public static DatasetCreationOutcome Created(DatasetCardViewModel dataset)
    {
        return new DatasetCreationOutcome
        {
            Success = true,
            StorageConfigured = true,
            Dataset = dataset
        };
    }
}

public static class DatasetCreationHelper
{
    public static async Task<DatasetCreationOutcome> TryCreateDatasetAsync(
        IAppSettingsService settingsService,
        CreateDatasetResult result)
    {
        var settings = await settingsService.GetSettingsAsync();

        if (string.IsNullOrWhiteSpace(settings.DatasetStoragePath))
        {
            return DatasetCreationOutcome.Failed(
                "Please configure the Dataset Storage Path in Settings first.",
                storageConfigured: false);
        }

        if (!Directory.Exists(settings.DatasetStoragePath))
        {
            return DatasetCreationOutcome.Failed(
                "The configured Dataset Storage Path does not exist. Please update it in Settings.",
                storageConfigured: false);
        }

        var sanitizedName = result.Name;
        var datasetPath = Path.Combine(settings.DatasetStoragePath, sanitizedName);

        if (Directory.Exists(datasetPath))
        {
            return DatasetCreationOutcome.Failed(
                $"A dataset named '{sanitizedName}' already exists.",
                storageConfigured: true);
        }

        try
        {
            Directory.CreateDirectory(datasetPath);
            var v1Path = Path.Combine(datasetPath, "V1");
            Directory.CreateDirectory(v1Path);

            var newDataset = new DatasetCardViewModel
            {
                Name = sanitizedName,
                FolderPath = datasetPath,
                IsVersionedStructure = true,
                CurrentVersion = 1,
                TotalVersions = 1,
                ImageCount = 0,
                VideoCount = 0,
                CategoryId = result.CategoryId,
                CategoryOrder = result.CategoryOrder,
                CategoryName = result.CategoryName,
                Type = result.Type,
                IsNsfw = result.IsNsfw
            };

            newDataset.VersionNsfwFlags[1] = result.IsNsfw;
            newDataset.SaveMetadata();

            return DatasetCreationOutcome.Created(newDataset);
        }
        catch (Exception ex)
        {
            return DatasetCreationOutcome.Failed(
                $"Failed to create dataset: {ex.Message}",
                storageConfigured: true);
        }
    }
}
