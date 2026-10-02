using DiffusionNexus.Domain.Models;
using DiffusionNexus.Tests.Helpers;
using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The training-run card's Presentation file count must match what the Presentation tab lists,
/// which skips dot-files such as the per-folder <c>.ratings.json</c> (issue #317).
/// </summary>
public class TrainingRunCardViewModelCountTests : IDisposable
{
    private readonly string _runFolder = Path.Combine(Path.GetTempPath(), "dn-training-run-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_runFolder))
        {
            Directory.Delete(_runFolder, recursive: true);
        }
    }

    [Fact]
    public void RefreshCounts_IgnoresDotFilesInThePresentationFolder()
    {
        var vm = new TrainingRunCardViewModel(
            new TrainingRunInfo { Name = "Run" },
            _runFolder,
            new DatasetEventAggregator(),
            uiScheduler: new ImmediateUiScheduler());
        Directory.CreateDirectory(vm.PresentationFolderPath);
        File.WriteAllBytes(Path.Combine(vm.PresentationFolderPath, "a.png"), [1]);
        File.WriteAllText(Path.Combine(vm.PresentationFolderPath, ImageRatingStore.RatingsFileName), "{}");

        vm.RefreshCounts();

        vm.PresentationFileCount.Should().Be(1);
    }
}
