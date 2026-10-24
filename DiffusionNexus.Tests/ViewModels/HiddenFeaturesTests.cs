using DiffusionNexus.UI.Services;
using DiffusionNexus.UI.ViewModels;
using FluentAssertions;

namespace DiffusionNexus.Tests.ViewModels;

/// <summary>
/// The faint checkbox at the bottom of the sidebar reveals the app's unofficial features. It used
/// to drive only the Diffusion Canvas entry; the LoRA Sorter's "Browse any folder…" now follows it
/// too (#653), so the flag lives in one shared place every feature can read.
/// </summary>
public sealed class HiddenFeaturesTests
{
    [Fact]
    public void TheSidebarCheckboxSwitchesTheSharedFlag()
    {
        var hidden = new HiddenFeatures();
        var main = new DiffusionNexusMainWindowViewModel { HiddenFeatures = hidden };

        main.IsHiddenFeaturesEnabled = true;
        hidden.IsEnabled.Should().BeTrue();

        main.IsHiddenFeaturesEnabled = false;
        hidden.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void HiddenFeaturesAreOffUntilTheCheckboxIsTicked()
    {
        new HiddenFeatures().IsEnabled.Should().BeFalse();
        new DiffusionNexusMainWindowViewModel().IsHiddenFeaturesEnabled.Should().BeFalse();
    }
}
