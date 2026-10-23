using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.Utilities;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.LoraDatasetHelper.Utilities;

/// <summary>
/// Unit tests for <see cref="DatasetVersionUtilities"/>: converting a flat dataset into V1 must
/// take the ratings with the images (issue #317).
/// </summary>
public class DatasetVersionUtilitiesTests : IDisposable
{
    private readonly string _datasetPath;

    public DatasetVersionUtilitiesTests()
    {
        _datasetPath = Path.Combine(Path.GetTempPath(), $"DatasetVersionUtilitiesTests_{Guid.NewGuid()}");
        Directory.CreateDirectory(_datasetPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_datasetPath))
        {
            Directory.Delete(_datasetPath, recursive: true);
        }
    }

    private string Media(string name)
    {
        var path = Path.Combine(_datasetPath, name);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    [Fact]
    public async Task EnsureVersionedStructureAsync_MovesLegacyRatingFilesUnchanged()
    {
        Media("a.png");
        File.WriteAllText(Path.Combine(_datasetPath, "a.rating"), "Approved");
        var dataset = DatasetCardViewModel.FromFolder(_datasetPath);

        await DatasetVersionUtilities.EnsureVersionedStructureAsync(dataset);

        var v1 = dataset.GetVersionFolderPath(1);
        File.ReadAllText(Path.Combine(v1, "a.rating")).Should().Be("Approved",
            "moving files is not a rating change, so the folder keeps the legacy format");
        File.Exists(Path.Combine(_datasetPath, "a.rating")).Should().BeFalse();
        new ImageRatingStore().Get(Path.Combine(v1, "a.png")).Should().Be(ImageRatingStatus.Approved);
    }

    [Fact]
    public async Task EnsureVersionedStructureAsync_MovesTheRatingsFile()
    {
        var image = Media("a.png");
        new ImageRatingStore().Set(image, ImageRatingStatus.Rejected);
        var dataset = DatasetCardViewModel.FromFolder(_datasetPath);

        await DatasetVersionUtilities.EnsureVersionedStructureAsync(dataset);

        var v1 = dataset.GetVersionFolderPath(1);
        File.Exists(Path.Combine(_datasetPath, ImageRatingStore.RatingsFileName)).Should().BeFalse();
        new ImageRatingStore().Get(Path.Combine(v1, "a.png")).Should().Be(ImageRatingStatus.Rejected);
    }
}
