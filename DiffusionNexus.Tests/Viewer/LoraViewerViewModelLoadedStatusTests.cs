using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.Viewer;

/// <summary>
/// Covers <see cref="LoraViewerViewModel.BuildLoadedStatus"/> — the actual user-facing string this
/// whole task (#527, Task 12) exists to produce. A pure function, so no view model construction
/// or DI wiring is needed; reached via <c>InternalsVisibleTo("DiffusionNexus.Tests")</c>.
/// </summary>
public sealed class LoraViewerViewModelLoadedStatusTests
{
    /// <summary>
    /// The common case today, on any library that predates #527's classification: nothing to
    /// explain, so the status line stays exactly what it always was — no dangling "· 0 support
    /// assets" noise on every ordinary load.
    /// </summary>
    [Fact]
    public void WhenNothingIsExcludedTheStatusOmitsTheExplanation()
    {
        LoraViewerViewModel.BuildLoadedStatus(modelCount: 293, tileCount: 312, excludedSupportAssets: 0)
            .Should().Be("Loaded 293 models (312 tiles)");
    }

    /// <summary>
    /// The case this task exists for: a legacy library holding support assets. Naming the count
    /// and the kinds is what turns a silent disappearance into an explained one.
    /// </summary>
    [Fact]
    public void WhenSomeAreExcludedTheStatusNamesThem()
    {
        LoraViewerViewModel.BuildLoadedStatus(modelCount: 293, tileCount: 312, excludedSupportAssets: 35)
            .Should().Be("Loaded 293 models (312 tiles) · 35 support assets (VAE, ControlNet, …) not shown");
    }
}
